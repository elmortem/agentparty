# AgentParty — Technical Design Document

## 1. Overview

**AgentParty** — библиотека для организации коммуникации с LLM-агентами через сменные транспорты. Предоставляет единый интерфейс для отправки и получения сообщений независимо от способа доставки (Telegram, файловая система, в будущем — REST, WebSocket и др.).

Библиотека не занимается оркестрацией, маршрутизацией задач или управлением сессиями — это ответственность потребляющего кода. AgentParty — чистый транспортный слой.

- **Target Framework:** .NET 8
- **Лицензия:** MIT
- **Распространение:** NuGet через GitHub Packages (автопубликация при push в main)
- **Репозиторий:** отдельный на GitHub

## 2. Основные понятия

- **Server** — точка приёма сообщений. Живёт внутри агента. Слушает входящие сообщения от клиентов и может отправлять сообщения конкретному клиенту по его `clientId`.
- **Client** — точка подключения к агенту. Может отправлять сообщения серверу и получать от него ответы.
- **Router** — композитный сервер. Реализует `IServer`, агрегирует несколько серверов. Хранит таблицу маршрутизации `clientId → IServer` и автоматически направляет исходящие сообщения в нужный сервер. Агент подписывается на Router один раз и работает с единой точкой входа.
- **Message** — единица обмена. Чистый DTO без поведения.

## 3. Интерфейсы

### 3.1. IMessage

```csharp
public interface IMessage
{
    string Id { get; }
    string Type { get; }
    string Content { get; }
    string ClientId { get; }
    DateTime Timestamp { get; }
}
```

Поля:
- `Id` — уникальный идентификатор сообщения (GUID).
- `Type` — тип сообщения. На старте — `"message"`. В будущем — `"tool_call"`, `"tool_result"` и т.д.
- `Content` — текстовое содержимое.
- `ClientId` — идентификатор клиента-отправителя (для входящих) или клиента-получателя (для исходящих).
- `Timestamp` — время создания сообщения.

### 3.2. IServer

```csharp
public interface IServer : IDisposable
{
    event Action<IMessage> MessageReceived;
    
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
```

- `MessageReceived` — событие, которое вызывается при получении сообщения от клиента.
- `StartAsync` / `StopAsync` — управление жизненным циклом (запуск polling/watcher, подключение к Telegram и т.д.).
- `SendAsync` — отправка сообщения конкретному клиенту по его `clientId`.

### 3.3. IClient

```csharp
public interface IClient : IDisposable
{
    event Action<IMessage> MessageReceived;
    
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(IMessage message, CancellationToken cancellationToken = default);
}
```

- `MessageReceived` — событие, которое вызывается при получении сообщения от сервера.
- `ConnectAsync` / `DisconnectAsync` — управление подключением.
- `SendAsync` — отправка сообщения серверу. `ClientId` в сообщении заполняется клиентом автоматически.

## 4. Router

```csharp
public class Router : IServer
{
    public void Register(IServer server);
    public void Unregister(IServer server);
    
    // IServer implementation
    public event Action<IMessage> MessageReceived;
    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StopAsync(CancellationToken cancellationToken = default);
    public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
```

### Логика работы

- `Register(server)` — подписывается на `server.MessageReceived`. При получении сообщения от зарегистрированного сервера:
  1. Запоминает в таблице маршрутизации: `message.ClientId → server`.
  2. Пробрасывает сообщение через собственный `MessageReceived`.
- `SendAsync(clientId, message)` — находит сервер по таблице маршрутизации и вызывает `server.SendAsync(clientId, message)`. Если `clientId` не найден — выбрасывает исключение.
- `StartAsync` / `StopAsync` — вызывает соответствующие методы у всех зарегистрированных серверов.

### Коллизии clientId

На практике коллизии маловероятны: Telegram использует числовые chat_id, файловый клиент — GUID. При обнаружении коллизии (два сервера регистрируют одинаковый clientId) — логируем warning и перезаписываем маршрут (last-write-wins). Это сознательный trade-off простоты.

