using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Json;

/// <summary>
/// Reads GUID values tolerantly: returns Guid.NewGuid() for malformed or legacy string IDs
/// that cannot be parsed as a GUID. Writes canonical GUID strings.
///
/// Used for OperationState.Id where historical persisted data contained non-GUID string IDs
/// prior to the 2026-04-18 enum refactor.
/// </summary>
public sealed class TolerantGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return Guid.NewGuid();

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
                return Guid.NewGuid();
            return Guid.TryParse(value, out var parsed) ? parsed : Guid.NewGuid();
        }

        // Unexpected token — skip to a safe default rather than throw
        return Guid.NewGuid();
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
