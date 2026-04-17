using System.Text.Json;

namespace AgentParty.Tests;

public class UnixDateTimeConverterTests
{
    private static readonly JsonSerializerOptions Opts = new() { Converters = { new UnixDateTimeConverter() } };

    private static string Serialize(DateTime dt) => JsonSerializer.Serialize(dt, Opts);
    private static DateTime Deserialize(long seconds) => JsonSerializer.Deserialize<DateTime>(seconds.ToString(), Opts);

    [Fact]
    public void Write_Utc_WritesSeconds()
    {
        var dt = new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Utc);
        Assert.Equal("1776418933", Serialize(dt));
    }

    [Fact]
    public void Write_IgnoresKind_UsesRawTicks()
    {
        // converter does raw tick subtraction, Kind is irrelevant
        var utc = new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Utc);
        var local = new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Local);
        Assert.Equal(Serialize(utc), Serialize(local));
    }

    [Fact]
    public void Write_Unspecified_TreatedAsLocal()
    {
        var unspecified = new DateTime(2026, 4, 17, 9, 42, 13); // ToUniversalTime treats as local
        var local = new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Local);
        Assert.Equal(Serialize(local), Serialize(unspecified));
    }

    [Fact]
    public void Read_Number_ReturnsUtcDateTime()
    {
        var dt = Deserialize(1776418933);
        Assert.Equal(new DateTime(2026, 4, 17, 9, 42, 13, DateTimeKind.Utc), dt);
        Assert.Equal(DateTimeKind.Utc, dt.Kind);
    }

    [Fact]
    public void Read_IsoString_ReturnsDateTime()
    {
        var dt = JsonSerializer.Deserialize<DateTime>("\"2026-04-17T09:42:13Z\"", Opts);
        Assert.Equal(9, dt.Hour);
        Assert.Equal(42, dt.Minute);
        Assert.Equal(13, dt.Second);
    }
}
