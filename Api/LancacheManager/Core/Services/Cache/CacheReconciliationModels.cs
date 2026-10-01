using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Progress data from the Rust eviction scan binary (read from progress JSON file)
/// </summary>
internal class EvictionScanProgressData
{
    public OperationStatus Status { get; set; } = OperationStatus.Pending;

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string StageKey { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
    public double PercentComplete { get; set; }
    public int Processed { get; set; }
    public int TotalEstimate { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }
}

/// <summary>
/// Result from the Rust eviction scan binary (parsed from stdout JSON)
/// </summary>
internal class EvictionScanResult
{
    public bool Success { get; set; }
    public int Processed { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }
    public int FilesOnDisk { get; set; }
    public string? Error { get; set; }
}

internal sealed class CacheRepairDocument
{
    public int Version { get; set; } = 1;
    public required Guid OperationId { get; set; }
    public required List<CacheRepairSource> Sources { get; set; }
    public CacheRepairTarget? Target { get; set; }
}

internal sealed class CacheRepairSource
{
    public required string Name { get; set; }
    public required string CachePath { get; set; }
    public required string KeyScheme { get; set; }
}

/// <summary>
/// What one `cache_purge_log_entries` run removes from a datasource's logs: the lines with a
/// listed URL or depot, or, when <see cref="Service"/> is set, only that service's lines with a
/// listed URL.
/// </summary>
internal sealed record LogPurgeTargets(
    IReadOnlyList<string> Urls,
    IReadOnlyList<long> DepotIds,
    string? Service);

/// <summary>
/// Progress-file schema written by the `cache_purge_log_entries` Rust binary - the same
/// camelCase shape every other cache_* binary writes for the progress-file poller.
/// </summary>
internal sealed class PurgeLogProgressData
{
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }
}

/// <summary>
/// Deserialized report from the `cache_purge_log_entries` Rust binary's output JSON.
/// </summary>
internal sealed class PurgeLogEntriesReport
{
    [System.Text.Json.Serialization.JsonPropertyName("success")]
    public bool Success { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lines_removed")]
    public long LinesRemoved { get; set; }

    /// <summary>
    /// Removed-line count per log-source stem; subtracted from saved ingestion positions
    /// so the purge cannot shift them past unread lines.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("log_lines_removed_by_source")]
    public Dictionary<string, long> LogLinesRemovedBySource { get; set; } = new();

    /// <summary>
    /// The already-read subset of the map above; the amount the saved position comes back by.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("log_lines_removed_before_position_by_source")]
    public Dictionary<string, long> LogLinesRemovedBeforePositionBySource { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("permission_errors")]
    public int PermissionErrors { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; set; }
}
