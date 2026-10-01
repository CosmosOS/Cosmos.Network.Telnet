<h1 align="center">Cosmos Telnet Server 🚀</h1>
<p>
  <a href="https://www.nuget.org/packages/Cosmos.Network.Telnet/" target="_blank">
    <img alt="Version" src="https://img.shields.io/nuget/v/Cosmos.Network.Telnet.svg" />
  </a>
  <a href="https://github.com/CosmosOS/Cosmos.Network.Telnet/blob/main/LICENSE.txt" target="_blank">
    <img alt="License: BSD Clause 3 License" src="https://img.shields.io/badge/license-BSD License-yellow.svg" />
  </a>
</p>

> Cosmos.Network.Telnet is a Telnet server made in C# for the Cosmos operating system construction kit.

It is for **Cosmos Gen3** kernels (NativeAOT). Every client that connects gets a console session of its own, with a thread that runs the shell you give the server, so the same `Console.ReadLine()` and `Console.WriteLine()` code runs for a remote terminal as on the display. It is built on the console sessions of `Cosmos.Kernel.System` and on `System.Net.Sockets`, which a Gen3 kernel runs on its own network stack.

## Usage

Add the package to your kernel .csproj:

```xml
<ItemGroup>
    <PackageReference Include="Cosmos.Network.Telnet" Version="1.0.0" />
</ItemGroup>
```

The kernel needs networking and the scheduler (`CosmosEnableNetwork`, `CosmosEnableScheduler`), the kernel console, since every session's screen is a grid on the display's canvas, and an IP configuration (DHCP or static). `Start()` returns at once: the server accepts connections on a thread of its own, and runs the shell of each on another.

```csharp
using System;
using Cosmos.Network.Telnet;

TelnetServer server = new(session =>
{
    Console.WriteLine("Welcome, " + session.Name);

    while (Console.ReadLine() is { } line && line != "exit")
    {
        Console.WriteLine("You typed: " + line);
    }
})
{
    // Optional: one line per connection, disconnection and failure.
    Log = message => Cosmos.Kernel.System.Diagnostics.Log.WriteString(message + "\n"),
};

server.Start(); // port 23 by default: new TelnetServer(shell, 2323) picks another

// Later, to stop accepting connections; the sessions already open stay connected:
server.Stop();
```

The connection closes when the shell returns. The sessions are listed and numbered with the kernel's others by `SessionManager`, and Alt with a function key shows one on the display, where the local keyboard can type into it.

### Reaching a kernel running in QEMU

With QEMU user-mode networking, forward a host port to the guest's port 23. With the Cosmos CLI:

```sh
cosmos run --nic e1000e --hostfwd tcp::2323-:23
telnet localhost 2323
```

With plain QEMU, the forward is an option of the user-mode NIC: `-nic user,model=e1000e,hostfwd=tcp::2323-:23`.

## Authors

👤 **[@valentinbreiz](https://github.com/valentinbreiz)**

## 🤝 Contributing

Contributions, issues and feature requests are welcome!

Feel free to check [issues page](https://github.com/CosmosOS/Cosmos.Network.Telnet/issues).

## 📝 License

Copyright © 2026 [CosmosOS](https://github.com/CosmosOS).

This project is [BSD Clause 3](https://github.com/CosmosOS/Cosmos.Network.Telnet/blob/main/LICENSE.txt) licensed.
