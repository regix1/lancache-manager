using LancacheManager.Models;
using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services;

internal sealed record DatasourceCorruptionReport(
    string DatasourceName,
    CorruptionReport Report,
    StructuralScanStatusResponse? ScanSummary = null);

/// <summary>JSON model for Rust corruption detection progress.</summary>
public class CorruptionDetectionProgressData
{
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }

    public long FilesProcessed { get; set; }
    public long TotalFiles { get; set; }
    public double PercentComplete { get; set; }
    public string? CurrentFile { get; set; }
    public string? Timestamp { get; set; }
}

/// <summary>JSON model for Rust corruption removal progress.</summary>
internal class CorruptionRemovalProgressData
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }

    [JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("filesProcessed")]
    public int FilesProcessed { get; set; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

/// <summary>Projection of the authoritative cached corruption scan.</summary>
public class CachedCorruptionResult
{
    public bool HasCachedResults { get; set; }
    public Guid ScanId { get; set; }
    public int Threshold { get; set; }
    public int LookbackDays { get; set; }
    public int ContractVersion { get; set; }
    public CorruptionDetectionMethod DetectionMethod { get; set; }
    public StructuralScanMode? ScanMode { get; set; }
    public CorruptionScanSettings Settings { get; set; } = new();
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);
    public CorruptionScanCoverage? Coverage { get; set; }
    public DateTime LastDetectionTime { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
}

/// <summary>Read-only summary of one retained completed corruption scan.</summary>
public sealed class CorruptionScanHistorySummary
{
    public Guid ScanId { get; set; }
    public int ContractVersion { get; set; }
    public CorruptionDetectionMethod DetectionMethod { get; set; }
    public StructuralScanMode? ScanMode { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public CorruptionScanSettings Settings { get; set; } = new();
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);
    public CorruptionScanCoverage? Coverage { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CachedCorruptionProjection
{
    [JsonRequired]
    public CorruptionScanSettings Settings { get; set; } = new();

    [JsonRequired]
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);

    public CorruptionScanCoverage? Coverage { get; set; }
}
