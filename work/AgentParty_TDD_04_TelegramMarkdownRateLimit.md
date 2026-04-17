# TDD 04 — Telegram: Markdown и rate-limit / 429

**Что решаем:** пункты 2.1 и 4.1 из `AgentParty_Review.md`.

**Scope:** `TelegramRenderer.cs`, `TelegramServer.cs`, `TelegramServerConfig.cs`, новый internal `TelegramRateLimiter.cs` (+ возможно wrapper `RateLimitingBotClient.cs`). API публичных классов — совместимо, добавляется несколько настроек.

---

## 1. Проблемы

### 2.1. Markdown в Telegram — несогласованность

Сейчас в `TelegramRenderer`:
- `Text`, `Choice`, action-items в `List` → `SendMessage` без `parseMode` → plain text.
- Info-list → `ParseMode.Html` с `<b>`.

README и оригинальный TDD обещают Markdown. Формат ответа от Telegram-транспорта варьируется в зависимости от типа сообщения — это не про «дизайн», это баг.

### 4.1. Rate-limit и 429 не обрабатываются

Telegram лимит: ~30 msg/s глобально, 1 msg/s на чат (приват), 20 msg/min в группу. При превышении — HTTP 429 c `Retry-After`.

Сейчас `TelegramRenderer.RenderAsync` делает прямой `botClient.SendMessage` без троттлинга и без retry. При всплеске (`List` из 10 action-items за секунду, несколько одновременных сессий агента, подряд `Notification` «thinking» + `Text`) сервер Telegram вернёт 429, и библиотека пробросит `ApiRequestException` в агента. Агенту это неинтересно — это уровень транспорта.

Прочие транзиентные ошибки (`HttpRequestException`, `TaskCanceledException` от сокета) тоже валят вызов без retry.

---

## 2. Цели

1. Единый формат форматирования для всех типов сообщений, отправляемых через Telegram.
2. Transparent 429-retry с учётом `Retry-After` на стороне библиотеки.
3. Per-chat и global троттлинг, чтобы мы сами не привели себя к 429 при всплесках.
4. Ограниченный retry на сетевых транзиентных ошибках.
5. API `TelegramServer` совместим, добавляются настройки в `TelegramServerConfig`.

---

## 3. Решения

### 3.1. Формат — Markdown (legacy)

Выбираем `ParseMode.Markdown` (legacy) для всех исходящих. Аргументы:

- В legacy Markdown служебные символы — только `*`, `_`, `[`, `` ` ``, `\`. Знаки препинания (`.`, `!`, `-`, `(`, `)`, цифры) в «обычном» тексте не требуют экранирования. Фраза «Цена 19.99 руб» уходит как есть.
- Разметка, которую мы используем в рендерере, вся поддерживается: `*bold*`, `_italic_`, `[label](url)`, `` `code` ``.
- `MarkdownV2` строже: требует экранирование 15+ символов в обычном тексте. Для LLM-текста это регулярно ломается. Надёжное автоэкранирование под V2 требует парсера Markdown-контента, что для personal-agent — оверкилл.
- Telegram помечает legacy `Markdown` как устаревший, но он принимается API без ограничений и из планов удаления не заявлен.

Info-list — уже идёт с `ParseMode.Html`. Переводим его на Markdown: `*Title*` вместо `<b>Title</b>`.

README обновляем: фиксируем `ParseMode.Markdown` (legacy) как формат Telegram-транспорта, приводим список спецсимволов и ссылку на хелпер Escape (см. 3.1.1).

#### 3.1.1. Экранирование — где и когда

`Message.Content` улетает в Telegram как есть, с `parseMode: Markdown`. Библиотека не парсит его. Причины:

- Для «обычного» текста (знаки препинания, числа, кириллица, эмодзи) ничего экранировать не надо — это бесплатная инвариантность.
- Агент, который хочет форматирование, шлёт `*жирный*` / `_курсив_` — работает из коробки.
- Агент, который хочет литералы `*`, `_`, `[`, `` ` `` — экранирует сам обратным слешем: `\*`, `\_`, `\[`, `` \` ``.

Для последнего сценария даём статический хелпер:

```csharp
public static class TelegramMarkdown
{
	public static string Escape(string text)
	{
		// Экранируем legacy Markdown spec chars: * _ [ ` \
		var sb = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			if (c is '*' or '_' or '[' or '`' or '\\')
				sb.Append('\\');
			sb.Append(c);
		}
		return sb.ToString();
	}
}
```

Применение — опциональное, на стороне агента, когда он хочет вставить «сырой» кусок пользовательского ввода в сообщение без риска, что тот поломает разметку. Библиотека сама `Escape` не дёргает — иначе теряется форматирование.

Если агент пришлёт сломанный Markdown (например, непарную ``` ` ```) — Telegram вернёт 400 «can't parse entities». Это не transient, retry не помогает, пробрасываем как обычное исключение `SendAsync`. Агент сам виноват.

