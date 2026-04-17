# TDD 02 — TelegramServer: lifecycle и sentMessageMap

**Что решаем:** пункты 1.3 и 1.4 из `AgentParty_Review.md`.

**Scope:** `TelegramServer.cs`, по необходимости новый internal `SentMessageMap.cs`. `TelegramRenderer.cs` остаётся на ТДД 04 (Markdown + rate-limit). `TelegramServerConfig.cs` — без изменений (если не решим иначе в DEC-1).

---

## 1. Проблемы

### 1.3. StopAsync не ждёт окончания polling

```csharp
public Task StopAsync(CancellationToken cancellationToken = default)
{
	...
	_isRunning = false;
	_cts?.Cancel();
	_cts?.Dispose();
	_cts = null;
	return Task.CompletedTask;
}
```

`StartReceiving` — это fire-and-forget метод `Telegram.Bot`, он запускает polling в фоновой Task, которая нам не видна. После `Cancel()` эта Task завершится не сразу — один update может быть в обработке. Наш `HandleUpdateAsync` ещё успеет поднять `MessageReceived` уже после того, как `StopAsync` вернулся.

Последствия:
- Подписчик получает сообщения после "остановки" сервера. Противоречит контракту.
- `Dispose` и последующий запуск (`StartAsync`) могут гоняться с in-flight update.
- В тестах с in-memory state гоняется флаг и коллекции.

### 1.4. Утечка `_sentMessageMap`

```csharp
private readonly ConcurrentDictionary<int, string> _sentMessageMap = new();
```

- При каждой отправке `Choice` или action-item в `List` добавляется запись.
- Ничего никогда не удаляет записи.
- Долгоживущий бот → OOM за время.

Плюс: при рестарте процесса map пустой. `callback_query` по старой кнопке матчится на fallback: `originalMsgId = telegramMsgId.ToString()`. Агент получает Response с `To`, который ничему не соответствует в его состоянии.

Плюс: пользователь может нажать кнопку повторно. Сейчас Telegram не «выключает» inline-кнопки сам — они остаются кликабельными до редактирования сообщения. Нет явного API для сброса состояния для клиента (например, при старте новой сессии агента).

---

## 2. Цели

1. `StopAsync` возвращается только после того, как polling реально остановлен и новые update'ы не обрабатываются.
2. `_sentMessageMap` ограничен по размеру per-client, не течёт в долгой работе.
3. Есть публичный метод очистки кнопок для конкретного клиента (для старта новой сессии агента).
4. Поведение при рестарте — осознанное и задокументированное.
5. API `TelegramServer` расширяется методом `ClearSentMessagesForClient`, остальное backward-compatible.

---

## 3. Решения

### 3.1. Lifecycle через `ReceiveAsync`

`Telegram.Bot` 22.x предоставляет и `StartReceiving` (fire-and-forget, void), и `ReceiveAsync` (awaitable, возвращает `Task`, завершается при cancel). Переходим на `ReceiveAsync`.

```csharp
public Task StartAsync(CancellationToken cancellationToken = default)
{
	ObjectDisposedException.ThrowIf(_disposed, this);
	if (_isRunning) return Task.CompletedTask;

	_botClient = new TelegramBotClient(_config.BotToken);
	_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

	_pollingTask = _botClient.ReceiveAsync(
		updateHandler: HandleUpdateAsync,
		errorHandler: HandleErrorAsync,
		receiverOptions: new ReceiverOptions
		{
			AllowedUpdates = [
				UpdateType.Message,
				UpdateType.CallbackQuery,
				UpdateType.ChannelPost,
				UpdateType.EditedMessage,
				UpdateType.EditedChannelPost
			]
		},
		cancellationToken: _cts.Token);

	_isRunning = true;
	return Task.CompletedTask;
}

public async Task StopAsync(CancellationToken cancellationToken = default)
{
	ObjectDisposedException.ThrowIf(_disposed, this);
	if (!_isRunning) return;

	_cts?.Cancel();
	if (_pollingTask != null)
	{
		try { await _pollingTask; }
		catch (OperationCanceledException) { }
	}
	_cts?.Dispose();
	_cts = null;
	_pollingTask = null;
	_botClient = null;
	_isRunning = false;
}
```

