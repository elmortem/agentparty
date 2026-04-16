# AgentParty — Technical Design Document

## 1. Overview

**AgentParty** — библиотека для организации коммуникации с LLM-агентами через сменные транспорты. Предоставляет единый интерфейс для отправки и получения типизированных сообщений независимо от способа доставки (Telegram, консоль, файловая система, в будущем — REST, WebSocket и др.).

Библиотека отвечает за транспорт и формат отображения. Каждый транспорт знает, как рендерить типизированные сообщения в своём формате (Telegram — inline-кнопками, Console — текстовым меню, File — JSON-конвертом).

Библиотека **не занимается** оркестрацией, маршрутизацией задач, управлением сессиями или бизнес-логикой агентов — это ответственность потребляющего кода.

- **Target Framework:** .NET 8
- **Лицензия:** MIT
- **Распространение:** NuGet через GitHub Packages (автопубликация при push в main)
- **Репозиторий:** отдельный на GitHub

## 2. Основные понятия

- **Server** — точка приёма сообщений. Живёт внутри агента. Слушает входящие сообщения от клиентов и может отправлять сообщения конкретному клиенту по его `clientId`. Знает, как рендерить типизированные сообщения в формате своего транспорта.
- **Client** — точка подключения к агенту. Может отправлять сообщения серверу и получать от него ответы. Знает, как рендерить входящие типизированные сообщения и собирать ответы пользователя.
- **Router** — композитный сервер. Реализует `IServer`, агрегирует несколько серверов. Хранит таблицу маршрутизации `clientId → IServer` и автоматически направляет исходящие сообщения в нужный сервер. Фильтрует команды по whitelist'у серверов. Агент подписывается на Router один раз и работает с единой точкой входа.
- **Message** — единица обмена. Типизированный DTO без поведения.

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
- `Type` — тип сообщения (см. раздел 4).
- `Content` — содержимое. Для `"text"` — строка. Для остальных типов — JSON.
- `ClientId` — идентификатор клиента-отправителя (для входящих) или клиента-получателя (для исходящих).
- `Timestamp` — время создания сообщения (UTC).

### 3.2. IServer

```csharp
public interface IServer : IDisposable
{
    event Action<IMessage> MessageReceived;
    
    HashSet<string> AllowedCommands { get; }
    
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
```

- `MessageReceived` — событие, которое вызывается при получении сообщения от клиента.
- `AllowedCommands` — whitelist разрешённых команд для этого сервера. Пустой — команды запрещены. Router фильтрует входящие `"command"` сообщения по этому списку.
- `StartAsync` / `StopAsync` — управление жизненным циклом.
- `SendAsync` — отправка сообщения конкретному клиенту. Сервер рендерит типизированное сообщение в формате своего транспорта.

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

- `MessageReceived` — событие, которое вызывается при получении сообщения от сервера. Клиент рендерит типизированное сообщение и может собрать ответ пользователя.
- `ConnectAsync` / `DisconnectAsync` — управление подключением.
- `SendAsync` — отправка сообщения серверу. `ClientId` в сообщении заполняется клиентом автоматически.

## 4. Типы сообщений

### 4.1. `"text"` — простой текст

Content — строка. Может содержать Markdown.

```json
{
    "id": "...",
    "type": "text",
    "content": "Привет! Вот **список** изменений.",
    "clientId": "...",
    "timestamp": "..."
}
```

Рендеринг:
- **TelegramServer** — Markdown через Telegram Bot API.
- **ConsoleServer/Client** — текст как есть (Markdown strip или as-is).
- **FileServer/Client** — JSON конверт.

### 4.2. `"choice"` — вопрос с вариантами выбора

Content — JSON. Ожидает ответ (`"response"`).

```json
{
    "id": "abc-123",
    "type": "choice",
    "content": "{\"text\": \"Выполняем?\", \"options\": [\"Да\", \"Нет\", \"Обсудим\"]}",
    "clientId": "...",
    "timestamp": "..."
}
```

Рендеринг:
- **TelegramServer** — текст + inline-кнопки.
- **ConsoleServer/Client** — текст + нумерованный список, ожидание ввода номера.
- **FileServer/Client** — JSON конверт as-is.

### 4.3. `"list"` — список элементов

Content — JSON. Каждый элемент может иметь свои действия (или не иметь). Если хотя бы у одного элемента есть действия — ожидает ответ (`"response"`). Если ни у одного нет действий — информационный, ответа не ждёт.

```json
{
    "id": "list-456",
    "type": "list",
    "content": "{\"title\": \"Задачи на сегодня\", \"items\": [{\"id\": \"1\", \"text\": \"Сделать AI врагов\", \"details\": \"SHOOTER-045, приоритет high\", \"actions\": [{\"id\": \"create\", \"label\": \"Создать\"}, {\"id\": \"skip\", \"label\": \"Пропустить\"}]}, {\"id\": \"2\", \"text\": \"Пофиксить баг #42\"}, {\"id\": \"3\", \"text\": \"Рефакторинг\", \"actions\": [{\"id\": \"discuss\", \"label\": \"Обсудить\"}]}]}",
    "clientId": "...",
    "timestamp": "..."
}
```

