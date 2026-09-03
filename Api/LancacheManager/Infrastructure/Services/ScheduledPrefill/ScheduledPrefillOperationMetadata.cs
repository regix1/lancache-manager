using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Run-level display state for the aggregate scheduled-prefill operation. Visibility is fixed for
/// the whole run, so a silent platform can neither remove a visible sibling's card nor make the
/// terminal event disagree with the Started event. The run-status endpoint reads this concurrently
/// with the run advancing, so the value uses volatile reads and writes.
/// </summary>
public sealed class ScheduledPrefillOperationMetadata
{
    private int _showNotification;

    public ScheduledPrefillOperationMetadata(bool showNotification)
    {
        _showNotification = showNotification ? 1 : 0;
    }

    /// <summary>
    /// True when this run should appear in the universal notification bar.
    /// </summary>
    public bool ShowNotification
    {
        get => Volatile.Read(ref _showNotification) == 1;
        set => Volatile.Write(ref _showNotification, value ? 1 : 0);
    }
}

/// <summary>
/// Live display state of ONE platform's scheduled prefill, carried on that platform's own tracked
/// operation. Two things need it: the run-status endpoint, which rebuilds that platform's
/// notification card after a page reload, and the run-status lookup on the Schedules card, which
/// tells a per-platform operation apart from the run-level one so several platforms running at once
/// cannot make it report the wrong operation. The platform's run writes it while the endpoint reads
/// it, so each field uses volatile reads and writes. [19][25][28]
/// </summary>
public sealed class ScheduledPrefillServiceRunState
{
    private string _stage = "starting";
    private string _message = string.Empty;
    private string? _stageKey;
    private double _percentComplete;

    public ScheduledPrefillServiceRunState(PrefillPlatform serviceId)
    {
        ServiceId = serviceId;
    }

    /// <summary>The platform this operation prefills.</summary>
    public PrefillPlatform ServiceId { get; }

    /// <summary>The stage the last progress event reported, e.g. "running" or "needs-login".</summary>
    public string Stage => Volatile.Read(ref _stage);

    /// <summary>The English sentence that event put on the card.</summary>
    public string Message => Volatile.Read(ref _message);

    /// <summary>The i18n key naming that same sentence, null when the text has no key.</summary>
    public string? StageKey => Volatile.Read(ref _stageKey);

    /// <summary>The percent the card's bar was last moved to.</summary>
    public double PercentComplete => Volatile.Read(ref _percentComplete);

    /// <summary>
    /// Records what the platform's latest progress event put on its card. A null
    /// <paramref name="percentComplete"/> leaves the bar where it was: the events that omit it are
    /// not claiming the run went backwards, they simply have no new percent to report.
    /// </summary>
    public void Record(string stage, string message, string? stageKey, double? percentComplete)
    {
        Volatile.Write(ref _stage, stage);
        Volatile.Write(ref _message, message);
        Volatile.Write(ref _stageKey, stageKey);

        if (percentComplete.HasValue)
        {
            Volatile.Write(ref _percentComplete, percentComplete.Value);
        }
    }
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
    CancellationToken Token);
