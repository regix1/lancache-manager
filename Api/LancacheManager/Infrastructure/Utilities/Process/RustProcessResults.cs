using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Result of a process execution
/// </summary>
public class ProcessExecutionResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Central Rust-failure gate: no-op when <see cref="ExitCode"/> is 0, otherwise throws a typed
    /// <see cref="RustProcessException"/> carrying the exit code and stderr (<see cref="Error"/>).
    /// Replaces the scattered <c>if (result.ExitCode != 0) throw new Exception($"... exit code ...")</c>
    /// checks so every Rust wrapper fails the same way and callers can <c>catch (RustProcessException)</c>.
    /// </summary>
    /// <param name="tool">The Rust binary/tool name for the message (e.g. "cache_cleaner").</param>
    /// <param name="context">Optional user-safe descriptor (e.g. datasource/service name); never stderr.</param>
    /// <param name="cancellationToken">
    /// The token the run was launched with. A killed child and a crashed one both exit non-zero, so
    /// the exit code cannot tell them apart; the token can, and cancelling it is what stops the run.
    /// </param>
    public void EnsureSuccess(string tool, string? context = null, CancellationToken cancellationToken = default)
    {
        if (ExitCode == 0)
        {
            return;
        }

        // A cancelled run is not a failed run. The child can die and WaitForExit return normally
        // before the next token check, so classify here rather than reporting a process failure.
        cancellationToken.ThrowIfCancellationRequested();

        throw new RustProcessException(tool, ExitCode, Error, context);
    }
}

/// <summary>
/// Strongly-typed envelope for the JSON lines Rust's progress_events.rs ProgressReporter emits
/// over stdout (event types "started"/"progress"/"complete" — see that file's header comment for
/// the exact wire shape). PercentComplete is only populated on "progress" events; Success/
/// Cancelled are only populated on "complete" events — the other event types leave them null.
/// Context is a free-form JSON object whose shape varies by StageKey (Rust declares it as
/// serde_json::Value, genuinely heterogeneous even within one binary's stream), so it stays a
/// JsonElement rather than being forced into a fixed shape; callers that need specific fields out
/// of it read them via Context.Value.TryGetProperty(...).
/// </summary>
public class RustProgressEvent
{
    [System.Text.Json.Serialization.JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double? PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string StageKey { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("context")]
    public JsonElement? Context { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("success")]
    public bool? Success { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("cancelled")]
    public bool? Cancelled { get; set; }

    /// <summary>
    /// The full failure reason for "failed"/"complete" events that carry one — Rust's
    /// progress_events.rs emits the whole anyhow chain (<c>format!("{e:#}")</c>) here. Null on
    /// success and on any envelope emitted before the errorDetail field was added, so parsing must
    /// tolerate its absence (it does — nullable + case-insensitive deserialization).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorDetail")]
    public string? ErrorDetail { get; set; }
}

/// <summary>
/// Result of a Rust executable execution with data result
/// </summary>
public class RustExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
}


/// <summary>
/// Final line/file totals produced by log_service_manager count-lines. <see cref="FilesWithErrors"/>
/// is greater than zero when a source stopped at an unreadable member, which marks the line and
/// source totals as a partial clean prefix rather than an authoritative count.
/// </summary>
public sealed record LogLineCountResult(
    long LinesProcessed,
    long FilesProcessed,
    Dictionary<string, long> SourceLineCounts,
    long FilesWithErrors = 0);

/// <summary>Pre-delete byte count produced by log_service_manager delete-file.</summary>
public sealed record LogFileDeletionResult(long BytesDeleted);

/// <summary>
/// Deserialized output of log_service_manager scan-content for one datasource log directory.
/// <see cref="Availability"/> is one of "available" (a live source file was read), "unreadable"
/// (a source file existed but could not be read), or "logMissing" (no sources discovered). The
/// records are the Rust-side positive-cache candidates before the C# security filters run.
/// </summary>
public sealed class RustContentScanResult
{
    /// <summary>Bounded tail budget per source file (32 MiB), matching the Rust default.</summary>
    public const int DefaultMaxTailBytes = 32 * 1024 * 1024;

    /// <summary>Upper bound on candidate records the scan returns, matching the Rust default.</summary>
    public const int DefaultMaxSamples = 5000;

    [JsonPropertyName("availability")]
    public string Availability { get; init; } = "logMissing";

    [JsonPropertyName("scanned_bytes")]
    public long ScannedBytes { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("records")]
    public IReadOnlyList<RustContentSample> Records { get; init; } = Array.Empty<RustContentSample>();
}

/// <summary>
/// One positive-cache candidate emitted by log_service_manager scan-content. <see cref="Target"/>
/// is the request URL exactly as logged (unmodified); the C# caller runs path/SSRF safety and host
/// DNS normalization over these raw fields before any of them is probed.
/// </summary>
public sealed class RustContentSample
{
    [JsonPropertyName("service")]
    public string Service { get; init; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    [JsonPropertyName("cache_status")]
    public string CacheStatus { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = string.Empty;

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; init; } = string.Empty;
}
