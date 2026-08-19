using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Response for log directory information
/// </summary>
public class LogInfoResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
}

/// <summary>
/// Response for log position reset operation
/// </summary>
public class LogPositionResponse
{
    public string Message { get; set; } = string.Empty;
    public long Position { get; set; }
}

/// <summary>
/// Response for log service removal start
/// </summary>
public class LogRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
}

/// <summary>Recovery response for the active log-removal operation.</summary>
public sealed class LogRemovalStatusResponse
{
    public bool IsProcessing { get; set; }

    /// <summary>
    /// The service currently or most recently being removed. Null before any removal has run in
    /// this process.
    /// </summary>
    public string? Service { get; set; }

    /// <summary>
    /// The datasource the current removal is scoped to. Null before progress has been recorded
    /// for a run.
    /// </summary>
    public string? Datasource { get; set; }

    /// <summary>
    /// Operation ID tracked for the current or most recent removal. Null before any removal has
    /// ever started in this process.
    /// </summary>
    public Guid? OperationId { get; set; }
    public int FilesProcessed { get; set; }
    public long LinesProcessed { get; set; }
    public long LinesRemoved { get; set; }

    /// <summary>
    /// Percent complete of the current removal. Null before progress has been recorded for a run.
    /// </summary>
    public double? PercentComplete { get; set; }

    /// <summary>
    /// <see cref="OperationStatus.Running"/> while a removal is in progress. Null when
    /// <see cref="IsProcessing"/> is false.
    /// </summary>
    public OperationStatus? Status { get; set; }

    /// <summary>
    /// Localization key for the current removal stage. Null before progress has been recorded
    /// for a run.
    /// </summary>
    public string? StageKey { get; set; }
    public IReadOnlyDictionary<string, object?> Context { get; set; } = new Dictionary<string, object?>();
}

/// <summary>
/// Response for log entries count
/// </summary>
public class LogEntriesCountResponse
{
    public long Count { get; set; }
}

/// <summary>
/// Per-datasource log entry counts by service, as returned in the grouped listing.
/// </summary>
public class DatasourceServiceCountsResponse
{
    [JsonPropertyName("datasource")]
    public string Datasource { get; set; } = string.Empty;

    [JsonPropertyName("logsPath")]
    public string LogsPath { get; set; } = string.Empty;

    [JsonPropertyName("logsWritable")]
    public bool LogsWritable { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("serviceCounts")]
    public Dictionary<string, ulong> ServiceCounts { get; set; } = new();
}

/// <summary>
/// Log position, size, and ingest-diagnostic counters for one datasource. Property names are
/// pinned with <see cref="JsonPropertyNameAttribute"/> so the wire shape matches the previous
/// anonymous-object response exactly, regardless of the serializer's naming policy.
/// </summary>
public class DatasourceLogPositionResponse
{
    [JsonPropertyName("datasource")]
    public string Datasource { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public long Position { get; set; }

    [JsonPropertyName("totalLines")]
    public long TotalLines { get; set; }

    /// <summary>
    /// True when <see cref="TotalLines"/> stopped at an unreadable source file during the
    /// first-run count, so the total is only a clean prefix rather than the whole file.
    /// </summary>
    [JsonPropertyName("totalLinesPartial")]
    public bool TotalLinesPartial { get; set; }

    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("layout")]
    public string Layout { get; set; } = string.Empty;

    [JsonPropertyName("sourceCount")]
    public int SourceCount { get; set; }

    [JsonPropertyName("sourcePositions")]
    public Dictionary<string, long> SourcePositions { get; set; } = new();

    [JsonPropertyName("unparsedLines")]
    public long UnparsedLines { get; set; }

    [JsonPropertyName("hintlessHttpDetailedLines")]
    public long HintlessHttpDetailedLines { get; set; }

    [JsonPropertyName("invalidEncodingLines")]
    public long InvalidEncodingLines { get; set; }

    [JsonPropertyName("skippedFallbackLines")]
    public long SkippedFallbackLines { get; set; }

    [JsonPropertyName("incompleteFinalRecords")]
    public long IncompleteFinalRecords { get; set; }

    [JsonPropertyName("filesWithErrors")]
    public List<string> FilesWithErrors { get; set; } = new();

    [JsonPropertyName("lastRunTerminalStatus")]
    public string LastRunTerminalStatus { get; set; } = string.Empty;

    /// <summary>
    /// Set while this datasource has no access-log sources at all on disk. Null once at least
    /// one source is present.
    /// </summary>
    [JsonPropertyName("missingSourcesMessage")]
    public string? MissingSourcesMessage { get; set; }
}

/// <summary>
/// Snapshot of the current or most recent log-processing run for clients that poll instead of
/// relying on SignalR.
/// </summary>
public class LogProcessingStatusResponse
{
    public bool IsProcessing { get; set; }
    public bool SilentMode { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Operation ID for the current or most recent run. Null before any processing run has ever
    /// started in this process.
    /// </summary>
    public Guid? OperationId { get; set; }

    /// <summary>
    /// Percent complete of the active run. Null when <see cref="IsProcessing"/> is false, or
    /// while the run has not yet produced its first progress file.
    /// </summary>
    public double? PercentComplete { get; set; }

    /// <summary>
    /// Megabytes processed so far. Null when <see cref="IsProcessing"/> is false, or while the
    /// run has not yet produced its first progress file.
    /// </summary>
    public double? MbProcessed { get; set; }

    /// <summary>
    /// Total megabytes discovered across all log files for this run. Null when
    /// <see cref="IsProcessing"/> is false, or while the run has not yet produced its first
    /// progress file.
    /// </summary>
    public double? MbTotal { get; set; }

    /// <summary>
    /// Log entries saved so far. Null when <see cref="IsProcessing"/> is false, or while the run
    /// has not yet produced its first progress file.
    /// </summary>
    public long? EntriesProcessed { get; set; }

    /// <summary>
    /// Total lines discovered across all log files for this run. Null when
    /// <see cref="IsProcessing"/> is false, or while the run has not yet produced its first
    /// progress file.
    /// </summary>
    public long? TotalLines { get; set; }

    /// <summary>
    /// Localization key for the current processing stage. Null when <see cref="IsProcessing"/>
    /// is false, or while the run has not yet produced its first progress file.
    /// </summary>
    public string? StageKey { get; set; }
}