#### 3.1.2. Изменения в Renderer

- `RenderAsync` (case `Text`): `SendMessage(... parseMode: ParseMode.Markdown)`.
- `RenderChoiceAsync`: `SendMessage(choice.Text, parseMode: ParseMode.Markdown, replyMarkup: ...)`.
- `RenderListAsync`:
  - info-часть: заменить `<b>{Title}</b>` на `*{Title}*`, `parseMode: ParseMode.Markdown`.
  - action-items → `parseMode: ParseMode.Markdown`.
- `RenderNotificationAsync` — `SendChatAction`/`SetMyName`, parseMode не нужен.
- default fallback (text) → тоже `ParseMode.Markdown`.

### 3.2. Rate-limit и retry — архитектура

Вводим internal класс `TelegramRateLimiter`, ответственный за:
- удержание per-chat лимита (1 отправка в `PerChatInterval`, по умолчанию 1 секунда);
- удержание global-лимита (30 отправок в `GlobalInterval`, по умолчанию 1 секунда);
- перехват `ApiRequestException` c code 429 → `Task.Delay(RetryAfter)` → повторить;
- перехват транзиентных `HttpRequestException`/`TaskCanceledException` → экспоненциальный backoff → повторить, ограниченное число раз.

API limiter'а:

```csharp
internal sealed class TelegramRateLimiter
{
	public TelegramRateLimiter(TelegramRateLimitOptions options, IRawLogger? rawLogger = null);

	public Task<T> ExecuteAsync<T>(long? chatId, Func<CancellationToken, Task<T>> action, CancellationToken ct);
	public Task ExecuteAsync(long? chatId, Func<CancellationToken, Task> action, CancellationToken ct);
}
```

`chatId == null` → только global-лимит (для `SetMyName` и т.п., не привязанного к чату).

### 3.3. Интеграция в Renderer

Выбор точки перехвата:

**Вариант A.** Обернуть `ITelegramBotClient` в `RateLimitingBotClient`, подменяющий `MakeRequest`. Перехватывает все API-вызовы автоматически. Плюс: Renderer не меняется. Минус: для извлечения `chatId` из конкретного request приходится либо делать reflection, либо pattern matching по 20+ типам requests Telegram.Bot.

**Вариант B.** Передавать в Renderer `TelegramRateLimiter` и менять каждый вызов `botClient.SendMessage(...)` на `limiter.ExecuteAsync(chatId, ct => botClient.SendMessage(..., ct), ct)`. Минус: чуть меняется сигнатура Renderer'а и все call-site'ы. Плюс: `chatId` в Renderer'е всегда известен явно, без reflection.

Предлагается **Вариант B** — явнее и проще. Меняем сигнатуру:

```csharp
public virtual Task RenderAsync(
	ITelegramBotClient botClient,
	TelegramRateLimiter limiter,
	long chatId,
	IMessage message,
	Action<int, string>? trackSentMessage = null,
	CancellationToken cancellationToken = default);
```

Внутри — каждый `botClient.SendMessage(...)` / `SendChatAction` / `SetMyName` оборачивается в `limiter.ExecuteAsync(chatId, ...)`. `SetMyName` — с `chatId = null` (не привязан к чату).

`TelegramRenderer` — public, сабклассы (если были) сломаются на обновлении. Обратную совместимость не держим — в 0.x это нормально. Старую сигнатуру удаляем, без `[Obsolete]`.

### 3.4. Реализация лимитов

Per-chat — по SemaphoreSlim + таймштамп последней отправки:

```csharp
private sealed class PerChatGate
{
	private readonly SemaphoreSlim _sem = new(1, 1);
	public DateTime NextAllowedAtUtc;
}

private readonly ConcurrentDictionary<long, PerChatGate> _perChat = new();
private readonly SemaphoreSlim _global = new(_options.GlobalConcurrency, _options.GlobalConcurrency);
// global-интервал моделируется через WaitAsync + Task.Delay(interval/concurrency) при релизе.
```

Вариант попроще — `System.Threading.RateLimiting.TokenBucketRateLimiter`:
- global: `TokenLimit=30, ReplenishmentPeriod=1s, TokensPerPeriod=30`.
- per-chat: словарь `ConcurrentDictionary<long, TokenBucketRateLimiter>`; каждый `TokenLimit=1, ReplenishmentPeriod=1s, TokensPerPeriod=1`.

