// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.System.Keyboard;
using Cosmos.Kernel.System.Sessions;

namespace Cosmos.Network.Telnet;

/// <summary>
/// The console session of one Telnet connection. What is written goes to
/// the session's screen, like any session's, and to the client as the
/// characters and VT100 sequences that make its terminal show the same
/// thing; the keys come from what the client sends.
/// </summary>
/// <remarks>
/// <para>The terminal follows the screen by being told each change as it
/// happens, so the two agree on where every line wraps: the screen wraps
/// as soon as a line is full, a terminal only when the next character
/// comes, so a full line is ended explicitly. A cursor move sends the
/// screen's cursor position, and a colour is sent before the first
/// character written in it.</para>
/// <para>Only the server's thread touches the connection, since the
/// kernel's sockets are not safe to use from two threads at once. The
/// thread the shell runs on only appends to the pending output, in the
/// hooks that report each change; the server's thread sends that output,
/// and reads what the client sends into keys for the shell.</para>
/// </remarks>
internal sealed class TelnetSession : ConsoleSession
{
    /// <summary>Screen size until the client reports its window: the classic terminal's.</summary>
    internal const int DefaultCols = 80;

    /// <summary>Screen rows until the client reports its window.</summary>
    internal const int DefaultRows = 24;

    /// <summary>Bytes taken from the connection at a time.</summary>
    private const int ReceiveChunkSize = 512;

    /// <summary>Initial size of a buffer that holds output until it is sent.</summary>
    private const int InitialOutputCapacity = 256;

    private readonly TcpClient _client;
    private readonly Socket _socket;
    private readonly byte[] _received = new byte[ReceiveChunkSize];
    private readonly TelnetDecoder _decoder = new();
    private readonly List<KeyEvent> _decodedKeys = [];
    private readonly List<byte> _replies = [];

    /// <summary>When the shell starts even if the client never reported its window, in <see cref="Stopwatch"/> ticks.</summary>
    private readonly long _startDeadline;

    // What the hooks append to, and the buffer the server's thread swaps in
    // for it when it takes the output to send.
    private OutputBuffer _output = new(InitialOutputCapacity);
    private OutputBuffer _spare = new(InitialOutputCapacity);

    // The colours the terminal draws in; null is the terminal's default.
    private ConsoleColor? _sentForeground;
    private ConsoleColor? _sentBackground;

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    public override bool IsRemote => true;

    /// <summary>Whether the server started the session's shell, or gave up on it.</summary>
    internal bool IsStarted { get; set; }

    /// <summary>
    /// Whether the shell should start: the client reported its window, so
    /// the screen has the terminal's size before anything is written, or it
    /// took too long to.
    /// </summary>
    internal bool IsReadyToStart => _decoder.WindowColumns != 0 || Stopwatch.GetTimestamp() >= _startDeadline;

    /// <summary>ANSI colour codes by <see cref="ConsoleColor"/>; a background's code is ten more than its foreground's.</summary>
    private static ReadOnlySpan<byte> AnsiForegrounds => [30, 34, 32, 36, 31, 35, 33, 37, 90, 94, 92, 96, 91, 95, 93, 97];

    /// <summary>Creates the session of an accepted connection.</summary>
    /// <param name="client">The connection.</param>
    /// <param name="negotiationTimeoutMs">How long the client may take to report its window size before the shell starts anyway.</param>
    /// <exception cref="InvalidOperationException">The kernel console is not initialized.</exception>
    internal TelnetSession(TcpClient client, int negotiationTimeoutMs)
        : base(DefaultCols, DefaultRows)
    {
        _client = client;
        _socket = client.Client;
        _startDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * negotiationTimeoutMs / 1000;

        IPEndPoint remote = (IPEndPoint)(_socket.RemoteEndPoint ?? throw new InvalidOperationException("An accepted connection has no remote end point."));
        // ToString() spelled out: interpolation formats an IPAddress through
        // ISpanFormattable, which the Cosmos socket plugs do not cover.
        Name = $"{remote.Address.ToString()}:{remote.Port}";
    }

    /// <summary>
    /// Opens the option negotiation: the server echoes and suppresses
    /// go-ahead, which puts the client in character-at-a-time mode, and asks
    /// for the window size. The server's thread.
    /// </summary>
    internal void Negotiate()
    {
        _decoder.Negotiate(_replies);
        SendReplies();
    }

    /// <summary>
    /// Takes in what the client sent: queues its keys for the shell, answers
    /// its negotiation, and resizes the screen to its window. Closes the
    /// session once the client hung up. The server's thread.
    /// </summary>
    /// <returns>Whether anything arrived, or the connection ended.</returns>
    internal bool Receive()
    {
        bool received = false;
        int available;
        while ((available = _socket.Available) > 0)
        {
            int count = _socket.Receive(_received, 0, Math.Min(available, _received.Length), SocketFlags.None);
            if (count <= 0)
            {
                break;
            }

            _decoder.Decode(_received.AsSpan(0, count), _decodedKeys, _replies);
            received = true;
        }

        foreach (KeyEvent key in _decodedKeys)
        {
            EnqueueInput(key);
        }

        _decodedKeys.Clear();
        SendReplies();

        int cols = _decoder.WindowColumns;
        int rows = _decoder.WindowRows;
        if (cols > 0 && (cols != Cols || rows != Rows))
        {
            Resize(cols, rows);
        }

        // Readable with nothing to read: the client closed its end.
        if (!received && _socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0)
        {
            Close();
            return true;
        }

        return received;
    }

