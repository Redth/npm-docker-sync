using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpmDockerSync.Services;

/// <summary>
/// Converts between boolean/integer JSON values and C# integers.
/// NPM API sometimes returns booleans (true/false) and sometimes integers (0/1).
/// </summary>
public class BoolToIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => 1,
            JsonTokenType.False => 0,
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String => int.TryParse(reader.GetString(), out var result) ? result : 0,
            _ => 0
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
