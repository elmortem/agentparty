# AgentParty

**Transport and rendering abstraction for LLM agent communication.**

AgentParty is a .NET 8 library that provides a unified interface for sending and receiving typed messages to/from LLM agents, regardless of the underlying transport (Telegram, console, file system, and more in the future). Each transport knows how to render typed messages in its own format — Telegram uses inline buttons, console uses text menus, file transport passes JSON as-is.

AgentParty does **not** handle orchestration, task routing, session management, or agent business logic — that responsibility belongs to the consuming code.

## Table of Contents

- [Key Features](#key-features)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
  - [Message Types](#message-types)
  - [Server](#server)
  - [Client](#client)
  - [Router](#router)
- [Message Types Reference](#message-types-reference)
- [Transports](#transports)
  - [File Transport](#file-transport)
  - [Telegram Transport](#telegram-transport)
  - [Console Transport](#console-transport)
- [API Reference](#api-reference)
  - [IMessage](#imessage)
  - [IServer](#iserver)
  - [IClient](#iclient)
  - [Router](#router-api)
  - [Content Models](#content-models)
- [Lifecycle & Edge Cases](#lifecycle--edge-cases)
- [Feed](#feed)
- [Extending AgentParty](#extending-agentparty)
- [Building from Source](#building-from-source)
- [License](#license)

---

## Key Features

- **Feed** — one-way information channel (Telegram channels, file-based feeds) delivered as `FeedReceived` events.
- **Typed messages** — `text`, `choice`, `list`, `notification`, `command`, `response` with structured content models.
- **Transport rendering** — each transport renders typed messages in its native format.
- **Router** — aggregates multiple servers behind a single event stream with automatic routing and command whitelist filtering.
- **File transport** — shared-folder messaging with atomic writes and `FileSystemWatcher` + polling.
- **Telegram transport** — `Telegram.Bot` adapter with inline buttons, callback queries, and typing indicators.
- **Console transport** — stdin/stdout messaging for CLI mode and programmatic pipe communication.
- **Idempotent lifecycle** — `Start`/`Stop` are safe to call multiple times.
- **Zero external dependencies** for file and console transports; only `Telegram.Bot` for Telegram.

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
<PackageReference Include="AgentParty" Version="0.4.*" />
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

### Agent side (server with multiple transports)

```csharp
using AgentParty;
using AgentParty.Content;
using AgentParty.File;
using AgentParty.Telegram;
using AgentParty.Console;

var telegramServer = new TelegramServer(new TelegramServerConfig
{
    BotToken = "123456:ABC-DEF",
    BotName = "PM",
    AllowedCommands = new() { "status", "help" }
});

var consoleServer = new ConsoleServer(new ConsoleServerConfig
{
    ClientId = "console",
    AllowedCommands = new() { "setup", "status", "help", "shutdown" }
});

var fileServer = new FileServer(new FileServerConfig
{
    Directory = "/tmp/agent-pm",
    AllowedCommands = new() { "status" }
});

var router = new Router();
router.Register(telegramServer);
router.Register(consoleServer);
router.Register(fileServer);

router.MessageReceived += async message =>
{
    switch (message.Type)
    {
        case MessageTypes.Message:
            // Regular text from user
            await agentLoop.ProcessMessage(message.ClientId, message.Content);
            break;

        case MessageTypes.Command:
            // Command (already passed whitelist filtering in Router)
            var cmd = CommandContent.Parse(message.Content);
            await commandHandler.Handle(message.ClientId, cmd.Name, cmd.Args);
            break;

        case MessageTypes.Response:
            // Response to a choice/list
            var resp = ResponseContent.Parse(message.Content);
            agentLoop.ProvideResponse(message.ClientId, resp);
            break;
    }
};

await router.StartAsync();
```

### Sending typed messages

```csharp
// Simple text
await router.SendAsync(clientId, new Message
{
    Type = MessageTypes.Text,
    Content = "Created 3 tasks in project Shooter.",
    ClientId = clientId
});

// Choice (expects response)
await router.SendAsync(clientId, new Message
{
    Id = "confirm-001",
    Type = MessageTypes.Choice,
    Content = JsonSerializer.Serialize(new ChoiceContent
    {
        Text = "Proceed?",
        Options = ["Yes", "No", "Discuss"]
    }),
    ClientId = clientId
});

// Informational list (no actions, no response expected)
await router.SendAsync(clientId, new Message
{
    Type = MessageTypes.List,
    Content = JsonSerializer.Serialize(new ListContent
    {
        Title = "Change plan",
        Items = [
            new ListItem { Id = "1", Text = "Create task: Enemy AI" },
            new ListItem { Id = "2", Text = "Create task: Wave spawning" }
        ]
    }),
    ClientId = clientId
});
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
    Console.WriteLine($"Agent replied [{message.Type}]: {message.Content}");
};

await client.ConnectAsync();

await client.SendAsync(new Message
{
    Type = MessageTypes.Message,
    Content = "Refactor PlayerController please"
});
```

---

## Core Concepts

### Message Types

Messages are typed DTOs. The `Type` field determines how the message is rendered by each transport and how the consuming code should interpret it.

| Type | Content | Expects Response | Description |
|------|---------|-----------------|-------------|
| `text` | String (may contain Markdown) | No | Simple text from agent |
| `choice` | JSON (`ChoiceContent`) | Yes (`response`) | Question with options |
| `list` | JSON (`ListContent`) | Only if items have actions | List of items, optionally with per-item actions |
| `notification` | JSON (`NotificationContent`) | No | Soft notification (thinking, attention) |
| `command` | JSON (`CommandContent`) | No | Command, filtered by Router whitelist |
| `response` | JSON (`ResponseContent`) | No | Response to a choice/list, correlated via `to` field |
| `message` | String | No | Incoming text from user (legacy/untyped) |

Constants are available in `MessageTypes` class (`MessageTypes.Text`, `MessageTypes.Choice`, etc.).

### Server

A **Server** lives inside an agent. It listens for incoming messages from clients and can send messages back to a specific client by `clientId`. Each server knows how to render typed messages in its transport format.

Implementations: `FileServer`, `TelegramServer`, `ConsoleServer`, or your own via `IServer`.

### Client

A **Client** connects to a server. It sends messages and receives responses. The `ClientId` is set automatically on outgoing messages.

Implementations: `FileClient`, `ConsoleClient`, or your own via `IClient`.

### Router

A **Router** is a composite server. It implements `IServer`, aggregates multiple servers, and maintains a routing table (`clientId -> server`). It also filters incoming `command` messages against each server's `AllowedCommands` whitelist.

```
                    +-----------+
   Telegram user -->|           |
                    |  Router   |--> MessageReceived (unified stream)
   Console user --->|           |    (commands filtered by whitelist)
                    |           |
   File client ---->|           |
                    +-----------+
                         |
        SendAsync("clientId", reply)
         |           |           |
  TelegramServer ConsoleServer FileServer
  (inline btns)  (text menu)   (JSON as-is)
```

---

## Message Types Reference

### `text`

```json
{
    "type": "text",
    "content": "Here are the **changes**."
}
```

### `choice`

```json
{
    "type": "choice",
    "content": "{\"text\": \"Proceed?\", \"options\": [\"Yes\", \"No\", \"Discuss\"]}"
}
```

Rendering: Telegram — inline buttons. Console — numbered list with input prompt. File — JSON as-is.

### `list`

```json
{
    "type": "list",
    "content": "{\"title\": \"Tasks\", \"items\": [{\"id\": \"1\", \"text\": \"Enemy AI\", \"actions\": [{\"id\": \"create\", \"label\": \"Create\"}]}, {\"id\": \"2\", \"text\": \"Fix bug #42\"}]}"
}
```

Items with actions expect a response. Items without actions are informational. Rendering: Telegram — action items get inline buttons, info items are plain text. Console — numbered list with `number.action` input.

### `notification`

```json
{
    "type": "notification",
    "content": "{\"kind\": \"thinking\"}"
}
```

Kinds: `thinking` (Telegram: typing action, Console: `...`), `attention` (Telegram: `SetMyName` with marker), `attention_clear` (resets name). Unknown kinds are silently ignored.

### `command`

```json
{
    "type": "command",
    "content": "{\"name\": \"status\", \"args\": [\"project1\"]}"
}
```

Filtered by Router through `server.AllowedCommands`. Blocked commands get an error text response sent back to the client.

### `response`

```json
{
    "type": "response",
    "content": "{\"to\": \"msg-id\", \"value\": \"Yes\"}"
}
```

Correlated with the original message via `to`. For list actions: `{"to": "msg-id", "items": [{"id": "1", "action": "create"}]}`.

### `message`

Plain text from user. Default type for incoming messages.

---

## Transports

### File Transport

File-based messaging using a shared directory. Messages are JSON envelopes — one file per message. FileServer/FileClient do **not** render typed messages — they pass JSON as-is.

#### Directory structure

```
{Directory}/
  incoming/    <-- client writes, server reads
  outgoing/    <-- server writes, client reads
  feed/        <-- client writes feed, server reads
```

#### Atomic writes

Files are written via a temp file rename pattern:
1. Write content to `{guid}.tmp`
2. Rename to `{guid}.json`

#### Configuration

```csharp
var server = new FileServer(new FileServerConfig
{
    Directory = "/tmp/agent-pm",
    PollingIntervalMs = 5000,
    AllowedCommands = new() { "status" }
});

var client = new FileClient(new FileClientConfig
{
    Directory = "/tmp/agent-pm",
    ClientId = "my-client-id",
    PollingIntervalMs = 5000
});
```

### Telegram Transport

Adapter over `Telegram.Bot`. Handles incoming text messages and callback queries. Renders typed messages with Markdown, inline buttons, and chat actions.

#### Configuration

```csharp
var server = new TelegramServer(new TelegramServerConfig
{
    BotToken = "123456:ABC-DEF",
    BotName = "PM",
    AttentionMarker = " 📌",
    AllowedCommands = new() { "status", "help" }
});
```

#### Rendering

| Type | Telegram rendering |
|------|-------------------|
| `text` | Markdown via `SendMessage` |
| `choice` | Text + `InlineKeyboardMarkup` |
| `list` (info) | Formatted text with bullet points |
| `list` (actions) | Each action item as a separate message with inline buttons |
| `notification` (thinking) | `SendChatAction(Typing)` |
| `notification` (attention) | `SetMyName(BotName + AttentionMarker)` |
| `notification` (attention_clear) | `SetMyName(BotName)` |

#### Callback queries

When a user presses an inline button, TelegramServer converts the callback into a `response` message with correlation to the original message ID.

#### No TelegramClient

There is no `TelegramClient` — Telegram itself is the client.

### Console Transport

stdin/stdout messaging. `ConsoleServer` listens to stdin and renders outgoing messages to stdout. `ConsoleClient` is the mirror — reads JSON from stdin, writes JSON to stdout (for programmatic pipe communication).

#### Configuration

```csharp
var server = new ConsoleServer(new ConsoleServerConfig
{
    ClientId = "console",
    AllowedCommands = new() { "setup", "status", "help" },
    StartupCommands = ["/setup"]  // sent on start
});

var client = new ConsoleClient(new ConsoleClientConfig
{
    ClientId = "console"
});
```

#### ConsoleServer behavior

- Text input → `message` type message.
- `/command args` → `command` type message (parsed into `CommandContent`).
- `StartupCommands` are sent as `command` messages on `StartAsync`.
- Outgoing messages are rendered via `ConsoleRenderer` (text menu for choices, numbered list for lists, `...` for thinking).

#### ConsoleClient behavior

- Reads JSON envelopes from stdin → fires `MessageReceived`.
- `SendAsync` writes JSON envelope to stdout.
- Designed for programmatic pipe communication, not interactive CLI.

---

## API Reference

### IMessage

```csharp
public interface IMessage
{
    string Id { get; }           // Unique message ID (GUID)
    string Type { get; }         // Message type (see MessageTypes)
    string Content { get; }      // Content (string for text, JSON for structured types)
    string ClientId { get; }     // Sender (incoming) or recipient (outgoing)
    DateTime Timestamp { get; }  // Creation time (UTC)
}
```

### IServer

```csharp
public interface IServer : IDisposable
{
    event Action<IMessage> MessageReceived;
    event Action<IFeedMessage> FeedReceived;
    HashSet<string> AllowedCommands { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
```

| Member | Description |
|---|---|
| `MessageReceived` | Fires when a message arrives from a client. |
| `FeedReceived` | Fires when a feed item arrives (one-way information, no response expected). |
| `AllowedCommands` | Whitelist of allowed commands for this server. Empty = no commands allowed. Router filters incoming `command` messages against this. |
| `StartAsync` | Starts listening. Idempotent. |
| `StopAsync` | Stops listening. Idempotent. |
| `SendAsync` | Sends a typed message to a specific client. The server renders it in its transport format. |

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

### Router API

```csharp
public class Router : IServer
{
    public void Register(IServer server);
    public void Unregister(IServer server);

    public HashSet<string> AllowedCommands { get; }  // union of all servers
    public event Action<IMessage> MessageReceived;
    public event Action<IFeedMessage> FeedReceived;  // aggregated from all servers
    // ... StartAsync, StopAsync, SendAsync, Dispose
}
```

| Method | Description |
|---|---|
| `Register(server)` | Adds a server. On incoming `command` messages, checks `server.AllowedCommands` — blocked commands get an error response. |
| `Unregister(server)` | Removes a server and its routes. |
| `AllowedCommands` | Union of `AllowedCommands` from all registered servers (informational). |
| `SendAsync(clientId, msg)` | Routes to the correct server. Throws `KeyNotFoundException` if unknown. |

### Content Models

All content models live in `AgentParty.Content` namespace and have `Serialize()` / `Parse(string)` methods.

| Class | Fields |
|-------|--------|
| `ChoiceContent` | `Text`, `Options` |
| `ListContent` | `Title?`, `Items` (array of `ListItem`) |
| `ListItem` | `Id`, `Text`, `Details?`, `Actions?` (array of `ListAction`) |
| `ListAction` | `Id`, `Label` |
| `NotificationContent` | `Kind` |
| `CommandContent` | `Name`, `Args?` |
| `ResponseContent` | `To`, `Value?`, `Items?` (array of `ResponseItem`) |
| `ResponseItem` | `Id`, `Action` |

---

## Lifecycle & Edge Cases

### Start/Stop Idempotency

All `StartAsync`/`StopAsync` calls are idempotent:

| Current State | Call | Result |
|---|---|---|
| Stopped | `StartAsync` | Starts |
| Running | `StartAsync` | No-op |
| Running | `StopAsync` | Stops |
| Stopped | `StopAsync` | No-op |
| Stopped | `SendAsync` | `InvalidOperationException` |

### Command Filtering

When Router receives a `command` message from a registered server:
1. Parses `CommandContent.Name`.
2. Checks `server.AllowedCommands`.
3. If allowed — propagates via `MessageReceived`.
4. If blocked — sends a `text` error message back to the client.

### Router Registration

- Router running + `Register(server)` → server started immediately.
- Router running + `Unregister(server)` → server stopped, routes removed.

### Routing Errors

- Unknown `clientId` → `KeyNotFoundException`.
- Unregistered server → its routes are removed.

### ClientId Collisions

Unlikely (Telegram uses numeric `chatId`, file clients use GUIDs, console uses `"console"` or configurable). On collision — last-write-wins with logging.

### Dispose

- `Dispose()` calls `StopAsync` if running.
- `Router.Dispose()` stops and disposes all registered servers.
- To keep a server alive — `Unregister` before disposing the Router.

---

## Feed

Feed is a one-way information channel — data flows from external sources (Telegram channels, file-based feeds) through the server into the agent. Feed messages don't expect a response and don't participate in routing.

### IFeedMessage

```csharp
public interface IFeedMessage
{
    string Content { get; }      // Text content (always a string, no JSON wrapping)
    string? Author { get; }      // Optional author
    DateTime Timestamp { get; }  // Creation time (UTC)
    string Source { get; }       // Source identifier (e.g. Telegram chatId, file-based tag)
}
```

Feed messages have no `Id`, `Type`, or `ClientId` — they are units of information, not protocol messages. `Source` is a transport-level identifier: TelegramServer sets it to `chatId.ToString()`, FileServer deserializes it from JSON. The library does not validate or interpret the value — semantics are defined by the consumer.

### How it works

- **Router** subscribes to `FeedReceived` on all registered servers and aggregates into its own `FeedReceived`.
- **TelegramServer** — configure `FeedChatIds` with Telegram channel/group IDs. Messages from those chats become feed items. Configure `AllowedUserIds` to accept regular messages only from specific users.
- **FileServer** — monitors a `feed/` subdirectory for JSON-serialized `FeedMessage` files.
- **FileClient** — has `SendFeedAsync()` to write feed items to the `feed/` directory.
- **ConsoleServer** — feed not supported (`FeedReceived` is declared but never fired).

### TelegramServer configuration

```csharp
var server = new TelegramServer(new TelegramServerConfig
{
    BotToken = "123456:ABC-DEF",
    BotName = "PM",
    AllowedCommands = new() { "status" },
    AllowedUserIds = new() { 123456789 }, // only these users' messages are processed
    FeedChatIds = new() { -1001234567 }  // messages from this channel go to feed
});
```

### Usage

```csharp
router.FeedReceived += feedMessage =>
{
    logger.Log($"Feed [{feedMessage.Source}]: {feedMessage.Content} (from: {feedMessage.Author ?? "unknown"})");
    agentContext.AddFeedItem(feedMessage);
};
```

### File transport feed

```csharp
// Client writes feed
await fileClient.SendFeedAsync(new FeedMessage
{
    Content = "New article about GameDev",
    Author = "GameDevChannel",
    Source = "dev-feed"
});
```

Directory structure with feed:

```
{Directory}/
  incoming/    <-- client writes messages, server reads
  outgoing/    <-- server writes messages, client reads
  feed/        <-- client writes feed, server reads
```

---

## Extending AgentParty

### Custom Transports

Implement `IServer` and/or `IClient`, including `AllowedCommands` and rendering of typed messages. Register with Router in one line.

### Custom Message Types

Message types are extensible. A transport that doesn't recognize a type will:
- For outgoing: attempt to render `Content` as text (fallback).
- For incoming: pass through via `MessageReceived` as-is.

### Custom Renderers

Each transport has its own renderer (`TelegramRenderer`, `ConsoleRenderer`). Renderers can be replaced via constructor injection for custom rendering behavior.

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
    AgentParty/
      IMessage.cs, IServer.cs, IClient.cs, IFeedMessage.cs
      Message.cs, FeedMessage.cs, Router.cs, MessageTypes.cs
      Content/
        ChoiceContent.cs, ListContent.cs, NotificationContent.cs
        CommandContent.cs, ResponseContent.cs
      File/
        FileServer.cs, FileClient.cs
        FileServerConfig.cs, FileClientConfig.cs
      Console/
        ConsoleServer.cs, ConsoleClient.cs
        ConsoleServerConfig.cs, ConsoleClientConfig.cs
        ConsoleRenderer.cs
      Telegram/
        TelegramServer.cs, TelegramServerConfig.cs
        TelegramRenderer.cs
  tests/
    AgentParty.Tests/
```

## License

[MIT](LICENSE)

---

*Designed by a human, implemented by an agent.*
