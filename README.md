# AgentParty

**Transport abstraction for LLM agent communication.**

AgentParty is a .NET 8 library that provides a unified interface for sending and receiving messages to/from LLM agents, regardless of the underlying transport (Telegram, file system, and more in the future). Write your agent logic once, connect any transport with a single line of code.

AgentParty is a **pure transport layer** — it does not handle orchestration, task routing, session management, or conversation context. That responsibility belongs to the consuming code.

## Table of Contents

- [Key Features](#key-features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
  - [Message](#message)
  - [Server](#server)
  - [Client](#client)
  - [Router](#router)
- [Transports](#transports)
  - [File Transport](#file-transport)
  - [Telegram Transport](#telegram-transport)
- [API Reference](#api-reference)
  - [IMessage](#imessage)
  - [IServer](#iserver)
  - [IClient](#iclient)
  - [Router](#router-api)
  - [FileServer / FileClient](#fileserver--fileclient)
  - [TelegramServer](#telegramserver)
- [Lifecycle & Edge Cases](#lifecycle--edge-cases)
  - [Start/Stop Idempotency](#startstop-idempotency)
  - [Router Registration](#router-registration)
  - [Routing Errors](#routing-errors)
  - [Dispose](#dispose)
- [Extending AgentParty](#extending-agentparty)
  - [Custom Transports](#custom-transports)
  - [Custom Message Types](#custom-message-types)
- [Building from Source](#building-from-source)
- [License](#license)

---

## Key Features

- **Unified messaging interface** — `IServer` and `IClient` abstract away transport details.
- **Router** — aggregates multiple servers behind a single event stream with automatic routing.
- **File transport** — shared-folder messaging with atomic writes and `FileSystemWatcher` + polling.
- **Telegram transport** — `Telegram.Bot` adapter out of the box.
- **Idempotent lifecycle** — `Start`/`Stop` are safe to call multiple times.
- **Zero external dependencies** for the file transport; only `Telegram.Bot` for Telegram.

## Installation

AgentParty is distributed via GitHub Packages.

### 1. Add the GitHub Packages NuGet source

```bash
dotnet nuget add source "https://nuget.pkg.github.com/elmortem/index.json" \
  --name "github-elmortem" \
  --username elmortem \
  --password YOUR_GITHUB_PAT
```

The PAT needs the `read:packages` scope.

### 2. Install the package

```bash
dotnet add package AgentParty
```

Or add to your `.csproj`:

```xml
<PackageReference Include="AgentParty" Version="0.1.*" />
```

### Alternative: nuget.config

Place this in the root of your consuming project:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github-elmortem" value="https://nuget.pkg.github.com/elmortem/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github-elmortem>
      <add key="Username" value="elmortem" />
      <add key="ClearTextPassword" value="%GITHUB_TOKEN%" />
    </github-elmortem>
  </packageSourceCredentials>
</configuration>
```

## Quick Start

### Agent side (server)

```csharp
using AgentParty;
using AgentParty.File;
using AgentParty.Telegram;

// Create transport servers
var telegramServer = new TelegramServer(new TelegramServerConfig
{
    BotToken = "123456:ABC-DEF"
});

var fileServer = new FileServer(new FileServerConfig
{
    Directory = "/tmp/agent-pm"
});

// Combine via Router
var router = new Router();
router.Register(telegramServer);
router.Register(fileServer);

// Single event stream for all transports
router.MessageReceived += message =>
{
    Console.WriteLine($"[{message.ClientId}]: {message.Content}");

    // Reply — Router finds the right transport automatically
    router.SendAsync(message.ClientId, new Message
    {
        Content = "Got it, working on it..."
    });
};

await router.StartAsync();
```

### Caller side (file client)

```csharp
using AgentParty;
using AgentParty.File;

var client = new FileClient(new FileClientConfig
{
    Directory = "/tmp/agent-pm",
    ClientId = "coder-agent-001"
});

client.MessageReceived += message =>
{
    Console.WriteLine($"Agent replied: {message.Content}");
};

await client.ConnectAsync();

await client.SendAsync(new Message
{
    Content = "Refactor PlayerController please"
});
```

---

## Core Concepts

### Message

A `Message` is a plain DTO — the unit of exchange between servers and clients. It carries text content, a type tag, and the identity of the client involved.

```csharp
var msg = new Message
{
    Content = "Hello, agent!",
    ClientId = "client-42",       // filled automatically by clients
    Type = "message",             // default; extensible for future use
};
// Id and Timestamp are auto-generated
```

### Server

A **Server** lives inside an agent. It listens for incoming messages from clients and can send messages back to a specific client by `clientId`.

Implementations: `FileServer`, `TelegramServer`, or your own via `IServer`.

### Client

A **Client** connects to a server. It sends messages and receives responses. The `ClientId` is set automatically on outgoing messages.

Implementations: `FileClient`, or your own via `IClient`.

### Router

A **Router** is a composite server. It implements `IServer`, aggregates multiple servers, and maintains a routing table (`clientId -> server`). Your agent subscribes to a single `MessageReceived` event and calls `SendAsync` — the Router resolves the correct transport automatically.

```
                    +-----------+
   Telegram user -->|           |
                    |  Router   |--> MessageReceived (unified stream)
   File client ---->|           |
                    +-----------+
                         |
        SendAsync("clientId", reply)
              |                  |
       TelegramServer      FileServer
       (if client came     (if client came
        from Telegram)      from file)
```

---

## Transports

### File Transport

File-based messaging using a shared directory. Messages are JSON files — one file per message.

#### How it works

```
{Directory}/
  incoming/    <-- client writes, server reads
  outgoing/    <-- server writes, client reads
```

- **Client -> Server:** client writes `{guid}.json` to `incoming/`. Server picks it up via `FileSystemWatcher` + periodic polling fallback, reads it, deletes it.
- **Server -> Client:** server writes `{guid}.json` to `outgoing/` with a `clientId` field. Client filters files by its own `clientId`, reads matching ones, deletes them.

#### Atomic writes

Files are written via a temp file rename pattern to prevent partial reads:

1. Write content to `{guid}.tmp`
2. Rename to `{guid}.json`

The `FileSystemWatcher` filters on `*.json`, so `.tmp` files are never picked up.

#### Ordering

Files are processed in `CreationTimeUtc` order. When a `FileSystemWatcher` event fires, all accumulated files are read with sorting — not just the triggering file.

#### Configuration

```csharp
var server = new FileServer(new FileServerConfig
{
    Directory = "/tmp/agent-pm",   // shared root directory
    PollingIntervalMs = 5000       // fallback polling interval (default: 5000)
});

var client = new FileClient(new FileClientConfig
{
    Directory = "/tmp/agent-pm",   // same shared root directory
    ClientId = "my-client-id",     // unique client identifier
    PollingIntervalMs = 5000       // fallback polling interval (default: 5000)
});
```

#### Multi-client

Multiple clients can use the same directory. The `outgoing/` folder contains files for all clients. Each client reads all files but only processes and deletes those matching its `clientId`. Files for other clients are left untouched.

### Telegram Transport

Adapter over `Telegram.Bot`. Listens for incoming Telegram messages and can reply to users.

#### Configuration

```csharp
var server = new TelegramServer(new TelegramServerConfig
{
    BotToken = "123456:ABC-DEF"
});
```

#### How it works

- `clientId` = Telegram `chatId` as a string.
- Incoming text messages are converted to `IMessage` with `ClientId = chatId.ToString()`.
- `SendAsync(clientId, message)` calls `botClient.SendMessage(long.Parse(clientId), message.Content)`.
- `StartAsync` begins long-polling via `Telegram.Bot`. `StopAsync` cancels it.

#### No TelegramClient

There is no `TelegramClient` implementation — Telegram itself is the client (users write to the bot from the Telegram app).

---

## API Reference

### IMessage

```csharp
public interface IMessage
{
    string Id { get; }           // Unique message ID (GUID)
    string Type { get; }         // Message type ("message" by default)
    string Content { get; }      // Text content
    string ClientId { get; }     // Sender (incoming) or recipient (outgoing)
    DateTime Timestamp { get; }  // Creation time (UTC)
}
```

### IServer

```csharp
public interface IServer : IDisposable
{
    event Action<IMessage> MessageReceived;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
```

| Method | Description |
|---|---|
| `MessageReceived` | Fires when a message arrives from a client. |
| `StartAsync` | Starts listening (polling, watcher, webhook, etc.). Idempotent. |
| `StopAsync` | Stops listening. Idempotent. |
| `SendAsync` | Sends a message to a specific client by `clientId`. Throws `InvalidOperationException` if not running. |

### IClient

```csharp
public interface IClient : IDisposable
{
    event Action<IMessage> MessageReceived;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(IMessage message, CancellationToken cancellationToken = default);
}
```

| Method | Description |
|---|---|
| `MessageReceived` | Fires when a message arrives from the server. |
| `ConnectAsync` | Opens the connection. Idempotent. |
| `DisconnectAsync` | Closes the connection. Idempotent. |
| `SendAsync` | Sends a message to the server. `ClientId` is set automatically. Throws `InvalidOperationException` if not connected. |

### Router API

```csharp
public class Router : IServer
{
    public void Register(IServer server);
    public void Unregister(IServer server);

    // IServer members
    public event Action<IMessage> MessageReceived;
    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StopAsync(CancellationToken cancellationToken = default);
    public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
    public void Dispose();
}
```

| Method | Description |
|---|---|
| `Register(server)` | Adds a server. Subscribes to its `MessageReceived`. If the Router is already running, the server is started immediately. |
| `Unregister(server)` | Removes a server. Unsubscribes events, removes its routes. If the Router is running, the server is stopped. |
| `SendAsync(clientId, msg)` | Looks up the routing table and forwards to the correct server. Throws `KeyNotFoundException` if `clientId` is unknown. |

### FileServer / FileClient

```csharp
// Server
new FileServer(new FileServerConfig
{
    Directory = "/path/to/shared",
    PollingIntervalMs = 5000
});

// Client
new FileClient(new FileClientConfig
{
    Directory = "/path/to/shared",
    ClientId = "unique-client-id",
    PollingIntervalMs = 5000
});
```

### TelegramServer

```csharp
new TelegramServer(new TelegramServerConfig
{
    BotToken = "your-bot-token"
});
```

---

## Lifecycle & Edge Cases

### Start/Stop Idempotency

All `StartAsync`/`StopAsync` (and `ConnectAsync`/`DisconnectAsync`) calls are idempotent:

| Current State | Call | Result |
|---|---|---|
| Stopped | `StartAsync` | Starts, state -> Running |
| Running | `StartAsync` | No-op |
| Running | `StopAsync` | Stops, state -> Stopped |
| Stopped | `StopAsync` | No-op |
| Stopped | `SendAsync` | `InvalidOperationException` |

### Router Registration

Registration behavior depends on the Router's state:

**Register:**
- Router stopped + `Register(server)` -> server is added to the list. `server.StartAsync` will be called on `Router.StartAsync`.
- Router running + `Register(server)` -> server is added and `server.StartAsync` is called immediately.

**Unregister:**
- Router stopped + `Unregister(server)` -> server removed, events unsubscribed.
- Router running + `Unregister(server)` -> server removed, events unsubscribed, `server.StopAsync` called, all routes for that server deleted.

### Routing Errors

- `Router.SendAsync` with an unknown `clientId` throws `KeyNotFoundException`.
- If a server is unregistered, all its routes are removed. Sending to those `clientId`s will throw `KeyNotFoundException`.

### ClientId Collisions

Collisions between transports are unlikely (Telegram uses numeric `chatId`, file clients use GUIDs). If a collision occurs, the routing table overwrites with last-write-wins and the behavior is undefined across transports. This is a deliberate simplicity trade-off.

### Dispose

- `Dispose()` calls `StopAsync` if the server/client is running.
- `Router.Dispose()` stops and disposes **all** registered servers, then clears the routing table.
- To keep a server alive after disposing the Router, call `Unregister(server)` first.

---

## Extending AgentParty

### Custom Transports

Implement `IServer` and/or `IClient` — that's it. Register with Router in one line.

```csharp
public class WebSocketServer : IServer
{
    public event Action<IMessage>? MessageReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Start listening on WebSocket
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Stop listening
    }

    public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
    {
        // Send to the right WebSocket connection
    }

    public void Dispose() { /* cleanup */ }
}

// Usage
router.Register(new WebSocketServer(config));
```

Possible future transports: REST, WebSocket, gRPC, stdin/stdout (console).

### Custom Message Types

The `Type` field in `IMessage` is extensible. The transport layer passes it as-is — interpretation is up to the consumer.

```csharp
// Tool call message
var toolCall = new Message
{
    Type = "tool_call",
    Content = "{\"name\": \"search\", \"args\": {\"query\": \"test\"}}"
};
```

Possible future types: `"tool_call"`, `"tool_result"`, `"status"`, `"error"`.

---

## Building from Source

```bash
git clone https://github.com/elmortem/AgentParty.git
cd AgentParty
dotnet build
dotnet test
```

**Requirements:** .NET 8 SDK.

**Project structure:**

```
AgentParty/
  AgentParty.sln
  src/
    AgentParty/                     # Core library (net8.0)
      IMessage.cs
      IServer.cs
      IClient.cs
      Message.cs
      Router.cs
      File/
        FileServer.cs
        FileClient.cs
        FileServerConfig.cs
        FileClientConfig.cs
      Telegram/
        TelegramServer.cs
        TelegramServerConfig.cs
  tests/
    AgentParty.Tests/               # xUnit tests
```

## License

[MIT](LICENSE)

---

*Designed by a human, implemented by an agent.*