## 5. Реализации

### 5.1. FileServer / FileClient

Файловая реализация использует shared folder для обмена сообщениями. Один файл — одно сообщение в формате JSON (envelope). Файл удаляется после прочтения.

#### Конфигурация

```csharp
public class FileServerConfig
{
    public string Directory { get; set; }          // корневая папка
    public int PollingIntervalMs { get; set; } = 5000; // fallback polling
}

public class FileClientConfig
{
    public string Directory { get; set; }          // та же корневая папка
    public string ClientId { get; set; }           // идентификатор клиента
    public int PollingIntervalMs { get; set; } = 5000;
}
```

#### Структура папки

```
{Directory}/
  incoming/    ← клиент пишет, сервер читает
  outgoing/    ← сервер пишет, клиент читает
```

- **Incoming:** клиент создаёт файл `{guid}.json` в `incoming/`. FileServer подхватывает через FileSystemWatcher (+ periodic polling как fallback), читает, удаляет.
- **Outgoing:** сервер создаёт файл `{guid}.json` в `outgoing/`. Внутри envelope содержит `clientId`. FileClient фильтрует только свои сообщения по `clientId`, читает, удаляет.

#### Формат файла (envelope)

```json
{
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "type": "message",
    "content": "Привет, как дела?",
    "clientId": "client-abc-123",
    "timestamp": "2026-04-14T12:00:00Z"
}
```

#### Атомарность записи

Запись через временный файл с переименованием:
1. Создаём `{guid}.tmp` с содержимым.
2. Переименовываем в `{guid}.json`.

FileSystemWatcher фильтрует по `*.json`, поэтому `.tmp` файлы не подхватываются.

#### Ordering

Файлы обрабатываются в порядке `CreationTime`. При polling — сортировка по дате создания. FileSystemWatcher может доставлять события не в порядке создания, поэтому при получении события через watcher вычитываем все накопившиеся файлы с сортировкой.

#### Мультиклиент (outgoing)

В `outgoing/` лежат файлы для всех клиентов. Каждый клиент при polling/watch читает все файлы, но обрабатывает и удаляет только те, где `clientId` совпадает с его собственным. Чужие файлы не трогает.

### 5.2. TelegramServer

Адаптер над `Telegram.Bot`. Выступает как сервер — слушает входящие сообщения от пользователей Telegram и может отвечать им.

#### Конфигурация

```csharp
public class TelegramServerConfig
{
    public string BotToken { get; set; }
}
```

#### Маппинг

- `clientId` = `chatId.ToString()` (Telegram chat ID).
- Входящие `Update` с текстовым сообщением → `IMessage` с `ClientId = chatId`, `Content = text`, `Type = "message"`.
- `SendAsync(clientId, message)` → `botClient.SendMessage(long.Parse(clientId), message.Content)`.

#### Lifecycle

- `StartAsync` — запускает polling через `TelegramBotClient.ReceiveAsync` или аналогичный механизм.
- `StopAsync` — останавливает polling через `CancellationToken`.

### 5.3. TelegramClient

TelegramClient не реализуется. Telegram и так предоставляет клиент — сам Telegram-мессенджер. Пользователь пишет боту из Telegram, это и есть клиент.

## 6. Структура проекта

```
AgentParty/
  AgentParty.sln
  src/
    AgentParty/                     ← core library (net8.0)
      IMessage.cs
      IServer.cs
      IClient.cs
      Message.cs                    ← реализация IMessage
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
    AgentParty.Tests/               ← xUnit тесты
```

NuGet зависимости:
- `AgentParty` (core): нет внешних зависимостей для файловой реализации.
- `AgentParty` включает Telegram: `Telegram.Bot` >= 22.0.

Если в будущем Telegram-зависимость станет нежелательной — выносим в отдельный пакет `AgentParty.Telegram`.

## 7. Пример использования

### Сторона агента (сервер)

