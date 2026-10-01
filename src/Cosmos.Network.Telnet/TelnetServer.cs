// This code is licensed under the BSD 3-Clause license (see LICENSE.txt for details)

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Cosmos.Kernel.System;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Sessions;

namespace Cosmos.Network.Telnet;

/// <summary>
/// A Telnet server: every client that connects gets a console session of
/// its own, and a thread bound to it that runs the shell the server was
/// given, so <see cref="Console"/> on that thread reads the client's keys
/// and writes to its terminal. The session is listed and numbered with the
/// others by <see cref="SessionManager"/>, and can be shown on the display.
/// </summary>
/// <remarks>
/// <para>The server has a thread of its own, which accepts connections and
/// carries every session's traffic: it sends what each shell wrote, and
/// queues the keys each client typed. It runs until the server is stopped
/// and its last session is closed.</para>
/// <para>Telnet sends everything in the clear, password included: serve it
/// on a network you trust. A session's screen lives on the display's
/// canvas, so the server needs the kernel console.</para>
/// </remarks>
public sealed class TelnetServer
{
    /// <summary>The port Telnet is served on by default.</summary>
    public const ushort DefaultPort = 23;

    /// <summary>How long a new connection may take to report its window size before its shell starts anyway, in milliseconds.</summary>
    private const int NegotiationTimeoutMs = 500;

    /// <summary>How long the server's thread sleeps when no client had anything for it, in milliseconds.</summary>
    private const int IdleSleepMs = 10;

    private readonly Action<ConsoleSession> _shell;

    /// <summary>The connections served, their shell started or not. Only the server's thread touches it.</summary>
    private readonly List<TelnetSession> _sessions = [];

    /// <summary>Whether the server should listen: set by <see cref="Start"/>, cleared by <see cref="Stop"/>.</summary>
    private volatile bool _listenRequested;

    private volatile bool _listening;

    /// <summary>1 while the server's thread runs, so there is never a second one.</summary>
    private int _threadRunning;

    /// <summary>The TCP port the server listens on.</summary>
    public ushort Port { get; }

    /// <summary>Whether the server accepts connections. It goes false shortly after <see cref="Stop"/>.</summary>
    public bool IsRunning => _listening;

    /// <summary>Receives a line for every connection, disconnection and failure, when set.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Creates a server; <see cref="Start"/> starts it.</summary>
    /// <param name="shell">What each connection runs, on a thread bound to its session; the connection is closed when it returns.</param>
    /// <param name="port">The TCP port to listen on.</param>
    public TelnetServer(Action<ConsoleSession> shell, ushort port = DefaultPort)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentOutOfRangeException.ThrowIfZero(port);