Использование:

```csharp
using var chatLease = await _perChat.GetOrAdd(chatId, CreateChatLimiter).AcquireAsync(1, ct);
using var globalLease = await _global.AcquireAsync(1, ct);
// выполнить action
```

Берём `System.Threading.RateLimiting` — это стандартная библиотека в .NET 8, меньше кода, меньше багов.

Очистка per-chat limiters — простая: никогда, они занимают ~100 байт каждый, при разумном числе клиентов (десятки) — пыль. Если агенту важно — можно добавить LRU, не сейчас.

### 3.5. Retry-логика

Внутри `ExecuteAsync`:

```csharp
int attempt = 0;
while (true)
{
	try
	{
		await AcquireLeases(chatId, ct);
		return await action(ct);
	}
	catch (ApiRequestException ex) when (ex.ErrorCode == 429)
	{
		var delay = TimeSpan.FromSeconds(ex.Parameters?.RetryAfter ?? 1);
		_rawLogger?.Log("TelegramRateLimiter.429", $"retry after {delay.TotalSeconds}s");
		await Task.Delay(delay + TimeSpan.FromMilliseconds(100), ct);
		attempt++;
		if (attempt > _options.MaxRetries) throw;
	}
	catch (HttpRequestException) when (attempt < _options.MaxRetries)
	{
		await Task.Delay(Backoff(attempt), ct);
		attempt++;
	}
	catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _options.MaxRetries)
	{
		// не наш cancel — это timeout сокета
		await Task.Delay(Backoff(attempt), ct);
		attempt++;
	}
}

static TimeSpan Backoff(int attempt) =>
	TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)); // 200, 400, 800, 1600 ms
```

Не-retryable исключения (включая `ApiRequestException` с другим кодом) пробрасываются наружу. `OperationCanceledException` пробрасывается как есть, когда cancelation запрошен снаружи.

### 3.6. Поведение `SendAsync` под нагрузкой

Контракт: `TelegramServer.SendAsync` возвращается, когда сообщение либо реально отправлено, либо retries исчерпаны (throw). На время ожидания токенов и `Retry-After` — просто висит `await`. Параллельные вызовы к разным чатам не блокируют друг друга (global-limit 30/s обычно с запасом).

Очереди в памяти с воркером не заводим — это overkill. Параллельные `SendAsync` просто сериализуются на семафорах лимитера.

### 3.7. Конфиг

Добавляем в `TelegramServerConfig`:

```csharp
public TelegramRateLimitOptions RateLimit { get; set; } = new();
```

```csharp
public class TelegramRateLimitOptions
{
	public int GlobalPerSecond { get; set; } = 30;
	public int PerChatPerSecond { get; set; } = 1;
	public int MaxRetries { get; set; } = 3;
}
```

Дефолты совпадают с Telegram-лимитами. Передаются в `TelegramRateLimiter` в конструкторе.

---

## 4. Изменения в коде

### `TelegramRateLimiter.cs` — новый файл

Класс из 3.2/3.4/3.5. `internal sealed`. Тестируется напрямую через `InternalsVisibleTo`.

### `TelegramRateLimitOptions.cs` — новый файл

Класс из 3.7.

### `TelegramMarkdown.cs` — новый файл

Статический хелпер `Escape(string)` (см. 3.1.1). Public — чтобы агент мог вызывать при необходимости.

### `TelegramServerConfig.cs`

Добавить `RateLimit` property с дефолтом.

### `TelegramRenderer.cs`

1. Единственный `RenderAsync(ITelegramBotClient, TelegramRateLimiter, long, IMessage, ..., ct)` — старую сигнатуру удаляем.
2. Все внутренние методы (`RenderChoiceAsync`, `RenderListAsync`, `RenderNotificationAsync`) принимают `TelegramRateLimiter`.
3. Каждый `botClient.SendMessage(...)` / `SendChatAction` / `SetMyName` → через `limiter.ExecuteAsync(chatId, ct => ...)`.
4. `ParseMode.Markdown` добавлен во все `SendMessage`.
5. Info-list: `<b>{Title}</b>` → `*{Title}*`.

### `TelegramServer.cs`

1. В конструкторе создаём `TelegramRateLimiter _limiter = new(config.RateLimit, rawLogger)`.
2. В `SendAsync` передаём `_limiter` в renderer.
3. `IDisposable`/`IAsyncDisposable` — limiter ничего не держит, `RateLimiter` из .NET 8 — `IDisposable`. Чистим в `Dispose`/`StopAsync`.

---