```csharp
// Создаём серверы
var telegramServer = new TelegramServer(new TelegramServerConfig 
{ 
    BotToken = "123456:ABC-DEF" 
});

var fileServer = new FileServer(new FileServerConfig 
{ 
    Directory = "/tmp/agent-pm" 
});

// Объединяем через Router
var router = new Router();
router.Register(telegramServer);
router.Register(fileServer);

// Подписываемся на единый поток
router.MessageReceived += message =>
{
    Console.WriteLine($"[{message.ClientId}]: {message.Content}");
    
    // Отвечаем — Router сам найдёт нужный сервер
    var reply = new Message
    {
        Id = Guid.NewGuid().ToString(),
        Type = "message",
        Content = "Понял, работаю...",
        ClientId = message.ClientId,
        Timestamp = DateTime.UtcNow
    };
    router.SendAsync(message.ClientId, reply);
};

await router.StartAsync();
```

### Сторона вызывающего (файловый клиент)

```csharp
var client = new FileClient(new FileClientConfig
{
    Directory = "/tmp/agent-pm",
    ClientId = "coder-agent-001"
});

client.MessageReceived += message =>
{
    Console.WriteLine($"Агент ответил: {message.Content}");
};

await client.ConnectAsync();

await client.SendAsync(new Message
{
    Id = Guid.NewGuid().ToString(),
    Type = "message",
    Content = "Нужен рефакторинг PlayerController",
    ClientId = "coder-agent-001",
    Timestamp = DateTime.UtcNow
});
```

## 8. Жизненный цикл и краевые случаи

### 8.1. Идемпотентность Start/Stop

`StartAsync` и `StopAsync` у всех реализаций (`IServer`, `IClient`) — **идемпотентны**. Повторный вызов — no-op. Реализации хранят внутренний флаг состояния (`_isRunning`).

| Текущее состояние | Вызов | Результат |
|---|---|---|
| Stopped | `StartAsync` | Запуск, `_isRunning = true` |
| Running | `StartAsync` | No-op |
| Running | `StopAsync` | Остановка, `_isRunning = false` |
| Stopped | `StopAsync` | No-op |
| Stopped | `SendAsync` | `InvalidOperationException` |

### 8.2. Router и регистрация серверов

Router отслеживает своё состояние (`_isRunning`). Поведение `Register`/`Unregister` зависит от этого состояния.

**Register:**
- Router stopped + Register(server) → сервер просто добавляется в список. При последующем `Router.StartAsync` будет вызван `server.StartAsync`.
- Router running + Register(server) → сервер добавляется и `server.StartAsync` вызывается немедленно. Если сервер уже запущен — Start идемпотентен, проблемы нет.

**Unregister:**
- Router stopped + Unregister(server) → сервер удаляется из списка, отписка от событий.
- Router running + Unregister(server) → сервер удаляется, отписка, вызывается `server.StopAsync`. Маршруты этого сервера удаляются из таблицы маршрутизации.

**StartAsync Router:**
- Вызывает `StartAsync` у всех зарегистрированных серверов. Идемпотентность серверов гарантирует, что уже запущенные серверы не пострадают.

**StopAsync Router:**
- Вызывает `StopAsync` у всех зарегистрированных серверов.

### 8.3. Ошибки маршрутизации

- `Router.SendAsync` с неизвестным `clientId` → выбрасывает `KeyNotFoundException` с информативным сообщением.
- Сервер, через который был зарегистрирован клиент, был удалён через `Unregister` → маршрут удалён, `SendAsync` выбросит `KeyNotFoundException`.

### 8.4. Dispose

- `Dispose` вызывает `StopAsync` синхронно если сервер/клиент был запущен.
- Router при `Dispose` вызывает `StopAsync` и `Dispose` у всех зарегистрированных серверов, очищает таблицу маршрутизации. Если нужно сохранить сервер живым — сначала `Unregister`, потом `Dispose` роутера.

## 9. Расширяемость

### Новые транспорты

Для добавления нового транспорта достаточно реализовать `IServer` и/или `IClient`. Регистрация в Router — одна строка. Примеры будущих реализаций:
- `HttpServer` / `HttpClient` — REST-based.
- `WebSocketServer` / `WebSocketClient` — для real-time.
- `ConsoleServer` — stdin/stdout, для отладки.