Структура Content:
```
{
    "title": string?,          // заголовок списка (опционален)
    "items": [
        {
            "id": string,      // идентификатор элемента
            "text": string,    // основной текст
            "details": string?, // дополнительные детали (опционально)
            "actions": [       // действия для этого элемента (опционально)
                {
                    "id": string,    // идентификатор действия
                    "label": string  // текст кнопки/пункта
                }
            ]?
        }
    ]
}
```

Рендеринг:
- **TelegramServer** — сообщение со списком. Элементы с actions рендерятся как карточки с inline-кнопками. Элементы без actions — просто текстом.
- **ConsoleServer/Client** — нумерованный список. Для элементов с actions — предложение ввести `номер.действие`.
- **FileServer/Client** — JSON конверт as-is.

Паттерн "confirm plan": отправить `"list"` без actions (информационный список изменений), затем `"choice"` с вариантами "Да / Нет / Обсудим". Так реализуются подтверждения без специального типа сообщений.

### 4.4. `"notification"` — мягкая нотификация

Content — JSON. Не ждёт ответа. Каждый транспорт решает, как отобразить (или проигнорировать).

```json
{
    "id": "...",
    "type": "notification",
    "content": "{\"kind\": \"thinking\"}",
    "clientId": "...",
    "timestamp": "..."
}
```

Виды (`kind`):
- `"thinking"` — агент обрабатывает запрос. TelegramServer показывает typing action. ConsoleServer/Client может показать "...". FileServer — игнорирует.
- `"attention"` — у агента есть что-то требующее внимания (например, накопились вопросы). TelegramServer может менять имя бота (`setMyName`). ConsoleServer/Client — маркер в prompt. FileServer — файл-флаг.
- `"attention_clear"` — сброс attention-нотификации.

Транспорт, не знающий конкретный `kind`, просто игнорирует сообщение.

### 4.5. `"command"` — команда

Content — JSON. Фильтруется Router'ом через whitelist сервера-источника.

```json
{
    "id": "...",
    "type": "command",
    "content": "{\"name\": \"status\", \"args\": [\"project1\"]}",
    "clientId": "...",
    "timestamp": "..."
}
```

Структура Content:
```
{
    "name": string,      // имя команды
    "args": string[]?    // аргументы (опционально)
}
```

Команды не обрабатываются транспортным слоем. Router проверяет whitelist, и если команда разрешена — пробрасывает через `MessageReceived`. Интерпретация — на стороне потребляющего кода (агента).

### 4.6. `"response"` — ответ на сообщение

Content — JSON. Корреляция с исходным сообщением через поле `to`.

```json
{
    "id": "...",
    "type": "response",
    "content": "{\"to\": \"abc-123\", \"value\": \"Да\"}",
    "clientId": "...",
    "timestamp": "..."
}
```

Для `"choice"`:
```
{"to": "msg-id", "value": "выбранный вариант"}
```

Для `"list"` (с actions):
```
{"to": "msg-id", "items": [{"id": "1", "action": "create"}, {"id": "3", "action": "discuss"}]}
```

Для свободного текстового ответа (пользователь ответил не кнопкой, а текстом):
```
{"to": "msg-id", "value": "произвольный текст"}
```

Не важно, кто отвечает — человек через Telegram, другой агент через FileClient, или stdin через ConsoleClient. Протокол одинаковый.

### 4.7. `"message"` — legacy / нетипизированное сообщение

Для обратной совместимости и для случаев, когда клиент отправляет обычное текстовое сообщение агенту (пользователь написал в чат). Это входящее сообщение, не путать с `"text"` (исходящее от агента).

```json
{
    "id": "...",
    "type": "message",
    "content": "Покажи задачи по shooter",
    "clientId": "...",
    "timestamp": "..."
}
```

## 5. Router

```csharp
public class Router : IServer
{
    public void Register(IServer server);
    public void Unregister(IServer server);
    
    // IServer implementation
    public HashSet<string> AllowedCommands { get; }
    public event Action<IMessage> MessageReceived;
    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StopAsync(CancellationToken cancellationToken = default);
    public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
```

### Логика работы

- `Register(server)` — подписывается на `server.MessageReceived`. При получении сообщения от зарегистрированного сервера:
  1. Запоминает в таблице маршрутизации: `message.ClientId → server`.
  2. Если сообщение `Type = "command"` — проверяет `server.AllowedCommands`. Если команда не в whitelist'е — дропает сообщение с логированием, отправляет клиенту `"text"` с сообщением об ошибке.
  3. Пробрасывает сообщение через собственный `MessageReceived`.
