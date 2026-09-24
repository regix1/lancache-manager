using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using System.Collections.ObjectModel;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Marks the aggregate scheduled-prefill operation that groups one tick's platform runs. It carries
/// no visibility of its own and never gets a row: each platform run's own notice decides how that
/// platform shows.
/// </summary>
public sealed class ScheduledPrefillOperationMetadata
{
}

/// <summary>
/// Live display state of ONE platform's scheduled prefill, carried on that platform's own tracked
/// operation. Two things need it: the run-status endpoint, which rebuilds that platform's
/// notification card after a page reload, and the run-status lookup on the Schedules card, which
/// tells a per-platform operation apart from the run-level one so several platforms running at once
/// cannot make it report the wrong operation. The platform's run writes it while the endpoint reads
/// it, so the complete display and its publication sequence share one lock.
/// </summary>
public sealed class ScheduledPrefillServiceRunState
{
    private readonly object _gate = new();
    private ScheduledPrefillSnapshot _snapshot = new();
    private bool _completed;
    private DaemonRun? _run;
    private DaemonSession? _session;

    public ScheduledPrefillServiceRunState(
        PrefillPlatform serviceId,
        Guid scheduleId,
        string name,
        RunNotice notice)
    {
        ServiceId = serviceId;
        ScheduleId = scheduleId;
        Name = name;
        Notice = notice;
    }

    /// <summary>The platform this operation prefills.</summary>
    public PrefillPlatform ServiceId { get; }

    public Guid ScheduleId { get; }

    public string Name { get; }

    /// <summary>The schedule's notification mode and the trigger that started this run.</summary>
    public RunNotice Notice { get; }

    public DateTime? CompletedAtUtc { get; set; }
    public bool Detached { get; set; }

    /// <summary>The stage the last progress event reported, e.g. "running" or "needs-login".</summary>
    public string Stage => Snapshot.Stage;

    /// <summary>The English sentence that event put on the card.</summary>
    public string Message => Snapshot.Message;

    /// <summary>The i18n key naming that same sentence, null when the text has no key.</summary>
    public string? StageKey => Snapshot.StageKey;

    /// <summary>The percent the card's bar was last moved to.</summary>
    public double? PercentComplete => Snapshot.PercentComplete;

    public ScheduledPrefillSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>
    /// Records what the platform's latest progress event put on its card. A null
    /// <paramref name="percentComplete"/> leaves the bar where it was: the events that omit it are
    /// not claiming the run went backwards, they simply have no new percent to report. Set
    /// <paramref name="clearPercent"/> when the event explicitly establishes that no truthful
    /// denominator is available.
    /// </summary>
    public ScheduledPrefillSnapshot? Record(
        string stage,
        string message,
        string? stageKey,
        double? percentComplete,
        bool clearPercent = false,
        Dictionary<string, object?>? stageContext = null,
        long? bytesDownloaded = null,
        long? totalBytes = null,
        string? downloadSessionId = null,
        string? needsLoginReason = null,
        DaemonRun? run = null,
        DaemonSession? session = null,
        bool ordinaryProgress = false,
        bool terminal = false,
        bool resume = false,
        bool started = false)
    {
        lock (_gate)
        {
            if (_completed) return null;
            _run ??= run;
            _session ??= session;
            if (ordinaryProgress && (_snapshot.Stage == "cancelling" || _snapshot.Stage == "recovering" && !resume
                || _run?.Recovering == true || _session?.Recovering == true || _run?.CancelRequested == true))
                return null;

            if (!terminal && !started && stage is ("recovering" or "cancelling") && _snapshot.Stage == stage)
                return null;

            if (terminal && string.IsNullOrEmpty(message))
            {
                message = stage == "cancelled" ? "Prefill stopped" : _snapshot.Message;
                stageKey = stage == "cancelled" ? "signalr.scheduledPrefill.stopped" : _snapshot.StageKey;
                stageContext = _snapshot.StageContext?.ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            _snapshot = _snapshot with
            {
                EventSequence = _snapshot.EventSequence + 1,
                DaemonInstanceId = _run?.DaemonInstanceId ?? _snapshot.DaemonInstanceId,
                Stage = stage,
                Message = message,
                StageKey = stageKey,
                StageContext = stageContext is null ? null
                    : new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(stageContext)),
                PercentComplete = percentComplete ?? (clearPercent ? null : _snapshot.PercentComplete),
                BytesDownloaded = bytesDownloaded ?? _snapshot.BytesDownloaded,
                TotalBytes = totalBytes ?? _snapshot.TotalBytes,
                DownloadSessionId = downloadSessionId ?? _snapshot.DownloadSessionId,
                NeedsLoginReason = needsLoginReason ?? (terminal ? _snapshot.NeedsLoginReason : null),
                Recovering = stage == "recovering"
            };
            _completed = terminal;
            return _snapshot;
        }
    }
}