## 5. Тесты

### 5.1. `Tests/TelegramRateLimiterTests.cs` (новый файл)

1. **Execute_PassesThrough_WhenNoThrottle** — одна операция под low лимитом, выполняется без задержки.
2. **Execute_PerChatLimit_SerializesCalls** — PerChatPerSecond=1, шлём 2 вызова в один chatId — второй выполняется через >=1 сек после первого.
3. **Execute_DifferentChats_Parallel** — 2 chatId, оба шлют параллельно, выполняются без per-chat очереди.
4. **Execute_GlobalLimit_BlocksSurplus** — GlobalPerSecond=2, 3 вызова в разные чаты — третий ждёт >=1s (освобождение токена).
5. **Execute_Retries429_UsesRetryAfter** — action первый раз кидает `ApiRequestException(429, RetryAfter=1)`, второй раз возвращает результат. Общее время >= 1s, результат получен.
6. **Execute_429_ExceedsMaxRetries_Throws** — MaxRetries=2, всегда 429 → бросает `ApiRequestException` наружу после 3 попыток.
7. **Execute_RetriesHttpRequestException** — сетевая ошибка → backoff → успех.
8. **Execute_NonRetryableException_Throws** — `ApiRequestException(code=400)` → бросает сразу, без retry.
9. **Execute_RespectsCancellationToken** — ct отменяется во время ожидания per-chat → `OperationCanceledException`.
10. **Execute_NullChatId_UsesOnlyGlobal** — chatId=null, per-chat gate не трогается.

### 5.2. `Tests/TelegramRendererTests.cs` (новый файл или расширить существующий, если есть)

Использует мок `ITelegramBotClient` или захватывающий `Func`-сабкласс — проверяет, что в вызовы `SendMessage` передаётся `ParseMode.Markdown`.

1. **RenderText_SendsWithMarkdownParseMode**.
2. **RenderChoice_SendsWithMarkdownParseMode_AndInlineKeyboard**.
3. **RenderListInfoItems_UsesMarkdownTitle** — проверяем, что заголовок идёт как `*Title*`, parseMode=Markdown.
4. **RenderListActionItems_EachSendsMarkdown**.
5. **RenderNotificationThinking_SendsTypingAction** — parseMode не проверяем, но убеждаемся, что `SendChatAction` вызван.

### 5.2a. `Tests/TelegramMarkdownTests.cs` (новый файл)

1. **Escape_EscapesSpecChars** — `*_[` `` ` `` `\` → `\*\_\[\`\\`.
2. **Escape_LeavesOtherCharsAsIs** — буквы, цифры, пунктуация, эмодзи остаются без изменений.
3. **Escape_EmptyString_ReturnsEmpty**.

### 5.3. `Tests/TelegramServerRoutingTests.cs` (из ТДД 02)

Не меняется по сути, но `SendAsync` теперь проходит через limiter. Добавляем один smoke:

11. **SendAsync_UsesRateLimiter** — инъектируем fake `TelegramRateLimiter` (через override в `TestableTelegramServer`), убеждаемся, что `ExecuteAsync` вызвался.

---

## 6. Зафиксированные решения

- **DEC-1.** Формат — `ParseMode.Markdown` (legacy). Обычные знаки препинания не экранируются. Разметка: `*bold*`, `_italic_`, `[link](url)`, `` `code` ``.
- **DEC-2.** Библиотека не парсит Content и не экранирует автоматически. Если агент хочет литералы `*_[` `` ` `` `\` — экранирует через публичный хелпер `TelegramMarkdown.Escape(string)`.
- **DEC-3.** Сигнатура `TelegramRenderer.RenderAsync` меняется — принимает `TelegramRateLimiter`. Старая удаляется, `[Obsolete]` не ставим. Версия 0.x, обратную совместимость не держим.
- **DEC-4.** Retry на `HttpRequestException` и `TaskCanceledException` (не наш cancel) — включён, до `MaxRetries` попыток с backoff 200/400/800/1600 мс.
- **DEC-5.** Дефолты лимитов: `GlobalPerSecond = 30`, `PerChatPerSecond = 1`, `MaxRetries = 3`.
- **DEC-6.** Info-list переводим на Markdown: заголовок `*Title*`, строки `• text — details` (без изменений, это plain-text).

---

## 7. Out of scope

- Очередь в памяти с отдельным воркером. Не нужна — await на семафорах покрывает.
- Поддержка Markdown как альтернативного parseMode (config.ParseMode). Пока один формат.
- Media/attachments (3.6 в Review).
- Persist rate-limit state между рестартами. Не нужно, at-most-once приемлем.