- `SendAsync(clientId, message)` — находит сервер по таблице маршрутизации и вызывает `server.SendAsync(clientId, message)`. Если `clientId` не найден — выбрасывает `KeyNotFoundException`.
- `StartAsync` / `StopAsync` — вызывает соответствующие методы у всех зарегистрированных серверов.
- `AllowedCommands` Router'а — объединение AllowedCommands всех зарегистрированных серверов (информационное, фильтрация происходит на уровне конкретного сервера-источника).

### Коллизии clientId

На практике коллизии маловероятны: Telegram использует числовые chat_id, файловый клиент — GUID, консольный — "console" или конфигурируемый. При обнаружении коллизии (два сервера регистрируют одинаковый clientId) — логируем warning и перезаписываем маршрут (last-write-wins).

## 6. Реализации

### 6.1. FileServer / FileClient

Файловая реализация использует shared folder для обмена сообщениями. Один файл — одно сообщение в формате JSON (envelope). Файл удаляется после прочтения.

#### Конфигурация

```csharp
public class FileServerConfig
{
    public string Directory { get; set; }
    public int PollingIntervalMs { get; set; } = 5000;
    public HashSet<string> AllowedCommands { get; set; } = new();
}

public class FileClientConfig
{
    public string Directory { get; set; }
    public string ClientId { get; set; }
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

Все типы сообщений сериализуются в единый JSON-формат. Content всегда строка (для structured типов — JSON-строка).

```json
{
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "type": "choice",
    "content": "{\"text\": \"Продолжаем?\", \"options\": [\"Да\", \"Нет\"]}",
    "clientId": "client-abc-123",
    "timestamp": "2026-04-14T12:00:00Z"
}
```

#### Рендеринг типизированных сообщений

FileServer/FileClient **не рендерят** — передают JSON as-is. Интерпретация на стороне потребителя файлового транспорта (другой агент, скрипт, UI).

#### Атомарность записи

Запись через временный файл с переименованием:
1. Создаём `{guid}.tmp` с содержимым.
2. Переименовываем в `{guid}.json`.

FileSystemWatcher фильтрует по `*.json`, поэтому `.tmp` файлы не подхватываются.

#### Ordering

Файлы обрабатываются в порядке `CreationTime`. При polling — сортировка по дате создания. FileSystemWatcher может доставлять события не в порядке создания, поэтому при получении события через watcher вычитываем все накопившиеся файлы с сортировкой.

#### Мультиклиент (outgoing)

В `outgoing/` лежат файлы для всех клиентов. Каждый клиент при polling/watch читает все файлы, но обрабатывает и удаляет только те, где `clientId` совпадает с его собственным. Чужие файлы не трогает.

### 6.2. TelegramServer

Адаптер над `Telegram.Bot`. Выступает как сервер — слушает входящие сообщения и callback_query от пользователей Telegram, отправляет типизированные сообщения с рендерингом в Telegram-формат.

#### Конфигурация

```csharp
public class TelegramServerConfig
{
    public string BotToken { get; set; }
    public HashSet<string> AllowedCommands { get; set; } = new();
}
```

#### Маппинг входящих

- `clientId` = `chatId.ToString()` (Telegram chat ID).
- Текстовое сообщение → `IMessage` с `Type = "message"`, `Content = text`.
- Callback query (нажатие inline-кнопки) → `IMessage` с `Type = "response"`, `Content = JSON` с `to` (id исходного сообщения) и `value`/`items` в зависимости от контекста. TelegramServer хранит маппинг отправленных сообщений, чтобы связать callback_query с исходным message id.

#### Рендеринг исходящих

| Тип | Рендеринг |
|-----|-----------|
| `text` | Markdown через `SendMessage` |
| `choice` | Текст + `InlineKeyboardMarkup` с кнопками по одной на вариант |
| `list` (info only) | Форматированный текст со списком |
| `list` (с actions) | Каждый элемент с actions → отдельное сообщение с inline-кнопками. Элементы без actions — в общем текстовом сообщении |
| `notification` (thinking) | `SendChatAction(Typing)` |
| `notification` (attention) | `SetMyName("ПМ 📌")` или аналогичное |
| `notification` (attention_clear) | `SetMyName("ПМ")` — возврат к обычному имени |
| `command` | Не рендерится (команды — входящие) |
| `response` | Не рендерится (ответы — входящие) |

#### Lifecycle

- `StartAsync` — запускает polling через `TelegramBotClient.ReceiveAsync`.
- `StopAsync` — останавливает polling через `CancellationToken`.

### 6.3. TelegramClient

TelegramClient не реализуется. Telegram-мессенджер и есть клиент.

### 6.4. ConsoleServer / ConsoleClient

Консольная реализация через stdin/stdout. ConsoleServer и ConsoleClient запускаются в одном процессе — Server слушает stdin (входящие от пользователя), Client пишет в stdout (ответы от агента). Используется для CLI-режима.

#### Конфигурация

```csharp
public class ConsoleServerConfig
{
    public string ClientId { get; set; } = "console";
    public HashSet<string> AllowedCommands { get; set; } = new();
    public string[] StartupCommands { get; set; } = [];
}

