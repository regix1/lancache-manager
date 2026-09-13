using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

public sealed record ScheduledPrefillSnapshot
{
    public Guid EventEpoch { get; init; } = Guid.NewGuid();
    public long EventSequence { get; init; }
    public string? DaemonInstanceId { get; init; }
    public string Stage { get; init; } = "starting";
    public string Message { get; init; } = string.Empty;
    public string? StageKey { get; init; }
    public IReadOnlyDictionary<string, object?>? StageContext { get; init; }
    public double? PercentComplete { get; init; }
    public long? BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public string? DownloadSessionId { get; init; }
    public string? NeedsLoginReason { get; init; }
    public bool Recovering { get; init; }
}

/// <summary>
/// One due platform's slice of a scheduled prefill run: the config it prefills, the tracked
/// operation whose id keys its notification card and whose token stops this platform alone, the
/// run-level id that tells one run from the next, and the live display state above. Passed as one
/// value because every progress line a service emits needs all four, and threading them separately
/// through the run path put four more parameters on a dozen call sites. [19][25]
/// </summary>
public sealed record ScheduledPrefillServiceRun(
    ScheduledPrefillServiceConfigDto ServiceConfig,
    Guid OperationId,
    string OperationIdString,
    string RunOperationId,
    ScheduledPrefillServiceRunState State,
    CancellationToken Token,
    DaemonRun? RestoredRun = null,
    Guid ClaimId = default);