    /// <summary>Sends the output appended since the last call. The server's thread.</summary>
    /// <returns>Whether there was any.</returns>
    internal bool SendOutput()
    {
        // The hooks append with interrupts masked, and the kernel schedules
        // its threads on one CPU, so no append is half done when the buffers
        // are swapped: each one went whole to the buffer taken here, or goes
        // whole to the one swapped in.
        OutputBuffer pending = Interlocked.Exchange(ref _output, _spare);
        try
        {
            if (pending.Length == 0)
            {
                return false;
            }

            _socket.Send(pending.Bytes, 0, pending.Length, SocketFlags.None);
            return true;
        }
        finally
        {
            pending.Length = 0;
            _spare = pending;
        }
    }

    /// <summary>
    /// Tells the client why it is turned away, before it has a shell, and
    /// closes the session. The server's thread; the line goes out with the
    /// session's last output.
    /// </summary>
    internal void Refuse(string reason)
    {
        Append(reason);
        Append("\r\n");
        Close();
    }

    /// <summary>
    /// Sends what the closed session still had to say, then closes the
    /// connection without waiting for the client to acknowledge it. The
    /// server's thread; never throws, since it ends failures too.
    /// </summary>
    internal void Disconnect()
    {
        try
        {
            SendOutput();
        }
        catch (Exception)
        {
            // The client is gone; there is no one left to tell.
        }

        try
        {
            // No linger: the close finishes in the background, so a client
            // that does not answer cannot hold up the other sessions.
            _socket.Close(0);
            _client.Close();
        }
        catch (Exception)
        {
            // Closed already, or broken: either way it is gone.
        }
    }

    /// <inheritdoc/>
    protected override void OnWritten(char value, bool wrapped)
    {
        switch (value)
        {
            case '\n':
                // The screen's line feed also returns the carriage.
                Append("\r\n");
                return;

            case '\r':
                // A carriage return alone is CR NUL on a Telnet connection (RFC 854).
                Append("\r\0");
                return;

            case '\b':
                // The screen moved back, possibly to the end of the line
                // above, and blanked that cell: blank it at the position the
                // screen gives, which a terminal's own backspace would not
                // reach across a line.
                SyncColors();
                AppendCursorPosition();
                Append(' ');
                AppendCursorPosition();
                return;
        }

        SyncColors();
        AppendCharacter(value);
        if (wrapped)
        {
            Append("\r\n");
        }
    }

    /// <inheritdoc/>
    protected override void OnCleared()
    {
        SyncColors();
        Append("\x1b[2J\x1b[H");
    }

    /// <inheritdoc/>
    protected override void OnCursorMoved() => AppendCursorPosition();

    /// <inheritdoc/>
    protected override void OnCursorVisibilityChanged(bool visible) => Append(visible ? "\x1b[?25h" : "\x1b[?25l");

    /// <summary>Sends the answers to the client's negotiation.</summary>
    private void SendReplies()
    {
        if (_replies.Count == 0)
        {
            return;
        }

        _socket.Send(CollectionsMarshal.AsSpan(_replies), SocketFlags.None);
        _replies.Clear();
    }

    /// <summary>Sends the colours the session writes in, if the terminal does not have them yet.</summary>
    private void SyncColors()
    {
        ConsoleColor? foreground = ExplicitForeground;
        ConsoleColor? background = ExplicitBackground;
        if (foreground == _sentForeground && background == _sentBackground)
        {
            return;
        }

        // Reset first, so a colour put back to the default is the
        // terminal's default rather than white on black.
        Append("\x1b[0");
        if (foreground is { } foregroundColor)
        {
            Append(';');
            AppendNumber(AnsiForegrounds[(int)foregroundColor]);
        }

        if (background is { } backgroundColor)
        {
            Append(';');
            AppendNumber(AnsiForegrounds[(int)backgroundColor] + 10);
        }

        Append('m');
        _sentForeground = foreground;
        _sentBackground = background;
    }

    /// <summary>Moves the terminal's cursor to the screen's.</summary>
    private void AppendCursorPosition()
    {
        Append("\x1b[");
        AppendNumber(CursorTop + 1);
        Append(';');
        AppendNumber(CursorLeft + 1);
        Append('H');
    }

    /// <summary>Appends a character as UTF-8. A control character, which the screen draws as a glyph but a terminal would act on, goes as a space.</summary>
    private void AppendCharacter(char value)
    {
        if (value < 0x20 || value == 0x7F)
        {
            Append(' ');
        }
        else if (value < 0x80)
        {
            Append((byte)value);
        }
        else if (value < 0x800)
        {
            Append((byte)(0xC0 | (value >> 6)));
            Append((byte)(0x80 | (value & 0x3F)));
        }
        else if (char.IsSurrogate(value))
        {
            Append('?');
        }
        else
        {
            Append((byte)(0xE0 | (value >> 12)));
            Append((byte)(0x80 | ((value >> 6) & 0x3F)));
            Append((byte)(0x80 | (value & 0x3F)));
        }
    }

    private void AppendNumber(int value)
    {
        Span<byte> digits = stackalloc byte[10];
        int count = 0;
        do
        {
            digits[count++] = (byte)('0' + value % 10);
            value /= 10;
        }
        while (value > 0);

        while (count > 0)
        {
            Append(digits[--count]);
        }
    }

    /// <summary>Appends ASCII text.</summary>
    private void Append(string text)
    {
        foreach (char c in text)
        {
            Append((byte)c);
        }
    }

    private void Append(char value) => Append((byte)value);

    private void Append(byte value)
    {
        if (!IsClosed)
        {
            _output.Append(value);
        }
    }

    /// <summary>Bytes held until the server's thread sends them.</summary>
    private sealed class OutputBuffer(int capacity)
    {
        public byte[] Bytes = new byte[capacity];

        public int Length;

        public void Append(byte value)
        {
            if (Length == Bytes.Length)
            {
                Array.Resize(ref Bytes, Bytes.Length * 2);
            }

            Bytes[Length++] = value;
        }
    }
}
