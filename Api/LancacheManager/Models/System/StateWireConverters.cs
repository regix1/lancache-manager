using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Wire converters that let <see cref="AppState"/> serialize straight to state.json in the exact
/// format the old split PersistedState class wrote: enums as their wire strings, with the same
/// lenient read fallbacks (an unrecognized stored string degrades to the default instead of
/// failing the whole state load). Property-level only - the enums keep their normal JSON shape
/// everywhere else.
/// </summary>
public sealed class RefreshRateWireConverter : JsonConverter<RefreshRate>
{
    // A stored JSON null used to bind to a null string and fall back the same way.
    public override bool HandleNull => true;

    public override RefreshRate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RefreshRateExtensions.TryParseWire(reader.TokenType == JsonTokenType.String ? reader.GetString() : null)
            ?? RefreshRate.Standard;

    public override void Write(Utf8JsonWriter writer, RefreshRate value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

public sealed class EvictedDataModeWireConverter : JsonConverter<EvictedDataMode>
{
    public override bool HandleNull => true;

    public override EvictedDataMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EvictedDataModeExtensions.TryParseWire(reader.TokenType == JsonTokenType.String ? reader.GetString() : null)
            ?? EvictedDataMode.Show;

    public override void Write(Utf8JsonWriter writer, EvictedDataMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

public sealed class SetupStepWireConverter : JsonConverter<SetupStep?>
{
    public override SetupStep? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SetupStepExtensions.TryParseWire(reader.TokenType == JsonTokenType.String ? reader.GetString() : null);

    public override void Write(Utf8JsonWriter writer, SetupStep? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToWireString());
    }
}

public sealed class DataSourceChoiceWireConverter : JsonConverter<DataSourceChoice?>
{
    public override DataSourceChoice? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DataSourceChoiceExtensions.TryParseWire(reader.TokenType == JsonTokenType.String ? reader.GetString() : null);

    public override void Write(Utf8JsonWriter writer, DataSourceChoice? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToWireString());
    }
}
