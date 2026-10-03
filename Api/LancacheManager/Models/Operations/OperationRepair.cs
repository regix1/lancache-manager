using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

public sealed class OperationRepair
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public required Guid Id { get; set; }
    public required OperationType Type { get; set; }
    public required string Name { get; set; }
    public required DateTime StartedAt { get; set; }
    [JsonConverter(typeof(OperationRepairNoticeConverter))]
    public RunNotice? Notice { get; set; }
    public OperationRepairPhase Phase { get; set; } = OperationRepairPhase.Prepared;
    public OperationStatus? Outcome { get; set; }
    public string? Error { get; set; }
    public DateTime? RetryAtUtc { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<OperationRepairSource> Sources { get; set; } = [];
    public CacheRepairTarget? Target { get; set; }
    public CorruptionRepair? Corruption { get; set; }
    public Guid? EvictionScanId { get; set; }
    public bool DatabaseWriteStarted { get; set; }
    public CacheClearingRepair? CacheClearing { get; set; }
    public LogProcessingRepair? LogProcessing { get; set; }
    public LogRemovalRepair? LogRemoval { get; set; }
    public RemovalRepair? Removal { get; set; }
    public GameDetectionMetrics? GameDetection { get; set; }
    public EvictionScanRepair? EvictionScan { get; set; }
    public EvictionRemovalRepair? EvictionRemoval { get; set; }
    /// <summary>The warnings the job's run carries, so a run restored after a restart ends with them.</summary>
    public List<RunWarning>? Warnings { get; set; }
    /// <summary>
    /// The job's run completed, with a warning, while the repair keeps a failed outcome for the part that
    /// failed (a partly cleared cache, a log removal whose nginx reopen failed); a run restored after a
    /// restart completes as it did.
    /// </summary>
    public bool RunCompleted { get; set; }
    /// <summary>
    /// The person stopped the run after its job had saved its own outcome (an eviction scan stopped during its
    /// remove step); a run restored after a restart ends canceled as it did.
    /// </summary>
    public bool RunCancelled { get; set; }
    /// <summary>
    /// The job saved its outcome before more work on the same run (an eviction scan's remove step and tail); the run's
    /// last save clears it. A record restored with it still set was stopped by a restart before that save.
    /// </summary>
    public bool RunContinues { get; set; }
    /// <summary>
    /// Why the run failed after its job saved a successful outcome (an eviction scan whose tail failed); a run restored
    /// after a restart ends failed with it, as it did.
    /// </summary>
    public string? RunError { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationRepairPhase
{
    Prepared,
    Running,
    Repairing,
    Completed
}

public sealed class OperationRepairSource
{
    public required string Datasource { get; set; }
    public string? LogRoot { get; set; }
    public string? CacheRoot { get; set; }
    public string? KeyScheme { get; set; }
    public bool NativeLaunchAuthorized { get; set; }
    public bool NativeCompletionAccepted { get; set; }
    public bool LogRewriteStarted { get; set; }
    public bool LogPositionsKept { get; set; }
    public string? ReceiptPath { get; set; }
    public bool ResetLogPositions { get; set; }
    public bool RefreshDownloads { get; set; }
    public bool ReconcileCache { get; set; }
    public bool RefreshDetection { get; set; }
    public bool InvalidateCorruption { get; set; }
    public bool ApplyCorruptionCandidates { get; set; }
    public List<string> CorruptionCandidateIds { get; set; } = [];
    public CorruptionRemovalCounts? CorruptionCounts { get; set; }
}

public sealed class CacheRepairTarget
{
    public uint? SteamAppId { get; set; }
    public List<uint> SteamDepotIds { get; set; } = [];
    public string? EpicGame { get; set; }
    public string? GameName { get; set; }
    public string? Service { get; set; }
}

public sealed class CorruptionRepair
{
    public required Guid ScanId { get; set; }
    public required int ContractVersion { get; set; }
    public required CorruptionDetectionMethod DetectionMethod { get; set; }
    public required string Service { get; set; }
}

public sealed class CacheClearingRepair : CacheClearingMetrics
{
    public int DatasourcesCleared { get; set; }
    public double? Duration { get; set; }
    public bool FullRepair { get; set; }
}

public sealed class RemovalRepair : RemovalMetrics
{
    public ulong LogEntriesRemoved { get; set; }
    public string? StageKey { get; set; }
}

public sealed class LogProcessingRepair
{
    public long EntriesProcessed { get; set; }
    public long LinesProcessed { get; set; }
    public double? Elapsed { get; set; }
    public string? Message { get; set; }
    public string? StageKey { get; set; }
}

public sealed class LogRemovalRepair
{
    public required string Service { get; set; }
    public string? Datasource { get; set; }
    public int FilesProcessed { get; set; }
    public long LinesProcessed { get; set; }
    public long LinesRemoved { get; set; }
    public int DatabaseRecordsDeleted { get; set; }
    public string? StageKey { get; set; }
}

public sealed class EvictionScanRepair
{
    public int Processed { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }
    public string? DetectionError { get; set; }
    /// <summary>The cache folders the scan did not check, so a scan restored after a restart still names them.</summary>
    public List<string>? UncheckedFolders { get; set; }
}

public sealed class EvictionRemovalRepair
{
    public required EvictionRemovalMetadata Selection { get; set; }
    public required string StageKey { get; set; }
    public int DownloadsRemoved { get; set; }
    public int LogEntriesRemoved { get; set; }
}

public sealed class CorruptionRemovalCounts
{
    public long UrlsRemoved { get; set; }
    public long FilesDeleted { get; set; }
    public long LogLinesRemoved { get; set; }
    public long DownloadsDeleted { get; set; }
    public long LogEntriesDeleted { get; set; }
    public long AlreadyMissing { get; set; }
    public long Healed { get; set; }
    public long BytesFreed { get; set; }
}

internal sealed class OperationRepairNoticeConverter : JsonConverter<RunNotice>
{
    public override RunNotice Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var mode = root.GetProperty(nameof(RunNotice.Mode)).Deserialize<NotificationMode>(options);
        var trigger = root.GetProperty(nameof(RunNotice.Trigger)).Deserialize<RunTrigger>(options);
        var actor = root.TryGetProperty(nameof(RunNotice.Actor), out var actorElement)
            && actorElement.ValueKind != JsonValueKind.Null
                ? actorElement.Deserialize<ScheduleActor>(options)
                : null;
        var restoredOrigin = root.TryGetProperty(
                nameof(RunNotice.RestoredOrigin),
                out var restoredOriginElement)
            && restoredOriginElement.GetBoolean();
        return new RunNotice(mode, trigger, actor, restoredOrigin);
    }

    public override void Write(
        Utf8JsonWriter writer,
        RunNotice value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(RunNotice.Mode));
        JsonSerializer.Serialize(writer, value.Mode, options);
        writer.WritePropertyName(nameof(RunNotice.Trigger));
        JsonSerializer.Serialize(writer, value.Trigger, options);
        writer.WritePropertyName(nameof(RunNotice.Actor));
        JsonSerializer.Serialize(writer, value.Actor, options);
        writer.WriteBoolean(nameof(RunNotice.RestoredOrigin), value.RestoredOrigin);
        writer.WriteEndObject();
    }
}