public class ConsoleClientConfig
{
    public string ClientId { get; set; } = "console";
}
```

`StartupCommands` — команды, которые ConsoleServer отправляет при старте (из аргументов командной строки, переданных приложением). Каждая команда отправляется как `Type = "command"`.

#### ConsoleServer (stdin → agent)

- `StartAsync` — начинает слушать stdin в фоновом потоке. При получении строки:
  - Если начинается с `/` — формирует `"command"` сообщение (парсит `/name arg1 arg2` → `{name, args}`).
  - Иначе — формирует `"message"` сообщение.
  - Отправляет startup commands при первом старте.
- `StopAsync` — останавливает чтение stdin.
- `SendAsync` — рендерит типизированное сообщение в stdout (см. таблицу рендеринга).
- `AllowedCommands` — whitelist из конфига. Определяет, какие команды пользователь может отправлять из консоли.

#### Рендеринг (ConsoleServer.SendAsync → stdout)

| Тип | Рендеринг |
|-----|-----------|
| `text` | Markdown strip + вывод текста. Заголовки — CAPS, bold — без маркеров, списки — as-is |
| `choice` | Текст + нумерованный список вариантов. Запрос ввода номера |
| `list` (info only) | Нумерованный список с details |
| `list` (с actions) | Нумерованный список. Для элементов с actions: `[номер] текст → [действие1/действие2]`. Запрос ввода `номер.действие` |
| `notification` (thinking) | `"..."` или `"[Думаю...]"` |
| `notification` (attention) | `"[!]"` добавляется к prompt'у |
| `notification` (attention_clear) | Убирает `"[!]"` из prompt'а |

При получении ввода от пользователя в ответ на `choice` или `list` — ConsoleServer формирует `"response"` сообщение и отправляет его как входящее (через `MessageReceived`).

#### ConsoleClient (stdout ← agent)

Зеркальная роль: используется, когда внешнее приложение подключается к агенту через stdin/stdout. Не для CLI-режима самого агента, а для программного взаимодействия (pipe).

- `ConnectAsync` — начинает слушать stdin для входящих сообщений от сервера.
- `SendAsync` — пишет JSON-конверт в stdout.
- `MessageReceived` — срабатывает при получении JSON-конверта из stdin.

## 7. Структура проекта

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
      MessageTypes.cs               ← константы типов ("text", "choice", etc.)
      Content/                      ← модели Content для типизированных сообщений
        ChoiceContent.cs            ← {text, options}
        ListContent.cs              ← {title, items[{id, text, details, actions}]}
        NotificationContent.cs      ← {kind}
        CommandContent.cs           ← {name, args}
        ResponseContent.cs          ← {to, value?, items?}
      File/
        FileServer.cs
        FileClient.cs
        FileServerConfig.cs
        FileClientConfig.cs
      Console/
        ConsoleServer.cs
        ConsoleClient.cs
        ConsoleServerConfig.cs
        ConsoleClientConfig.cs
        ConsoleRenderer.cs          ← рендеринг типизированных сообщений в текст
      Telegram/
        TelegramServer.cs
        TelegramServerConfig.cs
        TelegramRenderer.cs         ← рендеринг типизированных сообщений в Telegram-формат
  tests/
    AgentParty.Tests/               ← xUnit тесты
```

NuGet зависимости:
- `AgentParty` (core): нет внешних зависимостей для файловой и консольной реализации.
- `AgentParty` включает Telegram: `Telegram.Bot` >= 22.0.

Если в будущем Telegram-зависимость станет нежелательной — выносим в отдельный пакет `AgentParty.Telegram`.

## 8. Пример использования

### Сторона агента (сервер с несколькими транспортами)

```csharp
var telegramServer = new TelegramServer(new TelegramServerConfig 
{ 
    BotToken = "123456:ABC-DEF",
    AllowedCommands = new() { "status", "help" }
});

var consoleServer = new ConsoleServer(new ConsoleServerConfig
{
    ClientId = "console",
    AllowedCommands = new() { "setup", "status", "help", "shutdown" },
    StartupCommands = startupCommands // из аргументов командной строки
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
            // Обычное сообщение от пользователя → в AgentLoop
            await agentLoop.ProcessMessage(message.ClientId, message.Content);
            break;
            
        case MessageTypes.Command:
            // Команда (уже прошла whitelist-фильтрацию в Router)
            var cmd = CommandContent.Parse(message.Content);
            await commandHandler.Handle(message.ClientId, cmd.Name, cmd.Args);
            break;
            
        case MessageTypes.Response:
            // Ответ на choice/list → доставить в ожидающий AgentLoop
            var resp = ResponseContent.Parse(message.Content);
            agentLoop.ProvideResponse(message.ClientId, resp);
            break;
    }
};

await router.StartAsync();
```

### Отправка типизированных сообщений агентом

