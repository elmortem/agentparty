# TDD 05 — Timestamp формат

**Что решаем:** пункт 2.2 из `AgentParty_Review.md`.

**Scope:** `Message.cs`, `FeedMessage.cs`, `UnixDateTimeConverter.cs`. Интерфейсы `IMessage` / `IFeedMessage` — не меняются. Транспорты — не трогаем.

---

## 1. Проблема

Сейчас по одной файловой шине летят два формата timestamp:

```json
// Message
"timestamp": "2026-04-17T09:42:13.1234567Z"

// FeedMessage
"timestamp": 1744881733
```

`Message.Timestamp` — без конвертера, System.Text.Json пишет ISO-8601 с наносекундами.
`FeedMessage.Timestamp` — с `UnixDateTimeConverter`, пишет long Unix seconds.

Непоследовательно.

---

## 2. Цели

1. Один формат timestamp для `Message` и `FeedMessage`.
2. `IMessage.Timestamp` / `IFeedMessage.Timestamp` остаются `DateTime`.
3. Явная UTC-нормализация — не оставляем `Kind=Unspecified` и не протекает Local-время в wire.

---

## 3. Решение

### 3.1. Формат — Unix epoch seconds (long)

```json
"timestamp": 1744881733
```

- Совпадает с тем, как Telegram API отдаёт время.
- Компактно, int, однозначно UTC.
- Точность — до секунды. Порядок сообщений в одну секунду определяется по `Message.Id`, не по timestamp.

### 3.2. `UnixDateTimeConverter` — оставляем, чуть усиливаем

Текущий конвертер уже делает правильное. Усиливаем UTC-гарантии:

```csharp
public sealed class UnixDateTimeConverter : JsonConverter<DateTime>
{
	public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		var seconds = reader.GetInt64();
		return DateTime.UnixEpoch.AddSeconds(seconds);
	}

	public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
	{
		var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
		writer.WriteNumberValue((long)(utc - DateTime.UnixEpoch).TotalSeconds);
	}
}
```

- Read: число → DateTime с `Kind=Utc` (через `DateTime.UnixEpoch.AddSeconds`, у `UnixEpoch` уже `Kind=Utc`).
- Write: любой входной `Kind` нормализуется в UTC, потом считается offset от эпохи. Теряется timezone, но для модели `DateTime` это неизбежно — сознательный компромисс (`DateTimeOffset` не вводим).
- Нестроковый фоллбэк убран — при получении строки конвертер теперь бросает `JsonException`. Это ок, старые ISO-сообщения по этому контракту больше не валидны.

### 3.3. Применение конвертера

```csharp
public class Message : IMessage
{
	...
	[JsonPropertyName("timestamp")]
	[JsonConverter(typeof(UnixDateTimeConverter))]
	public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class FeedMessage : IFeedMessage
{
	...
	[JsonPropertyName("timestamp")]
	[JsonConverter(typeof(UnixDateTimeConverter))]
	public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
```

`FeedMessage` уже был с этим конвертером — оставляем. `Message` — добавляем.

### 3.4. Миграция

Персистенция сообщений между рестартами не предусмотрена (`at-most-once`), старые файлы могут лежать в `feed/` только от предыдущего запуска. После обновления такие файлы с ISO-строкой у `Message.Timestamp` упадут при десериализации.

Политика: без миграции. Заметка в README.

---

## 4. Изменения в коде

1. `src/AgentParty/UnixDateTimeConverter.cs` — правки по 3.2 (UTC-normalization на Write, `sealed`, убираем string-fallback).
2. `src/AgentParty/Message.cs` — добавить `[JsonConverter(typeof(UnixDateTimeConverter))]` над `Timestamp`.
3. `src/AgentParty/FeedMessage.cs` — без изменений по существу (конвертер уже стоит).
4. README — раздел про wire-формат: `timestamp` — Unix epoch seconds (long). Все времена нормализованы в UTC.

---

## 5. Тесты

### 5.1. `Tests/UnixDateTimeConverterTests.cs` (новый файл)

1. **Write_Utc_WritesSeconds** — `new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Utc)` → `1776418933` (или что там точно).
2. **Write_Local_ConvertsToUtcFirst** — `DateTime` с `Kind=Local` — результат соответствует UTC-варианту того же момента.
3. **Write_Unspecified_TreatedAsLocal** — фиксируем поведение `ToUniversalTime()` на `Kind=Unspecified` (он трактует как Local). Тест документирует.
4. **Read_Number_ReturnsUtcDateTime** — `1776418933` → `2026-04-17T09:42:13Z`, `Kind=Utc`.
5. **Read_String_Throws** — `"2026-04-17T09:42:13Z"` → `JsonException` / `InvalidOperationException`.

### 5.2. `Tests/MessageSerializationTests.cs` (новый файл)

1. **Message_Timestamp_RoundtripsAsUnixSeconds** — `Message { Timestamp = ... }` → сериализуем, проверяем, что в JSON `"timestamp": <число>`, обратно десериализуем — поле равно исходному (до секунды).
2. **FeedMessage_Timestamp_RoundtripsAsUnixSeconds** — то же для FeedMessage.

### 5.3. Существующие тесты

Не затрагиваются.

---

## 6. Зафиксированные решения

- **DEC-1.** Формат — Unix epoch seconds (long). И на чтение, и на запись. Читаемость JSON глазами как аргумент не рассматриваем.
- **DEC-2.** Без миграции старых сообщений. Breaking change в wire-формате `Message.Timestamp` (был ISO, стал Unix seconds).
- **DEC-3.** `IMessage.Timestamp` / `IFeedMessage.Timestamp` остаются `DateTime`. На `DateTimeOffset` не переходим — timezone в `DateTime` всё равно теряем, но для personal-agent'а это неактуально.
- **DEC-4.** `UnixDateTimeConverter` — оставляем класс, усиливаем UTC-normalization на Write, убираем string-fallback на Read.

---

## 7. Out of scope

- `ReceivedAt` / множественные timestamps.
- Sub-second точность.
- Timezone на уровне транспорта.
