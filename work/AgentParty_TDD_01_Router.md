# TDD 01 — Router: thread-safety и event handlers

**Что решаем:** пункты 1.1, 1.2, 1.8 из `AgentParty_Review.md`.

**Scope:** только `Router.cs`. Интерфейсы `IServer`/`IClient`/`IMessage` не меняются. Реализации серверов не меняются (их lifecycle — в отдельных ТДД).

---

## 1. Проблемы

### 1.1. Thread-safety коллекций

- `_servers` (List) — итерация в `StartAsync`/`StopAsync`/`Dispose`/`AllowedCommands`, add/remove в `Register`/`Unregister`.
- `_routingTable` (Dictionary) — **пишется в hot path** из event handler'а, читается в `SendAsync`, чистится в `Unregister`/`Dispose`.
- `_handlers`, `_feedHandlers` (Dictionary) — add/remove в `Register`/`Unregister`.

Параллельные источники входящих:
- `FileServer` — `FileSystemWatcher.Created` с пула потоков + polling Task.
- `TelegramServer` — `HandleUpdateAsync` из `StartReceiving`.
- Пользовательские транспорты — произвольно.

Сейчас при одновременных сообщениях из двух серверов происходит неатомарная запись в `_routingTable`, что может привести к повреждению Dictionary (бросит `InvalidOperationException` или даст некорректное состояние).

### 1.2. Синхронная блокировка в event handler

```csharp
server.SendAsync(message.ClientId, errorMsg).GetAwaiter().GetResult();
```

Вызов внутри синхронного `Action<IMessage>` handler'а:
- Блокирует транспортный поток (например, update-цикл Telegram) на время I/O.
- На контекстах с `SynchronizationContext` — риск deadlock.
- Исключения из `SendAsync` пропагируются в транспорт и ломают его polling loop.

### 1.8. Исключения подписчиков ломают транспорт

`MessageReceived?.Invoke(message)` и `FeedReceived?.Invoke(feed)` — если подписчик (агент) кинет, исключение прилетит в транспорт:
- `FileServer.ProcessIncomingFiles` — поймает общий catch? Нет, сейчас там только `IOException` и `JsonException`. Неожиданное исключение прервёт обработку остальных файлов в этом тике.
- `TelegramServer.HandleUpdateAsync` — в `StartReceiving` ошибка в update handler прилетает в `HandleErrorAsync`, но это не штатное место для ошибок подписчика.

---

## 2. Цели

1. Router безопасен при параллельных входящих сообщениях от разных серверов.
2. Фильтрация команд (ответ клиенту) не блокирует транспортный поток и не уносит его при ошибке.
3. Исключение подписчика на `MessageReceived`/`FeedReceived` не ломает транспорт.
4. API Router'а не меняется (backward-compatible для текущих пользователей).

---

## 3. Решения

### 3.1. Коллекции

- `_routingTable` → `ConcurrentDictionary<string, IServer>`. Hot path пишется из разных потоков.
- `_servers`, `_handlers`, `_feedHandlers` → защищаются одним приватным `object _lock` (плоская синхронизация, Register/Unregister редкие).
- Итерации по `_servers` в `StartAsync`/`StopAsync`/`Dispose`/`AllowedCommands` — делаются через snapshot под локом:
	```csharp
	IServer[] snapshot;
	lock (_lock) { snapshot = _servers.ToArray(); }
	// работа со snapshot вне лока
	```
	Это гарантирует, что долгие async-операции не держат лок.

Почему не `ReaderWriterLockSlim`: Register/Unregister редки, hot path — только `_routingTable`, которая уже на ConcurrentDictionary. Простой `lock` тут проще и надёжнее.

### 3.2. Фильтрация команд — без синхронной блокировки

Вариант A (рекомендуемый): **`async void` handler с try/catch**.

