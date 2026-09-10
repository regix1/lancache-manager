using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

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
    CancellationToken Token);
