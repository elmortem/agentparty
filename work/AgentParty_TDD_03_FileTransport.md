# TDD 03 — FileServer / FileClient: подкаталоги outgoing, cleanup, синхронизация watcher+polling

**Что решаем:** пункты 1.5, 1.6, 1.7 и 4.2 из `AgentParty_Review.md`.

**Scope:** `FileServer.cs`, `FileClient.cs`. Публичный API классов и конфигов не меняется. Меняется внутренняя структура каталогов — breaking change для внешних интеграций, которые читают/пишут файлы напрямую.

**Исходная установка:** один FileServer на одну директорию. Несколько серверов на общий каталог не поддерживаем — это разу упрощает модель.

---

## 1. Проблемы

### 1.5. Двойная доставка при гонке watcher/polling

Пайплайн: `ReadAllText(file)` → `File.Delete(file)` → `MessageReceived?.Invoke(msg)`.

Watcher и polling параллельно заходят в `ProcessIncomingFiles` на одном файле: оба успевают `ReadAllText` (один inode), первый `Delete` — второй ловит `IOException`. Но первое сообщение уже задвоено в памяти (оба `Invoke`).

Внутрипроцессная гонка нашей же системы. Решаем простой сериализацией.

### 1.6. `.tmp` не чистятся

Атомарная запись: `WriteAllText(x.tmp)` → `Move(x.tmp → x.json)`. Если writer упал между ними — `x.tmp` остаётся навечно. За год копится мусор.

### 1.7. FileClient: чужие файлы в общем `outgoing/`

Все клиенты читают общий `outgoing/`. Свои файлы определяются по `ClientId`, чужие — пропускаются, но не удаляются.
- Каждый polling цикл — чтение и десериализация ВСЕХ файлов, в том числе чужих. O(N × M).
- Чужой файл, адресованный несуществующему клиенту, копится вечно.

Решение: подкаталоги `outgoing/{clientId}/`.

### 4.2. CancellationToken не пробрасывается

`WriteAllText`, `ReadAllText`, `Delete` — синхронные. Для локального диска не критично, но формально контракт нарушается.

---

## 2. Цели

1. Файл не обрабатывается дважды в рамках одного процесса при гонке watcher+polling.
2. `.tmp` файлы не накапливаются.
3. Клиенты читают только свои `outgoing/{clientId}/`, нет квадратичной нагрузки.
4. CancellationToken учитывается на файловых операциях через async I/O.

**Вне целей:** устойчивость к kill -9 в середине обработки. Для personal-agent допустим at-most-once в этом окне — сообщения это общение, не транзакции.

---

## 3. Решения

### 3.1. Новая структура каталогов

```
{Directory}/
	incoming/
		*.json
	outgoing/
		{clientId}/
			*.json
	feed/
		*.json
```

Изменения:
- `outgoing/` теперь содержит подкаталоги по `clientId`. Сервер пишет в `outgoing/{clientId}/{guid}.json`. Клиент читает только из `outgoing/{config.ClientId}/`.
- `incoming/` и `feed/` остаются плоскими.

### 3.2. Синхронизация watcher+polling через SemaphoreSlim

Один семафор на директорию чтения. Оба источника — watcher callback и polling Task — проходят через один и тот же семафор с `WaitAsync(0)`: если занят, просто пропускаем тик, тот, кто внутри, всё равно подхватит наш файл.

```csharp
private readonly SemaphoreSlim _incomingLock = new(1, 1);

private async Task ProcessIncomingFilesAsync(CancellationToken ct)
{
	if (!await _incomingLock.WaitAsync(0, ct)) return;
	try
	{
		var files = new DirectoryInfo(_incomingDir)
			.GetFiles("*.json")
			.OrderBy(f => f.CreationTimeUtc)
			.ToArray();

		foreach (var file in files)
		{
			ct.ThrowIfCancellationRequested();

			string content;
			try
			{
				content = await System.IO.File.ReadAllTextAsync(file.FullName, ct);
			}
			catch (FileNotFoundException) { continue; }
			catch (IOException) { continue; }

			try { System.IO.File.Delete(file.FullName); }
			catch (FileNotFoundException) { }
			catch (IOException) { continue; }

			// parse & invoke — вне catch I/O
			HandleIncomingContent(content);
		}
	}
	finally
	{
		_incomingLock.Release();
	}
}
```

Watcher callback и polling loop оба вызывают `ProcessIncomingFilesAsync`. Кто первый взял семафор — обрабатывает всю директорию. Остальные просто возвращаются, не ждут. Потерянного файла нет — тот, кто внутри, увидит всё содержимое директории за свой проход.

Аналогично для `feed/` — отдельный `_feedLock`.

### 3.3. `.tmp` cleanup на старте

Правило: writer чистит `.tmp` в своих каталогах записи перед стартом.

- `FileServer.StartAsync`: удаляет `outgoing/**/*.tmp` (рекурсивно).
- `FileClient.ConnectAsync`: удаляет `incoming/*.tmp` и `feed/*.tmp`.

```csharp
private static void CleanupTempFiles(string dir, bool recursive)
{
	if (!System.IO.Directory.Exists(dir)) return;
	var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
	foreach (var tmp in System.IO.Directory.GetFiles(dir, "*.tmp", option))
	{
		try { System.IO.File.Delete(tmp); } catch (IOException) { }
	}
}
```

Предполагается single-writer на директорию записи, так что удалять чужой `.tmp` некому.