```csharp
async void Handler(IMessage message)
{
	try
	{
		_routingTable[message.ClientId] = server;

		if (message.Type == MessageTypes.Command)
		{
			if (!TryCheckCommand(server, message, out var blocked))
				return;

			if (blocked)
			{
				await SendBlockedCommandErrorAsync(server, message);
				return;
			}
		}

		RaiseMessageReceived(message);
	}
	catch (Exception ex)
	{
		_rawLogger?.Log("Router.Handler", ex.ToString());
	}
}
```

- `async void` допустим для event handler'ов — это общепринятый паттерн.
- Внутренний try/catch гарантирует, что исключение не всплывёт наружу (включая в `SynchronizationContext`).
- Отправка error-сообщения идёт через `await`, транспорт не блокируется.

Вариант B (альтернатива): `Task.Run(async () => ...)` внутри handler'а. Не даёт преимуществ над async void, но добавляет лишний поток. Отклоняем.

**Открытый вопрос (DEC-1):** если `server.SendAsync` для error-сообщения упадёт — swallow + log. Подтвердить.

### 3.3. try/catch вокруг Invoke подписчиков

Обернуть `Invoke` в try/catch. Для каждого подписчика отдельно (иначе исключение в первом подписчике не даст сработать остальным):

```csharp
private void RaiseMessageReceived(IMessage message)
{
	var handlers = MessageReceived;
	if (handlers == null) return;

	foreach (var handler in handlers.GetInvocationList().Cast<Action<IMessage>>())
	{
		try { handler(message); }
		catch (Exception ex) { _rawLogger?.Log("Router.Subscriber", ex.ToString()); }
	}
}
```

Аналогично для `FeedReceived`.

**Открытый вопрос (DEC-2):** делать ли изоляцию подписчиков (через `GetInvocationList`) или достаточно одного try/catch вокруг `Invoke`. Изоляция полезнее, но стоит лишнюю аллокацию. Склоняюсь к изоляции — она ценнее.

### 3.4. IRawLogger в Router

Добавить опциональный параметр конструктора:

```csharp
public Router(IRawLogger? rawLogger = null)
```

Консистентно с `FileServer`, `ConsoleServer`, `TelegramServer`. Текущий безпараметрический конструктор сохраняется как дефолтный (через optional parameter).

### 3.5. Unregister: отписка от событий перед StopAsync

Сейчас порядок:
1. `server.MessageReceived -= handler`
2. `server.FeedReceived -= feedHandler`
3. Удаление из `_servers` и `_routingTable`
4. `server.StopAsync()`

Это правильно — после отписки новые события не придут в Router. Между `StopAsync` и уже стоящими в очереди handler'ами возможна гонка (handler уже в process, MessageReceived инвокнут до отписки). Это нормально — после try/catch handler отработает или дропнет сообщение, роутинг всё равно не будет использоваться.

Оставляем как есть, только под lock.

---

## 4. Новый публичный API

```csharp
public class Router : IServer
{
	public Router(IRawLogger? rawLogger = null);  // было: без параметров

	// Остальное без изменений:
	public void Register(IServer server);
	public void Unregister(IServer server);
	public HashSet<string> AllowedCommands { get; }
	public event Action<IMessage>? MessageReceived;
	public event Action<IFeedMessage>? FeedReceived;
	public Task StartAsync(CancellationToken cancellationToken = default);
	public Task StopAsync(CancellationToken cancellationToken = default);
	public Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default);
	public void Dispose();
}
```

Backward-compatible: старый `new Router()` продолжает работать.

---

## 5. План изменений в Router.cs

1. Добавить поля:
	- `private readonly object _lock = new();`
	- `private readonly IRawLogger? _rawLogger;`
	- Заменить `_routingTable` на `ConcurrentDictionary<string, IServer>`.
2. Добавить конструктор с `IRawLogger?`.
3. Переписать `Register`:
	- Под `_lock`: добавить в `_servers`, `_handlers`, `_feedHandlers`.
	- Handler — `async void` с try/catch.
	- Вызов `StartAsync` на сервере — вне лока, под флаг `_isRunning` (читать под локом, вызывать снаружи).