```csharp
// Простой текст
await router.SendAsync(clientId, new Message
{
    Type = MessageTypes.Text,
    Content = "Создал 3 задачи в проекте Shooter.",
    ClientId = clientId
});

// Выбор (ожидает ответ)
await router.SendAsync(clientId, new Message
{
    Id = "confirm-001",
    Type = MessageTypes.Choice,
    Content = JsonSerializer.Serialize(new ChoiceContent
    {
        Text = "Выполняем?",
        Options = ["Да", "Нет", "Обсудим"]
    }),
    ClientId = clientId
});

// Информационный список (без actions, не ждёт ответ)
await router.SendAsync(clientId, new Message
{
    Type = MessageTypes.List,
    Content = JsonSerializer.Serialize(new ListContent
    {
        Title = "План изменений",
        Items = [
            new ListItem { Id = "1", Text = "Создать задачу: AI врагов" },
            new ListItem { Id = "2", Text = "Создать задачу: Спавн волн" },
            new ListItem { Id = "3", Text = "Обновить приоритет SHOOTER-042" }
        ]
    }),
    ClientId = clientId
});

// Список с действиями (ждёт ответ)
await router.SendAsync(clientId, new Message
{
    Id = "decomp-001",
    Type = MessageTypes.List,
    Content = JsonSerializer.Serialize(new ListContent
    {
        Title = "Предложенные задачи",
        Items = [
            new ListItem 
            { 
                Id = "1", Text = "AI поведение врагов",
                Actions = [new ListAction { Id = "create", Label = "Создать" }, 
                           new ListAction { Id = "discuss", Label = "Обсудить" },
                           new ListAction { Id = "skip", Label = "Не нужно" }]
            },
            new ListItem 
            { 
                Id = "2", Text = "Спавн волн",
                Actions = [new ListAction { Id = "create", Label = "Создать" },
                           new ListAction { Id = "skip", Label = "Не нужно" }]
            }
        ]
    }),
    ClientId = clientId
});
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
    Console.WriteLine($"Агент ответил [{message.Type}]: {message.Content}");
};

await client.ConnectAsync();

// Отправляем обычное сообщение
await client.SendAsync(new Message
{
    Type = MessageTypes.Message,
    Content = "Нужен рефакторинг PlayerController",
    ClientId = "coder-agent-001"
});

// Отправляем команду
await client.SendAsync(new Message
{
    Type = MessageTypes.Command,
    Content = JsonSerializer.Serialize(new CommandContent
    {
        Name = "status",
        Args = ["shooter"]
    }),
    ClientId = "coder-agent-001"
});
```

## 9. Жизненный цикл и краевые случаи

### 9.1. Идемпотентность Start/Stop

`StartAsync` и `StopAsync` у всех реализаций (`IServer`, `IClient`) — **идемпотентны**. Повторный вызов — no-op. Реализации хранят внутренний флаг состояния (`_isRunning`).

| Текущее состояние | Вызов | Результат |
|---|---|---|
| Stopped | `StartAsync` | Запуск, `_isRunning = true` |
| Running | `StartAsync` | No-op |
| Running | `StopAsync` | Остановка, `_isRunning = false` |
| Stopped | `StopAsync` | No-op |
| Stopped | `SendAsync` | `InvalidOperationException` |

### 9.2. Router и регистрация серверов

Router отслеживает своё состояние (`_isRunning`). Поведение `Register`/`Unregister` зависит от этого состояния.

**Register:**
- Router stopped + Register(server) → сервер просто добавляется в список. При последующем `Router.StartAsync` будет вызван `server.StartAsync`.
- Router running + Register(server) → сервер добавляется и `server.StartAsync` вызывается немедленно. Если сервер уже запущен — Start идемпотентен, проблемы нет.

**Unregister:**
- Router stopped + Unregister(server) → сервер удаляется из списка, отписка от событий.
- Router running + Unregister(server) → сервер удаляется, отписка, вызывается `server.StopAsync`. Маршруты этого сервера удаляются из таблицы маршрутизации.

**StartAsync Router:**
- Вызывает `StartAsync` у всех зарегистрированных серверов.

**StopAsync Router:**
- Вызывает `StopAsync` у всех зарегистрированных серверов.

### 9.3. Фильтрация команд

При получении сообщения с `Type = "command"` от зарегистрированного сервера:
1. Router парсит `CommandContent.Name` из сообщения.
2. Проверяет `server.AllowedCommands.Contains(name)`.
3. Если разрешено — пробрасывает через `MessageReceived`.
4. Если запрещено — логирует warning, отправляет клиенту `"text"` сообщение с ошибкой `"Команда '{name}' недоступна через этот канал"`.

### 9.4. Ошибки маршрутизации

- `Router.SendAsync` с неизвестным `clientId` → выбрасывает `KeyNotFoundException` с информативным сообщением.
- Сервер, через который был зарегистрирован клиент, был удалён через `Unregister` → маршрут удалён, `SendAsync` выбросит `KeyNotFoundException`.

### 9.5. Dispose

