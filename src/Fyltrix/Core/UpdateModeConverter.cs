using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fyltrix.Core;

public sealed class UpdateModeConverter : JsonConverter<UpdateMode>
{
    public override UpdateMode Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number) && Enum.IsDefined(typeof(UpdateMode), number)) return (UpdateMode)number;
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<UpdateMode>(reader.GetString(), true, out var named) && Enum.IsDefined(named)) return named;
        using var ignored = JsonDocument.ParseValue(ref reader);
        return ApplicationDefaults.Updates; // A malformed new option must not discard other existing preferences.
    }
    public override void Write(Utf8JsonWriter writer, UpdateMode value, JsonSerializerOptions options) => writer.WriteNumberValue((int)value);
}