Порядок важен:
1. `Cancel` — сигнал остановки.
2. `await _pollingTask` — ждём, пока receiver завершит текущий update и выйдет из цикла.
3. Очистка полей.
4. `_isRunning = false` — только после реальной остановки.

После этого гарантируем: ни одного нового `MessageReceived`/`FeedReceived` от Telegram-транспорта.

### 3.2. `SentMessageMap` — per-client, ограниченный по размеру

Назначение map'а: при отправке сообщения с inline-кнопками запомнить `telegramMessageId → agentPartyMessageId`. При нажатии пользователем кнопки достать наш `agentPartyMessageId`, чтобы сформировать `Response.To` — агент коррелирует ответ со своим запросом.

Хранение — per-client (`clientId == chatId.ToString()`). Это даёт две вещи:
- Кольцевой буфер считается в пределах клиента, а не глобально. Активный чат не вытесняет кнопки из соседнего.
- Можно прицельно сбросить все живые кнопки для одного клиента (например, при старте новой сессии агента).

Выносим в отдельный internal класс для тестируемости:

```csharp
internal sealed class SentMessageMap
{
	private readonly int _perClientMaxSize;
	private readonly ConcurrentDictionary<string, ClientBuffer> _byClient = new();

	public SentMessageMap(int perClientMaxSize)
	{
		_perClientMaxSize = perClientMaxSize;
	}

	public void Set(string clientId, int telegramMsgId, string agentPartyMsgId)
	{
		var buffer = _byClient.GetOrAdd(clientId, _ => new ClientBuffer(_perClientMaxSize));
		buffer.Set(telegramMsgId, agentPartyMsgId);
	}

	public bool TryGet(string clientId, int telegramMsgId, out string agentPartyMsgId)
	{
		if (_byClient.TryGetValue(clientId, out var buffer))
			return buffer.TryGet(telegramMsgId, out agentPartyMsgId);

		agentPartyMsgId = default!;
		return false;
	}

	public void Clear(string clientId)
	{
		_byClient.TryRemove(clientId, out _);
	}

	private sealed class ClientBuffer
	{
		private readonly ConcurrentDictionary<int, string> _map = new();
		private readonly ConcurrentQueue<int> _order = new();
		private readonly int _maxSize;

		public ClientBuffer(int maxSize)
		{
			_maxSize = maxSize;
		}

		public void Set(int telegramMsgId, string agentPartyMsgId)
		{
			_map[telegramMsgId] = agentPartyMsgId;
			_order.Enqueue(telegramMsgId);

			while (_map.Count > _maxSize && _order.TryDequeue(out var old))
				_map.TryRemove(old, out _);
		}

		public bool TryGet(int telegramMsgId, out string agentPartyMsgId)
			=> _map.TryGetValue(telegramMsgId, out agentPartyMsgId!);
	}
}
```

FIFO по порядку вставки в рамках клиента, не настоящий LRU. Для нашего use case разницы нет.

Telegram `message_id` уникален в рамках чата, и один и тот же ID дважды не добавится (новое сообщение → новый id). Гонки `Set` → `TryDequeue` другого ключа нет: каждая запись уникальна.

Размер per-client — настраивается через конфиг (см. DEC-1).

### 3.2.1. Нажатие кнопки: silent ignore при промахе

Пользователь может нажать кнопку несколько раз. Telegram сам не «гасит» inline-кнопки. Варианты, как реагировать на повторное нажатие:

