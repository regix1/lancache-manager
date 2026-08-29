using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Mirror of the Rust log processor's polled progress JSON. The final write doubles as the run's
/// terminal checkpoint: exit code 0 without a valid one is treated as a failure.
/// </summary>
public class LogProcessingProgress : RustProgressBase
{
    /// <summary>
    /// Only meaningful on the final "completed" write: the Rust processor no longer
    /// runs a line-counting pre-pass, so the total is unknown (0) while running.
    /// </summary>
    [JsonPropertyName("total_lines")]
    public long TotalLines { get; set; }

    [JsonPropertyName("lines_parsed")]
    public long LinesParsed { get; set; }

    [JsonPropertyName("entries_saved")]
    public long EntriesSaved { get; set; }

    /// <summary>Raw (compressed) log bytes consumed so far across all files.</summary>
    [JsonPropertyName("bytes_processed")]
    public long BytesProcessed { get; set; }

    /// <summary>Sum of on-disk sizes of every discovered log file.</summary>
    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();

    /// <summary>Contract version of the polled progress file (0 = pre-contract writer).</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// Empty while running; on the final write one of completed | completed_with_warnings |
    /// partial | failed | cancelled. This polled file is the single authority: exit code 0
    /// without a valid terminal checkpoint is treated as a failure.
    /// </summary>
    [JsonPropertyName("terminal_status")]
    public string TerminalStatus { get; set; } = string.Empty;

    /// <summary>Presentation-only source layout: monolithic | bare_metal | mixed.</summary>
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = string.Empty;

    /// <summary>Per-source-stem series line counts as consumed by this run.</summary>
    [JsonPropertyName("source_positions")]
    public Dictionary<string, long> SourcePositions { get; set; } = new();

    [JsonPropertyName("unparsed_lines")]
    public long UnparsedLines { get; set; }

    [JsonPropertyName("hintless_http_detailed_lines")]
    public long HintlessHttpDetailedLines { get; set; }

    [JsonPropertyName("skipped_fallback_lines")]
    public long SkippedFallbackLines { get; set; }

    [JsonPropertyName("invalid_encoding_lines")]
    public long InvalidEncodingLines { get; set; }

    [JsonPropertyName("recognized_ignored_lines")]
    public long RecognizedIgnoredLines { get; set; }

    [JsonPropertyName("incomplete_final_records")]
    public long IncompleteFinalRecords { get; set; }

    [JsonPropertyName("riot_hosts_processed")]
    public long RiotHostsProcessed { get; set; }

    [JsonPropertyName("riot_hosts_mapped")]
    public long RiotHostsMapped { get; set; }

    [JsonPropertyName("files_with_errors")]
    public List<string> FilesWithErrors { get; set; } = new();
}