- `Dispose` вызывает `StopAsync` синхронно если сервер/клиент был запущен.
- Router при `Dispose` вызывает `StopAsync` и `Dispose` у всех зарегистрированных серверов, очищает таблицу маршрутизации. Если нужно сохранить сервер живым — сначала `Unregister`, потом `Dispose` роутера.

## 10. Расширяемость

### Новые транспорты

Для добавления нового транспорта достаточно реализовать `IServer` и/или `IClient`, включая рендеринг типизированных сообщений. Регистрация в Router — одна строка.

Примеры будущих реализаций:
- `HttpServer` / `HttpClient` — REST-based.
- `WebSocketServer` / `WebSocketClient` — для real-time.

### Новые типы сообщений

Типы сообщений расширяемы. Транспорт, не знающий конкретный тип, может:
- Для исходящих: попытаться отрендерить Content как текст (fallback).
- Для входящих: пробросить as-is через `MessageReceived`.

Потребляющий код определяет, какие типы он поддерживает. AgentParty предоставляет базовый набор (`text`, `choice`, `list`, `notification`, `command`, `response`, `message`), но не ограничивает этим.

### Кастомные рендереры

Каждый транспорт имеет свой Renderer (например, `TelegramRenderer`, `ConsoleRenderer`). Рендереры могут быть заменены через конфигурацию для кастомизации отображения.

## 11. CI/CD: GitHub Packages

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
    <VersionPrefix>0.4</VersionPrefix>
    <Authors>elmortem</Authors>
    <Description>Transport and rendering abstraction for LLM agent communication</Description>
    <RepositoryUrl>https://github.com/elmortem/AgentParty</RepositoryUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
</Project>
```

Версия пакета формируется как `{VersionPrefix}.{github.run_number}` — например `0.2.1`, `0.2.2`, `0.2.37`. Patch растёт автоматически с каждым push. Для мажорных/минорных изменений — меняем `VersionPrefix` руками.

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

## 12. Feed — информационный канал

### 12.1. Концепция

Feed — однонаправленный поток входящей информации, который не является ни сообщением от пользователя, ни командой. Это "труба", по которой сервер проталкивает данные агенту. Агент не знает и не должен знать, откуда эти данные — из Telegram-канала, RSS, файловой системы или любого другого источника.

Feed не подразумевает ответа. Агент может использовать данные из feed как контекст, триггер для действий или просто игнорировать.

### 12.2. IFeedMessage

```csharp
public interface IFeedMessage
{
    string Content { get; }
    string? Author { get; }
    DateTime Timestamp { get; }
    string Source { get; }
}
```

Поля:
- `Content` — текстовое содержимое (обязательное). Всегда строка, без JSON-обёрток.
- `Author` — автор (необязательное). Может отсутствовать (посты каналов, автоматические уведомления).
- `Timestamp` — время создания (UTC).
- `Source` — строковый идентификатор источника (обязательное). Задаётся транспортным уровнем: TelegramServer — `FeedSource.ToString()` (формат `"chatId"` или `"chatId/threadId"`), FileServer — из JSON. Пустая строка допустима. Библиотека не валидирует и не интерпретирует значение; семантика определяется потребителем.

Feed-сообщение **не имеет** `Id`, `Type`, `ClientId` — это не сообщение протокола, а единица информации.

### 12.3. FeedMessage (реализация)

```csharp
public class FeedMessage : IFeedMessage
{
    public string Content { get; set; } = string.Empty;
    public string? Author { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = string.Empty;
}
```

Файл: `FeedMessage.cs` в корне проекта (рядом с `Message.cs`).

### 12.4. Изменения в IServer

```csharp
public interface IServer : IDisposable
{
    event Action<IMessage> MessageReceived;
    event Action<IFeedMessage> FeedReceived;   // NEW

    HashSet<string> AllowedCommands { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
}
```

Серверы, не поддерживающие feed, просто никогда не вызывают `FeedReceived`. Событие объявлено, но не используется (ConsoleServer).

### 12.5. IClient — без изменений

Feed — односторонний канал: клиент → сервер → агент. Клиент не получает feed, поэтому `IClient` не меняется. Клиенты, которым нужно отправлять feed-данные серверу, делают это через собственные методы конкретной реализации (например, `FileClient.SendFeedAsync`).

### 12.6. Изменения в Router

Router подписывается на `FeedReceived` у всех зарегистрированных серверов и агрегирует их в собственный `FeedReceived`, аналогично тому, как он уже делает с `MessageReceived`.

```csharp
public class Router : IServer
{
    public event Action<IMessage>? MessageReceived;
    public event Action<IFeedMessage>? FeedReceived;   // NEW

