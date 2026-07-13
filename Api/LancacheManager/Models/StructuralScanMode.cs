using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>Requested mode for structural corruption scans.</summary>
[JsonConverter(typeof(StructuralScanModeJsonConverter))]
public enum StructuralScanMode
{
    Full,
    Incremental
}

public static class StructuralScanModeExtensions
{
    public static string ToWireString(this StructuralScanMode mode) => mode switch
    {
        StructuralScanMode.Full => "full",
        StructuralScanMode.Incremental => "incremental",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported structural scan mode")
    };

    public static bool TryParseWire(string? value, out StructuralScanMode mode)
    {
        mode = value switch
        {
            "full" => StructuralScanMode.Full,
            "incremental" => StructuralScanMode.Incremental,
            _ => default
        };
        return value is "full" or "incremental";
    }
}

internal sealed class StructuralScanModeJsonConverter : JsonConverter<StructuralScanMode>
{
    public override StructuralScanMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !StructuralScanModeExtensions.TryParseWire(reader.GetString(), out var mode))
        {
            throw new JsonException("Unknown structural scan mode");
        }

        return mode;
    }

    public override void Write(
        Utf8JsonWriter writer,
        StructuralScanMode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

/// <summary>Actual work selected by the structural scanner for this datasource.</summary>
[JsonConverter(typeof(StructuralEffectiveScanModeJsonConverter))]
public enum StructuralEffectiveScanMode
{
    Full,
    Incremental,
    Baseline
}

public static class StructuralEffectiveScanModeExtensions
{
    public static string ToWireString(this StructuralEffectiveScanMode mode) => mode switch
    {
        StructuralEffectiveScanMode.Full => "full",
        StructuralEffectiveScanMode.Incremental => "incremental",
        StructuralEffectiveScanMode.Baseline => "baseline",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported effective structural scan mode")
    };

    public static bool TryParseWire(string? value, out StructuralEffectiveScanMode mode)
    {
        mode = value switch
        {
            "full" => StructuralEffectiveScanMode.Full,
            "incremental" => StructuralEffectiveScanMode.Incremental,
            "baseline" => StructuralEffectiveScanMode.Baseline,
            _ => default
        };
        return value is "full" or "incremental" or "baseline";
    }
}

internal sealed class StructuralEffectiveScanModeJsonConverter : JsonConverter<StructuralEffectiveScanMode>
{
    public override StructuralEffectiveScanMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !StructuralEffectiveScanModeExtensions.TryParseWire(reader.GetString(), out var mode))
        {
            throw new JsonException("Unknown effective structural scan mode");
        }

        return mode;
    }

    public override void Write(
        Utf8JsonWriter writer,
        StructuralEffectiveScanMode value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}

/// <summary>Publication state of the durable structural baseline.</summary>
[JsonConverter(typeof(StructuralBaselineStatusJsonConverter))]
public enum StructuralBaselineStatus
{
    Stateless,
    Building,
    Ready,
    Incomplete
}

public static class StructuralBaselineStatusExtensions
{
    public static string ToWireString(this StructuralBaselineStatus status) => status switch
    {
        StructuralBaselineStatus.Stateless => "stateless",
        StructuralBaselineStatus.Building => "building",
        StructuralBaselineStatus.Ready => "ready",
        StructuralBaselineStatus.Incomplete => "incomplete",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported structural baseline status")
    };

    public static bool TryParseWire(string? value, out StructuralBaselineStatus status)
    {
        status = value switch
        {
            "stateless" => StructuralBaselineStatus.Stateless,
            "building" => StructuralBaselineStatus.Building,
            "ready" => StructuralBaselineStatus.Ready,
            "incomplete" => StructuralBaselineStatus.Incomplete,
            _ => default
        };
        return value is "stateless" or "building" or "ready" or "incomplete";
    }
}

internal sealed class StructuralBaselineStatusJsonConverter : JsonConverter<StructuralBaselineStatus>
{
    public override StructuralBaselineStatus Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !StructuralBaselineStatusExtensions.TryParseWire(reader.GetString(), out var status))
        {
            throw new JsonException("Unknown structural baseline status");
        }

        return status;
    }

    public override void Write(
        Utf8JsonWriter writer,
        StructuralBaselineStatus value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}