        _shell = shell;
        Port = port;
    }

    /// <summary>Starts accepting connections, on the server's own thread.</summary>
    /// <exception cref="InvalidOperationException">The server already runs, networking or the scheduler is compiled out, or there is no kernel console.</exception>
    public void Start()
    {
        if (!KernelFeatures.Network)
        {
            throw new InvalidOperationException("Networking is disabled. Set CosmosEnableNetwork=true in your csproj to enable it.");
        }

        if (!KernelFeatures.Scheduler)
        {
            throw new InvalidOperationException("The Telnet server runs its sessions on threads. Set CosmosEnableScheduler=true in your csproj to enable it.");
        }

        if (!KernelConsole.IsInitialized)
        {
            throw new InvalidOperationException("The Telnet server's sessions draw on the kernel console, which is not initialized.");
        }

        if (_listenRequested)
        {
            throw new InvalidOperationException($"The Telnet server already runs on port {Port}.");
        }

        _listenRequested = true;

        // A thread still serving the sessions of an earlier run listens
        // again itself; only one thread may ever touch the sockets.
        if (Interlocked.CompareExchange(ref _threadRunning, 1, 0) == 0)
        {
            try
            {
                new Thread(Serve).Start();
            }
            catch
            {
                _listenRequested = false;
                Volatile.Write(ref _threadRunning, 0);
                throw;
            }
        }
    }

    /// <summary>
    /// Stops accepting connections. The sessions already open stay open;
    /// <see cref="ConsoleSession.Close"/> ends one.
    /// </summary>
    public void Stop() => _listenRequested = false;

    private void WriteLog(string message) => Log?.Invoke($"[Telnet] {message}");

    /// <summary>
    /// The server's thread: listens while asked to, and carries the traffic
    /// of every session, until it is not asked to listen and has no session
    /// left.
    /// </summary>
    private void Serve()
    {
        TcpListener? listener = null;
        try
        {
            while (true)
            {
                if (_listenRequested && listener is null)
                {
                    listener = StartListening();
                }
                else if (!_listenRequested && listener is not null)
                {
                    StopListening(listener);
                    listener = null;
                }

                if (listener is null && _sessions.Count == 0)
                {
                    // Nothing to serve: the thread ends, unless a Start()
                    // that found it still running asked it to listen
                    // meanwhile.
                    Volatile.Write(ref _threadRunning, 0);
                    if (!_listenRequested || Interlocked.CompareExchange(ref _threadRunning, 1, 0) != 0)
                    {
                        return;
                    }

                    continue;
                }

                bool busy = false;
                if (listener is not null)
                {
                    busy = AcceptPending(listener);
                }

                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    busy |= ServeSession(_sessions[i]);
                }

                if (!busy)
                {
                    Thread.Sleep(IdleSleepMs);
                }
            }
        }
        catch (Exception exception)
        {
            // Not expected: a connection's failure is caught where it
            // happens. Without the thread no session has a connection, so
            // they all hang up.
            WriteLog($"Server stopped by {exception.GetType().Name}: {exception.Message}");
            _listenRequested = false;
            if (listener is not null)
            {
                StopListening(listener);
            }

            foreach (TelnetSession session in _sessions)
            {
                session.Close();
                session.Disconnect();
            }

            _sessions.Clear();
            Volatile.Write(ref _threadRunning, 0);
        }
    }

    /// <returns>The listener, or null when the port could not be listened on, and the server stops asking to.</returns>
    private TcpListener? StartListening()
    {
        TcpListener listener = new(IPAddress.Any, Port);
        try
        {
            listener.Start();
        }
        catch (Exception exception)
        {
            WriteLog($"Cannot listen on port {Port}: {exception.Message}");
            _listenRequested = false;
            return null;
        }

        _listening = true;
        WriteLog($"Listening on port {Port}");
        return listener;
    }

    private void StopListening(TcpListener listener)
    {
        _listening = false;
        try
        {
            listener.Stop();
        }
        catch (Exception exception)
        {
            WriteLog($"Closing port {Port} failed: {exception.Message}");
        }

        WriteLog($"Stopped listening on port {Port}");
    }

    /// <summary>Takes a connection that waits to be accepted, if any.</summary>
    /// <returns>Whether one was.</returns>
    private bool AcceptPending(TcpListener listener)
    {
        TcpClient client;
        try
        {
            if (!listener.Pending())
            {
                return false;
            }

            client = listener.AcceptTcpClient();
        }
        catch (Exception exception)
        {
            WriteLog($"Accepting a connection failed: {exception.Message}");
            return true;
        }

        TelnetSession? session = null;
        try
        {
            session = new TelnetSession(client, NegotiationTimeoutMs);
            session.Negotiate();
            _sessions.Add(session);
            WriteLog($"{session.Name} connected");
        }
        catch (Exception exception)
        {
            // A client gone before its negotiation costs its own connection,
            // never the server.
            WriteLog($"Connection dropped while accepted: {exception.Message}");
            if (session is null)
            {
                client.Close();
            }
            else
            {
                session.Close();
                session.Disconnect();
            }
        }

        return true;
    }

    /// <summary>
    /// Carries one session's traffic, starts its shell once the client has
    /// negotiated, and drops it once it is closed.
    /// </summary>
    /// <returns>Whether the session had anything to do.</returns>
    private bool ServeSession(TelnetSession session)
    {
        bool busy;
        try
        {
            busy = session.Receive();
            if (!session.IsStarted && !session.IsClosed && session.IsReadyToStart)
            {
                StartShell(session);
                busy = true;
            }

            busy |= session.SendOutput();
        }
        catch (Exception exception)
        {
            // A failure of this connection ends this session only, never the
            // server or the others.
            WriteLog($"{session.Name} dropped by {exception.GetType().Name}: {exception.Message}");
            session.Close();
            busy = true;
        }

        if (session.IsClosed)
        {
            _sessions.Remove(session);
            session.Disconnect();
            WriteLog($"{session.Name} disconnected");
        }

        return busy;
    }

    /// <summary>Lists a negotiated session with the others, and starts the shell on it.</summary>
    private void StartShell(TelnetSession session)
    {
        session.IsStarted = true;
        if (!SessionManager.TryRegister(session))
        {
            session.Refuse($"All {SessionManager.MaxSessions} console sessions are in use.");
            return;
        }

        WriteLog($"{session.Name} runs as session {session.Id}");
        try
        {
            SessionManager.Start(session, () => _shell(session));
        }
        catch (InvalidOperationException exception)
        {
            WriteLog($"Session {session.Id} did not start: {exception.Message}");
            session.Close();
        }
    }
}