- Чистить запись при первом нажатии → второе нажатие не найдёт mapping. Но тогда любой дубль клика по сети (Telegram иногда шлёт callback повторно) теряется.
- Не чистить → отдаём Response каждый раз, пока запись не вытеснена FIFO. Дубликаты — проблема агента, он должен быть идемпотентен по `(From, Id)`.

Выбираем второй вариант: **не чистим при нажатии, пусть вытесняются FIFO**. Это соответствует уже принятому «агент идемпотентен».

Если записи нет (вытеснена, рестарт процесса, неизвестный callback) — `HandleCallbackQuery` **молча ничего не делает**: нет `MessageReceived`, нет лога, нет ответа Telegram'у кроме обязательного `answerCallbackQuery`. Никакого fallback на `telegramMsgId.ToString()`.

### 3.2.2. Явная очистка: `ClearSentMessagesForClient`

Публичный метод на `TelegramServer`:

```csharp
public void ClearSentMessagesForClient(string clientId)
{
	_sentMessages.Clear(clientId);
}
```

Типичный сценарий: агент решает начать новую сессию с пользователем, не хочет, чтобы нажатие на старую кнопку подняло `MessageReceived`. Чистит map для клиента — все нажатия по старым кнопкам теперь silent-ignore.

Старые сообщения в Telegram остаются видимыми, кнопки остаются кликабельными. Если агент хочет и скрыть UI — отдельно редактирует сообщения (это уже agent-level, вне scope).

### 3.3. Поведение при рестарте

Map живёт только в памяти процесса. Никакой дисковой персистенции. После рестарта map пустой.

При нажатии кнопки, отправленной до рестарта → `TryGet` не найдёт запись → silent ignore (см. 3.2.1). Для пользователя это выглядит как «нажал — ничего не произошло», что корректно: старая сессия агента закрыта.

**Решение:** документировать в README в секции "Lifecycle & Edge Cases":

> Inline-кнопки связаны с жизненным циклом процесса. После рестарта агента кнопки из прошлой сессии перестают работать — нажатие игнорируется. Для явного сброса в пределах одного процесса используйте `TelegramServer.ClearSentMessagesForClient(clientId)`.

См. DEC-2.

### 3.4. `_isRunning` и in-flight sends

`SendAsync` проверяет `_isRunning` в начале. После успешной проверки между нами и `botClient.SendMessage` может произойти `StopAsync` (cancel token). Тогда `SendMessage` кинет `OperationCanceledException`. Это приемлемо — контракт: отправка, пересекающаяся со Stop, даёт исключение.

---

## 4. Изменения в коде

### `TelegramServer.cs`