4. Переписать `Unregister`:
	- Под `_lock`: отписать, удалить из `_servers`, `_handlers`, `_feedHandlers`.
	- `_routingTable` — удаление через `ConcurrentDictionary.TryRemove` в цикле (по snapshot'у ключей).
	- `StopAsync` на сервере — вне лока.
5. `StartAsync`/`StopAsync`/`Dispose` — работают через snapshot `_servers.ToArray()` под локом, затем итерация вне лока.
6. `AllowedCommands` — тоже snapshot.
7. `SendAsync` — `_routingTable.TryGetValue` (thread-safe из ConcurrentDictionary).
8. `RaiseMessageReceived`/`RaiseFeedReceived` — приватные методы с изоляцией подписчиков.
9. `SendBlockedCommandErrorAsync` — приватный async метод для отправки error-сообщения при запрете команды.

---

## 6. Тесты

Дополнить `RouterTests.cs`. Существующие тесты должны продолжать проходить.

### Новые тесты

1. **Concurrent messages from multiple servers don't corrupt routing table.**
	- 2 `FakeServer`, параллельно из разных Task'ов симулируем по 1000 сообщений.
	- После — для каждого clientId `SendAsync` работает без исключений.

2. **Blocked command doesn't throw on transport thread.**
	- `FakeServer.SimulateMessage(blockedCommand)` возвращается без исключения.
	- После отработки async handler — в `FakeServer.Sent` лежит error-text.
	- Использовать `Task.Delay`/`SpinWait.SpinUntil` для ожидания async handler'а.

3. **Subscriber exception doesn't propagate.**
	- Подписчик на `MessageReceived` кидает `InvalidOperationException`.
	- `FakeServer.SimulateMessage(msg)` не кидает.
	- IRawLogger получил запись (если передан).

4. **Subscriber exception doesn't block other subscribers.**
	- Два подписчика: первый кидает, второй записывает сообщение.
	- После SimulateMessage — второй подписчик всё равно получил сообщение.

5. **Feed subscriber exception doesn't propagate / doesn't block others.**
	- Аналогично 3, 4 для `FeedReceived`.

6. **Blocked command: SendAsync failure doesn't crash.**
	- `FakeServer` настроен так, что на отправку error-сообщения кидает исключение.
	- `SimulateMessage` не кидает, IRawLogger получил запись.

### Модификация existing тестов

- `Command_NotInWhitelist_IsBlocked` — сейчас ожидает, что error уже в `server.Sent` сразу после `SimulateMessage`. После перехода на async void — нужно дождаться (через `SpinWait.SpinUntil` с таймаутом, например 1 сек).
- `Command_EmptyWhitelist_BlocksAllCommands` — аналогично.

---

## 7. Зафиксированные решения

- **DEC-1.** При падении `server.SendAsync` для error-сообщения — swallow + log через `IRawLogger` (source: `"Router.Handler"`). Error-сообщение — best-effort; ронять транспорт из-за невозможности доставить отказ важнее не стоит.
- **DEC-2.** Изоляция подписчиков через `GetInvocationList` + try/catch на каждого. Исключение одного подписчика не мешает остальным и не прилетает в транспорт.
- **DEC-3.** `IRawLogger?` в конструкторе Router — добавляем. Дублирования в логе не будет: источник пишется в параметре `source` (`"Router.Handler"`, `"Router.Subscriber"`), сcope транспорта — свой.
- **DEC-4.** `async void` для event handler'а внутри Router — ок. Стандартный .NET-паттерн, обёрнут во внутренний try/catch.
- **Тесты:** переход на async void потребует `SpinWait.SpinUntil` с таймаутом в тестах фильтрации команд. Это не ломает возможность запускать их из консоли через `dotnet test`.

---

## 8. Out of scope (в других ТДД)

- TelegramServer lifecycle, `_sentMessageMap` → ТДД 02.
- FileServer atomic claim, подкаталоги outgoing, .tmp cleanup → ТДД 03.
- Markdown + rate-limit в Telegram → ТДД 04.
- Timestamp формат → ТДД 05.
