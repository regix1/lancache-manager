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
    private readonly ScheduledRunPayloadFactories? _payloadFactories;
    private readonly Action? _onTerminalCleanup;
    private readonly ILogger? _logger;
    private readonly Func<OperationTerminalInfo, string>? _externalTerminalStageKey;
    private readonly TaskCompletionSource _terminalEmitted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Guid _operationId;
    private bool _started;
    private bool _ctsHandedOff;
    private int _completed;
    private int _terminalCleanupInvoked;
    private string _terminalStageKey;
    private bool _terminalStagePublished;

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
        CancellationToken stoppingToken,
        ScheduledRunPayloadFactories? payloadFactories = null,
        Action? onTerminalCleanup = null,
        ILogger? logger = null,
        Func<OperationTerminalInfo, string>? externalTerminalStageKey = null)
    {
        _notifications = notifications;
        _tracker = tracker;
        _serviceKey = serviceKey;
        _operationType = operationType;
        _events = events;
        _completeStageKey = completeStageKey;
        _terminalStageKey = completeStageKey;
        _showNotification = showNotification;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _payloadFactories = payloadFactories;
        _onTerminalCleanup = onTerminalCleanup;
        _logger = logger;
        _externalTerminalStageKey = externalTerminalStageKey;
    }

    /// <summary>The tracker-owned id for this run, or <see cref="Guid.Empty"/> before it starts.</summary>
    public Guid OperationId => _operationId;

    /// <summary>Whether the reporter has registered its operation and emitted its started payload.</summary>
    public bool IsStarted => _started;

    /// <summary>
    /// The run's cancellation token. Callers pass this to the work they perform so the tracker can
    /// cancel the run (and so shutdown flows through in one place).
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Registers the tracked operation and awaits the run-started broadcast. Call this only once.
    /// A run that finds nothing to do has two honest shapes and both are in use: return BEFORE
    /// calling this, so no card ever surfaces (OperationHistoryCleanupService when there is no
    /// history old enough to prune), or start and then finish with <c>skipped: true</c>, so the user
    /// sees that the run happened and changed nothing (Epic and Xbox mapping when no account is
    /// signed in). Prefer the second whenever the user triggered the run themselves, because silence
    /// there reads as a button that did not work.
    /// </summary>
    public async Task StartAsync(string stageKey, Dictionary<string, object?>? context = null)
    {
        await _sendGate.WaitAsync(CancellationToken.None);
        try
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
            var started = new ScheduledRunStartedEvent(
                _serviceKey,
                _operationId,
                stageKey,
                context,
                _showNotification);
            await _notifications.NotifyAllAsync(
                _events.Started,
                _payloadFactories?.Started(started) ?? started);
        }
        finally
        {
            _sendGate.Release();
        }
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
            if (Volatile.Read(ref _completed) != 0)
            {
                return;
            }

            var bounded = Math.Clamp(percent, 0, 100);
            if (bounded > _highestPercent)
            {
                _highestPercent = bounded;
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
            var progress = new ScheduledRunProgressEvent(
                _serviceKey,
                _operationId,
                status.ToWireString(),
                stageKey,
                clamped,
                context,
                _showNotification);
            await _notifications.NotifyAllAsync(
                _events.Progress,
                _payloadFactories?.Progress(progress) ?? progress);
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
    public async Task CompleteAsync(
        bool success,
        string? error = null,
        bool cancelled = false,
        string? stageKey = null,
        Dictionary<string, object?>? context = null,
        bool skipped = false)
    {
        if (!_started)
        {
            return;
        }

        await _sendGate.WaitAsync(CancellationToken.None);
        try
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
            {
                // Publish the terminal context under the same gate the progress sends use, so the terminal
                // payload cannot race a final in-flight ReportAsync.
                _terminalStageKey = stageKey ?? _completeStageKey;
                _terminalStagePublished = true;
                if (context is not null)
                {
                    _lastContext = context;
                    _tracker.UpdateMetadata(_operationId, metadata =>
                    {
                        if (metadata is Dictionary<string, object?> bag)
                        {
                            bag["context"] = context;
                        }
                    });
                }

                _tracker.CompleteOperation(_operationId, success, error, cancelled, skipped);
            }
        }
        finally
        {
            _sendGate.Release();
        }

        await _terminalEmitted.Task;
    }

    /// <summary>
    /// Requests cancellation through the unified tracker after start, or cancels the local linked
    /// source before start. The actual terminal payload remains owned by <see cref="CompleteAsync"/>.
    /// </summary>
    public bool RequestCancellation()
    {
        if (_started)
        {
            return _tracker.CancelOperation(_operationId) != OperationCancelResult.NotFound;
        }

        _cts.Cancel();
        return true;
    }

    // The single terminal emit, invoked exactly once by the tracker (CompletedFlag-gated). Success
    // carries 100; failure and cancellation carry the highest percent reached. Failure routes through
    // the uniform NotifyOperationFailedAsync funnel; success and cancellation broadcast directly.
    private async Task EmitTerminalAsync(OperationTerminalInfo info)
    {
        // An external tracker completion can arrive while Started or Progress is still in flight.
        // Mark terminal immediately so no new progress starts, then join the same send gate so the
        // already-started lifecycle send finishes before Complete is constructed and published.
        Interlocked.Exchange(ref _completed, 1);
        await _sendGate.WaitAsync(CancellationToken.None);
        try
        {
            // A skipped run keeps whatever percent it actually reached, which for a run that stopped
            // before doing anything is 0. Stamping 100 would claim work that never happened.
            var percent = info.Success && !info.Skipped ? 100d : _highestPercent;
            var status = info.Cancelled
                ? OperationStatus.Cancelled
                : info.Skipped
                    ? OperationStatus.Skipped
                    : info.Success ? OperationStatus.Completed : OperationStatus.Failed;
            var terminalStageKey = _terminalStagePublished
                ? _terminalStageKey
                : _externalTerminalStageKey?.Invoke(info) ?? _terminalStageKey;
            // A cancelled run carries no error: it did not fail, it was stopped. A failure that
            // supplied no error keeps one only while the key above is the run's all-outcomes complete
            // key, which reads as success text on a failed card. Once the key was picked for THIS
            // outcome the frontend translates it, but only while this field is null, so a generic
            // English sentence here would take precedence and hide the real reason. [27]
            var error = info.Success || info.Cancelled
                ? null
                : info.Error ?? (terminalStageKey == _completeStageKey ? "Scheduled run failed" : null);
            var terminal = new ScheduledRunCompleteEvent(
                _serviceKey,
                _operationId,
                info.Success,
                terminalStageKey,
                percent,
                error,
                _lastContext,
                _showNotification,
                info.Cancelled,
                status);
            var payload = _payloadFactories?.Complete(terminal) ?? terminal;

            if (info.Success || info.Cancelled)
            {
                await _notifications.NotifyAllAsync(_events.Complete, payload);
            }
            else
            {
                await _notifications.NotifyOperationFailedAsync(_events.Complete, payload);
            }
        }
        finally
        {
            _sendGate.Release();
            RunTerminalCleanup();
            _terminalEmitted.TrySetResult();
        }
    }

    private void RunTerminalCleanup()
    {
        if (_onTerminalCleanup is null
            || Interlocked.Exchange(ref _terminalCleanupInvoked, 1) != 0)
        {
            return;
        }

        try
        {
            _onTerminalCleanup();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Terminal cleanup failed for {ServiceKey} operation {OperationId}",
                _serviceKey,
                _operationId);
        }
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
                error: cancelled ? null : "Scheduled run ended without completion",
                cancelled: cancelled);
        }

        if (_started)
        {
            await _terminalEmitted.Task;
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

/// <summary>
/// Optional adapters for producers that must preserve additive platform-specific fields while using
/// the canonical scheduled-run lifecycle. The complete adapter must retain the
/// <see cref="IOperationComplete"/> terminal contract.
/// </summary>
public sealed record ScheduledRunPayloadFactories(
    Func<ScheduledRunStartedEvent, object> Started,
    Func<ScheduledRunProgressEvent, object> Progress,
    Func<ScheduledRunCompleteEvent, IOperationComplete> Complete);