1. Добавить поле `private Task? _pollingTask;`.
2. Заменить `ConcurrentDictionary<int, string> _sentMessageMap` на `SentMessageMap _sentMessages`. Инициализация в конструкторе по `config.SentMessagesPerClientMaxSize`.
3. Заменить `StartReceiving` на `ReceiveAsync` через новые protected virtual методы (см. 4.3).
4. Переписать `StopAsync` как `async` с `await _pollingTask`.
5. Сделать `HandleUpdateAsync`, `HandleCallbackQuery`, `HandleFeedUpdate` → `internal` (для тестов routing'а).
6. В `HandleCallbackQuery`:
   - Вместо `_sentMessageMap.TryGetValue(tgId, out originalId)` вызываем `_sentMessages.TryGet(clientId, tgId, out originalId)`.
   - Если `TryGet` вернул false — `return` сразу (silent ignore). Никакого fallback на `telegramMsgId.ToString()`, никакого `MessageReceived`.
   - `answerCallbackQuery` шлём всегда — чтобы Telegram не крутил loading-индикатор у клиента.
7. В `SendAsync` заменить lambda `trackSentMessage` на `(tgId, apId) => _sentMessages.Set(clientId, tgId, apId)`.
8. Добавить публичный метод `public void ClearSentMessagesForClient(string clientId)` — делегирует в `_sentMessages.Clear(clientId)`.

### Точки для тестируемости (новое)

Добавить два `protected virtual` метода, чтобы тесты могли подменить реальный Telegram bot client и polling:

```csharp
protected virtual ITelegramBotClient CreateBotClient(string token)
	=> new TelegramBotClient(token);

protected virtual Task RunPollingAsync(
	ITelegramBotClient botClient,
	ReceiverOptions options,
	CancellationToken cancellationToken)
	=> botClient.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, options, cancellationToken);
```

В `StartAsync` теперь:

```csharp
_botClient = CreateBotClient(_config.BotToken);
_cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
_pollingTask = RunPollingAsync(_botClient, options, _cts.Token);
_isRunning = true;
```

В тестах — subclass `TestableTelegramServer`, переопределяющий оба метода. `CreateBotClient` возвращает fake `ITelegramBotClient` (достаточно минимальной реализации — для lifecycle-тестов методы не дёргаются). `RunPollingAsync` возвращает управляемую `TaskCompletionSource`.

### `Telegram/SentMessageMap.cs` — новый файл

Класс из п.3.2.

### `TelegramServerConfig.cs`

Добавить одно поле:

```csharp
public int SentMessagesPerClientMaxSize { get; set; } = 100;
```

Ограничение — сколько «живых» кнопок помнит процесс в пределах одного чата. Старые вытесняются FIFO. Backward-compatible: значение по умолчанию.

### `AgentParty.csproj` / `AgentParty.Tests.csproj`

Добавить `InternalsVisibleTo` для тестового проекта, чтобы `SentMessageMap` и internal-методы `TelegramServer` были видны:

```xml
<ItemGroup>
	<InternalsVisibleTo Include="AgentParty.Tests" />
</ItemGroup>
```

---

## 5. Тесты

### 5.1. `Tests/SentMessageMapTests.cs` (новый файл)

1. **Set_TryGet_ReturnsValue** — базовый smoke.
2. **TryGet_UnknownClient_ReturnsFalse** — клиент не существует в map'е.
3. **TryGet_UnknownMessageId_ReturnsFalse** — клиент есть, но такого telegramMsgId у него нет.
4. **Set_UpdatesExistingKey** — повторный Set на тот же (clientId, telegramMsgId) обновляет value.
5. **Set_ExceedsPerClientMaxSize_EvictsOldest** — perClientMax=3, вставляем 4 записи для одного клиента, первая вытеснена.
6. **Set_TwoClients_IndependentBuffers** — perClientMax=2, заполняем оба клиента до предела, проверяем, что вытеснение в одном не трогает другой.
7. **Clear_RemovesAllEntriesForClient** — добавляем пары в двух клиентов, `Clear(a)` → все записи `a` исчезли, `b` не тронут.
8. **Clear_UnknownClient_DoesNotThrow**.
9. **Set_ConcurrentAdds_DoesNotThrow** — 10 потоков × 1000 Set'ов (разные clientId/tgId), операции не бросают.

### 5.2. `Tests/TelegramServerLifecycleTests.cs` (новый файл)

Использует `TestableTelegramServer : TelegramServer` с override `CreateBotClient` (минимальный fake) и `RunPollingAsync` (управляемая TCS, заверишается только по CancellationToken).

1. **StartAsync_CreatesPollingTask** — после `StartAsync` polling запущен (TCS не завершён).
2. **StopAsync_CancelsAndWaitsForPolling** — `StopAsync` не возвращается до тех пор, пока polling task не завершится.
3. **StopAsync_AfterStart_SetsNotRunning** — после `StopAsync` повторный `StartAsync` успешно создаёт новый polling task.
4. **StopAsync_WhenNotRunning_IsNoop** — вызов без предварительного Start.
5. **StartAsync_IsIdempotent** — два Start подряд — один polling task.
6. **Dispose_WhileRunning_StopsPolling** — Dispose дожидается polling task.

### 5.3. `Tests/TelegramServerRoutingTests.cs` (новый файл)

Подаём заготовленные `Update` объекты напрямую в `HandleUpdateAsync` (через internal visibility). `botClient` параметр — `null!` (не используется в теле).

1. **PrivateChat_FromAllowedUser_FiresMessageReceived**.
2. **PrivateChat_FromDisallowedUser_Dropped** (AllowedUserIds = {1}, From.Id = 2).
3. **PrivateChat_EmptyAllowedUserIds_AcceptsAll**.
4. **GroupChat_NotInFeedSources_Dropped**.
5. **GroupChat_InFeedSources_FiresFeedReceived**.
6. **GroupChat_WithThreadId_MatchesExactFeedSource** — `(chatId, null)` не матчит `(chatId, 2736)`.
7. **GroupChat_FeedDiscoveryMode_AcceptsAll**.
8. **ChannelPost_FiresFeedReceived** (update.ChannelPost вместо update.Message).
9. **CallbackQuery_WithKnownMapping_UsesAgentPartyId** — предварительно вызываем `_sentMessages.Set(clientId, 42, "confirm-001")`, затем CallbackQuery с messageId=42 → `MessageReceived` поднят, `Response.To = "confirm-001"`.
10. **CallbackQuery_WithoutMapping_IsSilentlyIgnored** — нет записи → `MessageReceived` не поднимается, исключений нет.
11. **CallbackQuery_AfterClearForClient_IsSilentlyIgnored** — Set, затем `ClearSentMessagesForClient(clientId)`, затем CallbackQuery на этот id → silent ignore.
12. **CallbackQuery_ClearDoesNotAffectOtherClient** — два клиента с записями, `ClearSentMessagesForClient(a)`, CallbackQuery у клиента `b` — всё ещё матчится.
13. **CallbackQuery_SameButtonPressedTwice_BothFireResponse** — второе нажатие тоже поднимает `MessageReceived` (запись не удаляется при первом нажатии).
14. **CallbackQuery_ListActionFormat_ParsedCorrectly** — `callback_data = "item1:create"` → Response.Items = [{Id: "item1", Action: "create"}].
15. **FeedUpdate_NoText_Dropped** (ни text, ни caption, ни document.FileName).
16. **FeedUpdate_FallsBackToCaption** и **FallsBackToDocumentFileName**.

### 5.4. Existing тесты

`ConsoleTransportTests`, `FileTransportTests`, `RouterTests` — не затрагиваются.

---

## 6. Зафиксированные решения

- **DEC-1.** Размер map'а — параметр в `TelegramServerConfig` (`SentMessagesPerClientMaxSize`, default 100 на клиента). Map per-client. Публичный API расширяется методом `ClearSentMessagesForClient(string)`.
- **DEC-2.** Поведение при нажатии на неизвестную кнопку — silent ignore (нет `MessageReceived`, нет лога). Записи не чистятся при нажатии — только FIFO-вытеснение или явный `ClearSentMessagesForClient`. Map не персистится на диск.
- **DEC-3.** Документируем поведение после рестарта и наличие `ClearSentMessagesForClient` в README — секция "Lifecycle & Edge Cases".
- **DEC-4.** Делаем тестируемость сразу: `protected virtual CreateBotClient` + `protected virtual RunPollingAsync`; internal-видимость `HandleUpdateAsync`/`HandleCallbackQuery`/`HandleFeedUpdate` через `InternalsVisibleTo`. Без этого lifecycle-баг считается не закрытым.

---

## 7. Out of scope

- Markdown в Telegram, rate-limit, 429-retry → ТДД 04.
- Вынос `ITelegramBotClient` factory ради unit-тестов lifecycle → отдельный ТДД (если решим, что нужно).
- Упаковка `message.Id` в `callback_data` (альтернатива mapping'у) → не рассматриваем сейчас, зарубка на будущее.
