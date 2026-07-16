using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Per-run helper that gives a pipeline-less scheduled maintenance service a full operation lifecycle:
/// one tracked operation, an awaited run-started broadcast, monotonic progress broadcasts, and exactly
/// one terminal broadcast. Collapses the boilerplate that would otherwise be duplicated across every
/// such service.
///
/// One instance per run. Ownership contract:
/// <list type="bullet">
/// <item>The reporter creates and owns the run's <see cref="CancellationTokenSource"/> (linked to the
/// caller's stopping token). When the run is started, that CTS is handed to the tracker, which becomes
/// its single disposer. If the run is never started (a prerequisite was not met and the caller returned
/// before <see cref="StartAsync"/>), the reporter disposes the CTS itself.</item>
/// <item>Sends are awaited (started + progress) and serialized through a per-instance semaphore so the
/// tracker's percent and the broadcast percent update together and never reorder.</item>
/// <item>The terminal event is emitted from a single place - the tracker's <c>onTerminalEmit</c> gate -
/// so it fires exactly once regardless of which path completed the operation, even on exception (the
/// caller uses <c>await using</c> so <see cref="DisposeAsync"/> completes an unfinished run).</item>
/// </list>
/// </summary>
public sealed class ScheduledRunReporter : IAsyncDisposable
{
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _tracker;
    private readonly string _serviceKey;
    private readonly OperationType _operationType;
    private readonly ScheduledRunEventNames _events;
    private readonly string _completeStageKey;
    private readonly bool _showNotification;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private Guid _operationId;
    private bool _started;
    private bool _ctsHandedOff;
    private int _completed;

    // Guarded by _sendGate. Read by the terminal-emit closure, which the tracker starts synchronously
    // on the CompleteOperation stack (inside CompleteAsync, while _sendGate has already published these
    // values), so the terminal payload observes the last progress state.
    private double _highestPercent;
    private Dictionary<string, object?>? _lastContext;

    /// <summary>
    /// Creates a run reporter for one scheduled service run. The run's cancellation source is created
    /// here (linked to <paramref name="stoppingToken"/>) and handed to the tracker when the run starts.
    /// </summary>
    /// <param name="completeStageKey">i18n stage key carried by the terminal event on every outcome
    /// (success / failure / cancellation); the frontend renders the outcome from Success + Error.</param>
    /// <param name="showNotification">Precomputed once from the run's notification mode + trigger and
    /// stamped, immutable, into every lifecycle payload for this run.</param>
    /// <param name="stoppingToken">The service's stopping token; the run's CTS is linked to it.</param>
    public ScheduledRunReporter(
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker tracker,
        string serviceKey,
        OperationType operationType,
        ScheduledRunEventNames events,
        string completeStageKey,
        bool showNotification,
        CancellationToken stoppingToken)
    {
        _notifications = notifications;
        _tracker = tracker;
        _serviceKey = serviceKey;
        _operationType = operationType;
        _events = events;
        _completeStageKey = completeStageKey;
        _showNotification = showNotification;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    }

    /// <summary>
    /// The run's cancellation token. Callers pass this to the work they perform so the tracker can
    /// cancel the run (and so shutdown flows through in one place).
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Registers the tracked operation and awaits the run-started broadcast. Call this only once, and
    /// only after confirming there is work to do - a prerequisite-not-met run should return BEFORE
    /// starting so it never surfaces a card (mirrors the existing eviction behavior).
    /// </summary>
    public async Task StartAsync(string stageKey, Dictionary<string, object?>? context = null)
    {
        if (_started)
        {
            throw new InvalidOperationException($"ScheduledRunReporter for '{_serviceKey}' was already started.");
        }

        // Persist the run's immutable display flag onto the tracked operation so the run-status
        // recovery endpoint can report it - without this, a page refresh during a SILENT run would
        // resurrect the card (recovery would have to assume every active run is visible). The
        // "context" slot is seeded here (never structurally added later) so progress updates only
        // overwrite an existing value reference and can never structurally race a concurrent
        // status read; recovery uses it to rehydrate a mid-run card with its interpolation values.
        _operationId = _tracker.RegisterOperation(
            _operationType,
            _serviceKey,
            _cts,
            metadata: new Dictionary<string, object?>
            {
                ["showNotification"] = _showNotification,
                ["context"] = context,
            },
            onTerminalCleanup: null,
            onTerminalEmit: EmitTerminalAsync);
        _ctsHandedOff = true;
        _started = true;
        _lastContext = context;

        _tracker.UpdateProgress(_operationId, 0, stageKey);
        await _notifications.NotifyAllAsync(_events.Started, new ScheduledRunStartedEvent(
            _serviceKey,
            _operationId,
            stageKey,
            context,
            _showNotification));
    }

