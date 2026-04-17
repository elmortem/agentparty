using System.Collections.Concurrent;
using System.Diagnostics;
using AgentParty.Telegram;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace AgentParty.Tests;

public class TelegramRateLimiterTests
{
    [Fact]
    public async Task Execute_PassesThrough_WhenNoThrottle()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 100 });
        var result = await limiter.ExecuteAsync<int>(1L, ct => Task.FromResult(42), default);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Execute_PerChatLimit_SerializesCalls()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 1 });
        var callTimes = new ConcurrentBag<DateTime>();

        var t1 = limiter.ExecuteAsync<int>(1L, ct => { callTimes.Add(DateTime.UtcNow); return Task.FromResult(1); }, default);
        var t2 = limiter.ExecuteAsync<int>(1L, ct => { callTimes.Add(DateTime.UtcNow); return Task.FromResult(2); }, default);
        await Task.WhenAll(t1, t2);

        var sorted = callTimes.OrderBy(t => t).ToList();
        Assert.True((sorted[1] - sorted[0]).TotalSeconds >= 0.9,
            $"Expected >= 0.9s between calls, got {(sorted[1] - sorted[0]).TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Execute_DifferentChats_Parallel()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 1 });
        var sw = Stopwatch.StartNew();
        var t1 = limiter.ExecuteAsync<int>(1L, ct => Task.FromResult(1), default);
        var t2 = limiter.ExecuteAsync<int>(2L, ct => Task.FromResult(2), default);
        await Task.WhenAll(t1, t2);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(0.5),
            $"Expected < 0.5s for different chats, got {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Execute_GlobalLimit_BlocksSurplus()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 2, PerChatPerSecond = 100 });
        var callTimes = new ConcurrentBag<DateTime>();

        var tasks = new[]
        {
            limiter.ExecuteAsync<int>(1L, ct => { callTimes.Add(DateTime.UtcNow); return Task.FromResult(1); }, default),
            limiter.ExecuteAsync<int>(2L, ct => { callTimes.Add(DateTime.UtcNow); return Task.FromResult(2); }, default),
            limiter.ExecuteAsync<int>(3L, ct => { callTimes.Add(DateTime.UtcNow); return Task.FromResult(3); }, default)
        };
        await Task.WhenAll(tasks);

        var sorted = callTimes.OrderBy(t => t).ToList();
        Assert.True((sorted[2] - sorted[0]).TotalSeconds >= 0.9,
            $"Expected >= 0.9s between first and third call, got {(sorted[2] - sorted[0]).TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Execute_Retries429_UsesRetryAfter()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 100, MaxRetries = 3 });
        int calls = 0;
        var sw = Stopwatch.StartNew();
        var result = await limiter.ExecuteAsync<int>(1L, ct =>
        {
            calls++;
            if (calls == 1)
                throw new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 1 });
            return Task.FromResult(42);
        }, default);
        sw.Stop();
        Assert.Equal(42, result);
        Assert.Equal(2, calls);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1),
            $"Expected >= 1s for Retry-After=1, got {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task Execute_429_ExceedsMaxRetries_Throws()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 100, MaxRetries = 2 });
        var ex = await Assert.ThrowsAsync<ApiRequestException>(async () =>
            await limiter.ExecuteAsync<int>(1L, ct =>
                throw new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 0 }),
                default));
        Assert.Equal(429, ex.ErrorCode);
    }

    [Fact]
    public async Task Execute_RetriesHttpRequestException()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 100, MaxRetries = 3 });
        int calls = 0;
        var result = await limiter.ExecuteAsync<int>(1L, ct =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("Connection reset");
            return Task.FromResult(42);
        }, default);
        Assert.Equal(42, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Execute_NonRetryableException_Throws()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 100 });
        int calls = 0;
        await Assert.ThrowsAsync<ApiRequestException>(async () =>
            await limiter.ExecuteAsync<int>(1L, ct =>
            {
                calls++;
                throw new ApiRequestException("Bad Request", 400);
            }, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Execute_RespectsCancellationToken()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 1 });

        // Consume the per-chat token
        await limiter.ExecuteAsync<int>(1L, ct => Task.FromResult(1), default);

        // Next call should wait for ~1s; cancel it after 100ms
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.ExecuteAsync<int>(1L, ct => Task.FromResult(2), cts.Token));
    }

    [Fact]
    public async Task Execute_NullChatId_UsesOnlyGlobal()
    {
        using var limiter = new TelegramRateLimiter(new TelegramRateLimitOptions { GlobalPerSecond = 100, PerChatPerSecond = 1 });
        var sw = Stopwatch.StartNew();
        var t1 = limiter.ExecuteAsync<int>(null, ct => Task.FromResult(1), default);
        var t2 = limiter.ExecuteAsync<int>(null, ct => Task.FromResult(2), default);
        await Task.WhenAll(t1, t2);
        sw.Stop();
        // Both calls succeed immediately (no per-chat throttle for null chatId)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(0.5),
            $"Expected < 0.5s for null chatId, got {sw.Elapsed.TotalSeconds:F2}s");
    }
}
