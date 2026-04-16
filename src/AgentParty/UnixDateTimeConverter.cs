using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentParty;

public class UnixDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return DateTime.UnixEpoch.AddSeconds(reader.GetInt64());

        return reader.GetDateTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue((long)(value - DateTime.UnixEpoch).TotalSeconds);
    }
}
