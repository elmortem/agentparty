namespace AgentParty.Telegram;

public readonly struct FeedSource : IEquatable<FeedSource>
{
    public long ChatId { get; }
    public int? ThreadId { get; }

    public FeedSource(long chatId, int? threadId = null)
    {
        ChatId = chatId;
        ThreadId = threadId;
    }

    public static FeedSource Parse(string s)
    {
        var slashIndex = s.IndexOf('/');
        if (slashIndex < 0)
            return new FeedSource(long.Parse(s));

        var chatId = long.Parse(s.AsSpan(0, slashIndex));
        var threadId = int.Parse(s.AsSpan(slashIndex + 1));
        return new FeedSource(chatId, threadId);
    }

    public override string ToString() =>
        ThreadId.HasValue ? $"{ChatId}/{ThreadId.Value}" : ChatId.ToString();

    public bool Equals(FeedSource other) =>
        ChatId == other.ChatId && ThreadId == other.ThreadId;

    public override bool Equals(object? obj) =>
        obj is FeedSource other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(ChatId, ThreadId);

    public static bool operator ==(FeedSource left, FeedSource right) => left.Equals(right);
    public static bool operator !=(FeedSource left, FeedSource right) => !left.Equals(right);
}