### 3.4. Async I/O и CancellationToken

Заменяем всю файловую работу:
- `File.WriteAllTextAsync(path, content, ct)`
- `File.ReadAllTextAsync(path, ct)`
- `File.Delete` — sync, быстрый; `ct.ThrowIfCancellationRequested()` перед ним.
- `File.Move` — sync, быстрый.

`SendAsync` становится реально async:

```csharp
public async Task SendAsync(string clientId, IMessage message, CancellationToken cancellationToken = default)
{
	...
	var clientDir = Path.Combine(_outgoingDir, clientId);
	System.IO.Directory.CreateDirectory(clientDir);

	var json = JsonSerializer.Serialize(envelope);
	var guid = Guid.NewGuid().ToString();
	var tmpPath = Path.Combine(clientDir, $"{guid}.tmp");
	var jsonPath = Path.Combine(clientDir, $"{guid}.json");

	await System.IO.File.WriteAllTextAsync(tmpPath, json, cancellationToken);
	System.IO.File.Move(tmpPath, jsonPath);
}
```

### 3.5. FileClient: читать только свой подкаталог

```csharp
_myOutgoingDir = Path.Combine(config.Directory, "outgoing", config.ClientId);
```

Watcher и polling смотрят только сюда. Фильтрация по `ClientId` в десериализации становится ненужной (все файлы в каталоге — для этого клиента), но оставим её как sanity-check с логированием через `IRawLogger`, если прилетит чужой envelope (это баг сервера).

---

## 4. Изменения в коде

### `FileServer.cs`

1. `_outgoingDir` по-прежнему `{Directory}/outgoing` — родитель подкаталогов. `SendAsync` создаёт `outgoing/{clientId}/` по требованию.
2. `_incomingLock`, `_feedLock` — `SemaphoreSlim(1, 1)`.
3. `StartAsync`:
	- Создать каталоги `incoming`, `outgoing`, `feed`.
	- `CleanupTempFiles(outgoing, recursive: true)`.
	- Запустить watcher'ы и polling.
4. Watcher `Created` callback → `ProcessIncomingFilesAsync` под семафором.
5. Polling Task → тот же `ProcessIncomingFilesAsync` под тем же семафором.
6. `SendAsync` — async, пишет в `outgoing/{clientId}/`.

### `FileClient.cs`

1. `_myOutgoingDir = outgoing/{ClientId}`.
2. `_myOutgoingLock` — `SemaphoreSlim(1, 1)`.
3. `ConnectAsync`:
	- Создать каталоги `incoming`, `feed`, `_myOutgoingDir`.
	- `CleanupTempFiles(incoming, recursive: false)`, `CleanupTempFiles(feed, recursive: false)`.
	- Watcher на `_myOutgoingDir`.
4. `SendAsync` (клиент→сервер) — пишет в `incoming/` с атомарной записью, async I/O.
5. `SendFeedAsync` — пишет в `feed/`, async.
6. Watcher+polling на `_myOutgoingDir` — через `_myOutgoingLock`.

### README

Обновить секцию "File Transport / Directory structure":
- новая структура с `outgoing/{clientId}/`;
- явная фраза «один FileServer на директорию».

---

## 5. Тесты

### Существующие тесты в `FileTransportTests.cs`

Должны продолжать проходить. Публичный API не изменился.

### Новые тесты

1. **Server_CleansUpTmpFilesInOutgoingOnStart** — создаём `outgoing/client-x/stale.tmp`, стартуем сервер, файл удалён.
2. **Client_CleansUpTmpFilesInIncomingOnConnect** — создаём `incoming/stale.tmp`, `ConnectAsync`, файл удалён.
3. **Server_ConcurrentProcessCalls_NoDuplicateDelivery** — вручную вызываем `ProcessIncomingFilesAsync` из двух Task'ов параллельно на один файл, `MessageReceived` сработал ровно один раз.
4. **Server_WritesToClientSubdirectory** — после `SendAsync("client-a", msg)` файл лежит в `outgoing/client-a/`, не в `outgoing/`.
5. **Client_DoesNotSeeOtherClientsSubdir** — создаём `outgoing/client-b/something.json` вручную, клиент `client-a` не должен его увидеть.
6. **SendAsync_Cancelled_DoesNotWriteFile** — уже отменённый ct → `.json` не появляется.

Существующий `Client_IgnoresMessagesForOtherClients` — остаётся, семантика та же (теперь это sanity для случая «сервер положил не в ту подпапку»).

---

## 6. Зафиксированные решения

- **DEC-1.** Схема каталогов меняется без миграции, только заметка в README. Версия 0.7, API ещё нестабильный.
- **DEC-2.** Поддержку нескольких FileServer на одну директорию не заявляем и не тестируем. Модель — один writer-сервер, N клиентов. Если кто-то запустит второй сервер — поведение не определено.
- **DEC-3.** Хелперы `CleanupTempFiles` — простой `static`-метод в `FileServer` (и переиспользуется из `FileClient` через `internal`). Отдельный класс `FileDirectory` больше не нужен — кода стало мало.
- **DEC-4.** Polling остаётся как fallback (watcher может пропустить события при burst'ах). Интервал по умолчанию — текущий.

---

## 7. Out of scope

- Разделение пакетов (core / file / telegram) — ТДД позже.
- Поддержка media/attachments — отложено.
- Timestamp формат (Unix vs ISO) — ТДД 05.
