using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Telegram.Bot.Exceptions;

namespace AgentParty.Telegram;

public sealed class TelegramRateLimiter : IDisposable
{
    private readonly TelegramRateLimitOptions _options;
    private readonly IRawLogger? _rawLogger;
    private readonly TokenBucketRateLimiter _global;
    private readonly ConcurrentDictionary<long, TokenBucketRateLimiter> _perChat = new();
    private bool _disposed;

    public TelegramRateLimiter(TelegramRateLimitOptions options, IRawLogger? rawLogger = null)
    {
        _options = options;
        _rawLogger = rawLogger;
        _global = CreateLimiter(options.GlobalPerSecond);
    }

    public async Task<T> ExecuteAsync<T>(long? chatId, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            RateLimitLease? chatLease = null;
            RateLimitLease? globalLease = null;
            try
            {
                if (chatId.HasValue)
                {
                    var chatLimiter = _perChat.GetOrAdd(chatId.Value, _ => CreateLimiter(_options.PerChatPerSecond));
                    chatLease = await chatLimiter.AcquireAsync(1, ct);
                }
                globalLease = await _global.AcquireAsync(1, ct);

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
                await Task.Delay(Backoff(attempt), ct);
                attempt++;
            }
            finally
            {
                chatLease?.Dispose();
                globalLease?.Dispose();
            }
        }
    }

    public Task ExecuteAsync(long? chatId, Func<CancellationToken, Task> action, CancellationToken ct) =>
        ExecuteAsync<bool>(chatId, async c => { await action(c); return true; }, ct);

    private static TokenBucketRateLimiter CreateLimiter(int perSecond) =>
        new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = perSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = perSecond,
            AutoReplenishment = true,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _global.Dispose();
        foreach (var limiter in _perChat.Values)
            limiter.Dispose();
    }
}