    /// <summary>
    /// Updates the tracker and broadcasts progress. Percent is clamped monotonic - a value lower than
    /// the highest already sent is raised to that maximum so the bar never regresses.
    /// </summary>
    public async Task ReportAsync(
        double percent,
        string stageKey,
        Dictionary<string, object?>? context = null,
        OperationStatus status = OperationStatus.Running)
    {
        if (!_started || Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        await _sendGate.WaitAsync(_cts.Token);
        try
        {
            if (percent > _highestPercent)
            {
                _highestPercent = percent;
            }

            var clamped = _highestPercent;
            _lastContext = context;

            // Mirror the latest interpolation context onto the tracked operation under the same gate
            // the sends use, so the run-status recovery endpoint reports the values the live card is
            // rendering. Overwrites the seeded "context" slot (an atomic reference write).
            _tracker.UpdateMetadata(_operationId, metadata =>
            {
                if (metadata is Dictionary<string, object?> bag)
                {
                    bag["context"] = context;
                }
            });

            _tracker.UpdateProgress(_operationId, clamped, stageKey);
            await _notifications.NotifyAllAsync(_events.Progress, new ScheduledRunProgressEvent(
                _serviceKey,
                _operationId,
                status.ToWireString(),
                stageKey,
                clamped,
                context,
                _showNotification));
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Completes the run exactly once. The terminal event is produced by the tracker's terminal-emit
    /// gate, so a later duplicate completion (e.g. a racing force-kill) is a no-op.
    /// </summary>
    public async Task CompleteAsync(bool success, string? error = null, bool cancelled = false)
    {
        if (!_started)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return;
        }

        // Publish the terminal context under the same gate the progress sends use, so the terminal
        // payload cannot race a final in-flight ReportAsync.
        await _sendGate.WaitAsync(CancellationToken.None);
        _sendGate.Release();

        if (cancelled)
        {
            // Mark the tracked op cancelled so CompleteOperation yields the Cancelled terminal state
            // (distinct from a failure) before we complete it.
            _tracker.CancelOperation(_operationId);
        }

        _tracker.CompleteOperation(_operationId, success, error);
    }

    // The single terminal emit, invoked exactly once by the tracker (CompletedFlag-gated). Success
    // carries 100; failure and cancellation carry the highest percent reached. Failure routes through
    // the uniform NotifyOperationFailedAsync funnel; success and cancellation broadcast directly.
    private Task EmitTerminalAsync(OperationTerminalInfo info)
    {
        var percent = info.Success ? 100d : _highestPercent;
        var error = info.Success
            ? null
            : (info.Cancelled ? "Cancelled by user" : (info.Error ?? "Scheduled run failed"));

        var terminal = new ScheduledRunCompleteEvent(
            _serviceKey,
            _operationId,
            info.Success,
            _completeStageKey,
            percent,
            error,
            _lastContext,
            _showNotification);

        if (info.Success || info.Cancelled)
        {
            return _notifications.NotifyAllAsync(_events.Complete, terminal);
        }

        return _notifications.NotifyOperationFailedAsync(_events.Complete, terminal);
    }

    public async ValueTask DisposeAsync()
    {
        // Safety net: a run that started but never completed (an exception escaped the caller's work)
        // still reaches a single terminal here. A run that never started leaves nothing to complete.
        if (_started && Volatile.Read(ref _completed) == 0)
        {
            var cancelled = _cts.IsCancellationRequested;
            await CompleteAsync(
                success: false,
                error: cancelled ? "Cancelled by user" : "Scheduled run ended without completion",
                cancelled: cancelled);
        }

        // The tracker is the CTS's single disposer once the run started; only an unstarted run's CTS
        // is ours to dispose.
        if (!_ctsHandedOff)
        {
            _cts.Dispose();
        }

        _sendGate.Dispose();
    }
}