    // Register/Unregister подписываются/отписываются от FeedReceived серверов
}
```

Feed не проходит через фильтрацию команд и не участвует в таблице маршрутизации — это чистый односторонний поток.

### 12.7. TelegramServer — изменения

#### Удаляем

- `public event Action<Update>? RawUpdateReceived` — заменяется на `FeedReceived` из `IServer`.
- `public long BotId { get; private set; }` — больше не нужен на публичном API. Не нужен и внутри.

#### FeedSource — идентификатор фид-источника

```csharp
public readonly struct FeedSource : IEquatable<FeedSource>
{
    public long ChatId { get; }
    public int? ThreadId { get; }
}
```

`FeedSource` идентифицирует конкретный источник фида в Telegram: чат + опциональный топик (thread). Матчинг **строгий** — `(chatId, null)` не совпадает с `(chatId, 2736)`.

Строковый формат (для конфигурации в агенте):
- `"-1003287187081"` → `FeedSource(chatId: -1003287187081, threadId: null)` — General-тред или чат без топиков
- `"-1003287187081/2736"` → `FeedSource(chatId: -1003287187081, threadId: 2736)` — конкретный топик

`FeedSource` реализует `IEquatable<FeedSource>`, переопределяет `GetHashCode`/`Equals` для корректной работы в `HashSet`. Имеет статический метод `Parse(string)` и `ToString()` в формате выше.

#### Конфигурация

```csharp
public class TelegramServerConfig
{
    public string BotToken { get; set; } = string.Empty;
    public string BotName { get; set; } = string.Empty;
    public string AttentionMarker { get; set; } = " 📌";
    public HashSet<string> AllowedCommands { get; set; } = new();
    public HashSet<long> AllowedUserIds { get; set; } = new();
    public HashSet<FeedSource> FeedSources { get; set; } = new();   // CHANGED: was HashSet<long> FeedChatIds
    public bool FeedDiscoveryMode { get; set; }
}
```

- `AllowedUserIds` — множество ID пользователей Telegram, от которых принимаются сообщения и команды **из личных чатов** (`Chat.Type == "private"`). Если пусто — фильтрация по пользователю отключена (принимаются все из личных чатов). Сообщения из групп/каналов **никогда** не попадают в `MessageReceived`, независимо от `AllowedUserIds`.
- `FeedSources` — множество `(chatId, threadId?)`, определяющее из каких чатов/топиков принимать фид. Матчинг строгий: `message_thread_id` из update должен точно совпадать с `ThreadId` в `FeedSource` (включая `null == null`).
- `FeedDiscoveryMode` — временный режим: если `true`, **все** сообщения из не-private чатов отправляются в feed без проверки `FeedSources`. Позволяет узнать chatId/threadId групп и каналов. На личные чаты **не влияет** — они всегда идут через `AllowedUserIds` → `MessageReceived`.

#### Логика HandleUpdateAsync

Ключевой принцип: **тип чата определяет ветку маршрутизации**. Личные сообщения (`Chat.Type == "private"`) идут только в `MessageReceived`. Всё остальное (supergroup, group, channel) идёт только в `FeedReceived`.

```
Получен update:
  1. Это callback_query?
     → Да: HandleCallbackQuery → MessageReceived
     → Нет: переходим к п.2

  2. Определяем msg = update.ChannelPost ?? update.Message
     → msg == null: дропаем

  3. Определяем тип чата: msg.Chat.Type

  4. Chat.Type == "private"?
     → Да: это личное сообщение
        → userId входит в AllowedUserIds (или AllowedUserIds пуст)?
           → Да + есть текст: Message → MessageReceived
           → Нет: дропаем
     → Нет: переходим к п.5

  5. Не-private чат (supergroup, group, channel) — только фид
     → Собираем feedSource = FeedSource(msg.Chat.Id, msg.MessageThreadId)
     → FeedDiscoveryMode == true?
        → Да: HandleFeedUpdate → FeedReceived
     → feedSource входит в FeedSources? (строгий матчинг chatId + threadId, включая null == null)
        → Да: HandleFeedUpdate → FeedReceived
        → Нет: дропаем
```

Важно: `msg.MessageThreadId` в Telegram API — это `int?`. Для сообщений вне топика или в General-треде оно `null`. Для сообщений в конкретном топике — числовой ID первого сообщения топика.

#### Сбор текста из feed-апдейтов

Из Telegram-апдейта извлекаем текстовое содержимое в следующем приоритете:
1. `update.ChannelPost?.Text` или `update.Message?.Text` — текст сообщения
2. `update.ChannelPost?.Caption` или `update.Message?.Caption` — подпись к фото/видео/документу
3. `update.ChannelPost?.Document?.FileName` или `update.Message?.Document?.FileName` — имя файла

Если ничего текстового нет — апдейт из feed дропается.

Author заполняется из `msg.From?.Username` или `msg.From?.FirstName` (если доступно). Для `ChannelPost` автор обычно отсутствует — будет `null`.

Source заполняется в формате `FeedSource.ToString()`:
- `"-1003287187081"` — если `msg.MessageThreadId == null`
- `"-1003287187081/2736"` — если `msg.MessageThreadId == 2736`

Это позволяет агенту точно знать, из какого чата и топика пришло сообщение.

### 12.8. FileServer / FileClient — feed через отдельную папку

#### Структура папки

```
{Directory}/
  incoming/    ← клиент пишет сообщения, сервер читает
  outgoing/    ← сервер пишет сообщения, клиент читает
  feed/        ← клиент пишет feed, сервер читает          // NEW
```

#### Конфигурация

Дополнительных полей конфигурации не требуется. Папка `feed/` создаётся автоматически при старте, если сервер/клиент работает с директорией.

#### FileServer

При старте начинает мониторить папку `feed/` (FileSystemWatcher + polling) аналогично `incoming/`. Файлы в `feed/` — JSON-сериализованные `FeedMessage`. При чтении десериализует, удаляет файл, вызывает `FeedReceived`.

#### FileClient

Для отправки feed-данных серверу добавляем метод (не в интерфейс IClient — feed не является частью протокола общения клиента с сервером, но FileClient может быть утилитой для записи в feed):

```csharp
public class FileClient : IClient
{
    // ... существующее ...
    