### Новые типы сообщений

Поле `Type` в `IMessage` зарезервировано для расширения. Транспортный слой передаёт его as-is. Интерпретация — на стороне потребителя. Возможные будущие типы: `"tool_call"`, `"tool_result"`, `"status"`, `"error"`.

## 10. CI/CD: GitHub Packages

### Что делаем

При push в `main` — GitHub Actions автоматически собирает проект, упаковывает в NuGet-пакет и публикует в GitHub Packages. Версия генерируется автоматически.

### Настройка репозитория

1. В настройках репозитория никаких секретов добавлять не нужно — `GITHUB_TOKEN` предоставляется автоматически.
2. Permissions: Settings → Actions → General → Workflow permissions → **Read and write permissions**.

**Важно:** GitHub Packages требует аутентификации даже для публичных пакетов. Для потребления пакета из другого репозитория или локально нужен classic PAT с `read:packages` scope.

### .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <PackageId>AgentParty</PackageId>
    <VersionPrefix>0.1</VersionPrefix> <!-- major.minor меняем руками, patch — автоинкремент из CI -->
    <Authors>elmortem</Authors>
    <Description>Transport abstraction for LLM agent communication</Description>
    <RepositoryUrl>https://github.com/elmortem/AgentParty</RepositoryUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
</Project>
```

Версия пакета формируется как `{VersionPrefix}.{github.run_number}` — например `0.1.1`, `0.1.2`, `0.1.37`. Patch растёт автоматически с каждым push. Для мажорных/минорных изменений — меняем `VersionPrefix` руками.

### GitHub Actions workflow

Файл `.github/workflows/publish.yml`:

```yaml
name: Publish NuGet

on:
  push:
    branches: [ main ]

jobs:
  publish:
    runs-on: ubuntu-latest
    permissions:
      packages: write
      contents: read

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore

      - name: Read version prefix
        run: echo "VERSION_PREFIX=$(grep -oP '(?<=<VersionPrefix>)[^<]+' src/AgentParty/AgentParty.csproj)" >> $GITHUB_ENV

      - name: Build
        run: dotnet build --configuration Release --no-restore -p:Version=${{ env.VERSION_PREFIX }}.${{ github.run_number }}

      - name: Test
        run: dotnet test --configuration Release --no-restore

      - name: Pack
        run: dotnet pack src/AgentParty/AgentParty.csproj --configuration Release --no-build --output ./nupkg -p:Version=${{ env.VERSION_PREFIX }}.${{ github.run_number }}

      - name: Publish to GitHub Packages
        run: dotnet nuget push ./nupkg/*.nupkg --source "https://nuget.pkg.github.com/elmortem/index.json" --api-key ${{ secrets.GITHUB_TOKEN }} --skip-duplicate
```

### Подключение пакета в потребляющих проектах

Один раз добавить source (глобально или в `nuget.config`):

```bash
dotnet nuget add source "https://nuget.pkg.github.com/elmortem/index.json" \
  --name "github-elmortem" \
  --username elmortem \
  --password YOUR_GITHUB_PAT
```

PAT (Personal Access Token) нужен с правом `read:packages`. После этого:

```bash
dotnet add package AgentParty
```

Альтернативно — `nuget.config` в корне потребляющего проекта:

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

## 11. Ограничения и допущения

- Библиотека не управляет сессиями. Группировка сообщений в сессии, контексты, диалоги — ответственность потребляющего кода.
- Файловый транспорт работает на доверии: клиент сам фильтрует свои сообщения, защиты от чтения чужих нет.
- FileSystemWatcher может пропускать события при высокой нагрузке — periodic polling компенсирует.
- Ordering гарантируется в пределах одного транспорта. Между разными транспортами (Telegram + File) порядок не гарантирован.
- Коллизии `clientId` между транспортами маловероятны, но возможны. При коллизии — last-write-wins с логированием.