    public Task SendFeedAsync(IFeedMessage feedMessage, CancellationToken cancellationToken = default);
}
```

`SendFeedAsync` сериализует `FeedMessage` в JSON и пишет в папку `feed/` с атомарной записью (.tmp → .json).

**Итого для File-транспорта:**
- `FileServer` — мониторит `feed/`, десериализует `FeedMessage`, вызывает `FeedReceived`.
- `FileClient` — имеет `SendFeedAsync` для записи в `feed/`.

### 12.9. ConsoleServer / ConsoleClient

Feed не поддерживается. Событие `FeedReceived` объявлено (требование интерфейса), но никогда не вызывается.

### 12.10. Формат feed-файла (File-транспорт)

```json
{
    "content": "Новая статья о GameDev: 10 паттернов для AI врагов",
    "author": "GameDevChannel",
    "source": "dev-feed",
    "timestamp": "2026-04-15T10:30:00Z"
}
```

Если `source` не указан в JSON — десериализуется как пустая строка (default).

### 12.11. Пример использования

```csharp
var router = new Router();
router.Register(telegramServer);
router.Register(fileServer);

router.MessageReceived += async message =>
{
    // Обычные сообщения и команды — как раньше
};

router.FeedReceived += feedMessage =>
{
    // Информация из каналов, файлов и т.д.
    // Агент решает, что с ней делать
    logger.Log($"Feed [{feedMessage.Source}]: {feedMessage.Content} (from: {feedMessage.Author ?? "unknown"})");
    agentContext.AddFeedItem(feedMessage);
};

await router.StartAsync();
```

### 12.12. Новые файлы

```
AgentParty/
  src/
    AgentParty/
      IFeedMessage.cs                    // NEW — интерфейс
      FeedMessage.cs                     // NEW — реализация
      Telegram/
        FeedSource.cs                    // NEW — readonly struct (chatId + threadId?)
```

### 12.13. Изменяемые файлы

| Файл | Изменение |
|------|-----------|
| `IServer.cs` | + `event Action<IFeedMessage> FeedReceived` |
| `IClient.cs` | + `event Action<IFeedMessage> FeedReceived` |
| `Router.cs` | + подписка/отписка на `FeedReceived` серверов, агрегация |
| `TelegramServer.cs` | − `RawUpdateReceived`, − `BotId`, + `FeedReceived`, + маршрутизация по `Chat.Type` (private → MessageReceived, остальное → FeedReceived), + фильтрация по `FeedSources` с учётом `MessageThreadId`, + `FeedDiscoveryMode` только для не-private чатов |
| `TelegramServerConfig.cs` | + `AllowedUserIds` (HashSet<long>), + `FeedSources` (HashSet<FeedSource>), + `FeedDiscoveryMode` |
| `FileServer.cs` | + мониторинг `feed/`, + `FeedReceived` |
| `FileClient.cs` | + `SendFeedAsync` |
| `ConsoleServer.cs` | + `FeedReceived` (не используется) |

### 12.14. Новые файлы

| Файл | Описание |
|------|----------|
| `Telegram/FeedSource.cs` | `readonly struct FeedSource` — идентификатор фид-источника (chatId + threadId?), `IEquatable`, `Parse`/`ToString` |

## 13. Ограничения и допущения

- Библиотека не управляет сессиями. Группировка сообщений в сессии, контексты, диалоги — ответственность потребляющего кода.
- Файловый транспорт работает на доверии: клиент сам фильтрует свои сообщения, защиты от чтения чужих нет.
- FileSystemWatcher может пропускать события при высокой нагрузке — periodic polling компенсирует.
- Ordering гарантируется в пределах одного транспорта. Между разными транспортами (Telegram + File) порядок не гарантирован.
- Коллизии `clientId` между транспортами маловероятны, но возможны. При коллизии — last-write-wins с логированием.
- Транспорт, не поддерживающий конкретный тип сообщения, использует текстовый fallback для исходящих и пробрасывает as-is для входящих.
- ConsoleServer/ConsoleClient рассчитаны на одного клиента. Для multi-client CLI — использовать FileServer/FileClient.
