using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Scheduling;
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
    /// <summary>The line a run someone started shows when it found nothing to do.</summary>
    public const string NothingToDoStageKey = "signalr.generic.nothingToDo";

    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _tracker;
    private readonly string _serviceKey;
    private readonly OperationType _operationType;
    private readonly ScheduledRunEventNames _events;
    private readonly string _completeStageKey;
    private readonly RunNotice _notice;
    private CancellationTokenRegistration _cancelRegistration;
    private readonly CancellationTokenSource _cts;
    // Captured once: completing the run (a force stop does) disposes the source, and a token read from a disposed source
    // throws, while a token captured before still reports the cancel.
    private readonly CancellationToken _token;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ScheduledRunPayloadFactories? _payloadFactories;
    private readonly Action? _onTerminalCleanup;
    private readonly ILogger? _logger;
    private readonly Func<OperationTerminalInfo, string>? _externalTerminalStageKey;
    private readonly TaskCompletionSource<OperationTerminalInfo> _terminalEmitted =
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
    /// <param name="notice">The run's notice, registered with its operation; it alone decides how the
    /// run is drawn.</param>
    /// <param name="stoppingToken">The service's stopping token; the run's CTS is linked to it.</param>
    public ScheduledRunReporter(
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker tracker,
        string serviceKey,
        OperationType operationType,
        ScheduledRunEventNames events,
        string completeStageKey,
        RunNotice notice,
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
        _notice = notice;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _token = _cts.Token;
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
    public CancellationToken Token => _token;

    /// <summary>
    /// Registers the tracked operation and awaits the run-started broadcast. Call this only once.
    /// A run that finds nothing to do has two honest shapes and both are in use: return BEFORE
    /// calling this, so no card ever surfaces (an automatic OperationHistoryCleanupService run when
    /// there is no history old enough to prune), or start and then finish with <c>skipped: true</c>,
    /// so the user sees that the run happened and changed nothing (the same cleanup run from Run
    /// Now, with <see cref="NothingToDoStageKey"/>; Epic and Xbox mapping when no account is signed
    /// in). Use the second whenever the user triggered the run themselves, because silence there
    /// reads as a button that did not work.
    /// </summary>
    public async Task StartAsync(string stageKey, Dictionary<string, object?>? context = null, IntegrationLogin? login = null)
    {
        await _sendGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_started)
            {
                throw new InvalidOperationException($"ScheduledRunReporter for '{_serviceKey}' was already started.");
            }

            // The notice registered with the operation decides how its row is drawn, including after
            // a page refresh. The "context" slot is seeded here (never structurally added later) so
            // progress updates only overwrite an existing value reference and can never structurally
            // race a concurrent status read; recovery uses it to rehydrate a mid-run card with its
            // interpolation values.
            _operationId = _tracker.RegisterOperation(
                _operationType,
                ScheduleTitles.ByServiceKey[_serviceKey],
                _cts,
                metadata: new Dictionary<string, object?>
                {
                    ["context"] = context,
                    ["integrationLogin"] = login,
                },
                onTerminalCleanup: null,
                onTerminalEmit: EmitTerminalAsync,
                notice: _notice);
            _ctsHandedOff = true;
            _started = true;
            _lastContext = context;

            _cancelRegistration = _token.Register(() => _notice.Cancel(_tracker, _operationId));
            _notice.Attach(_tracker, _operationId);

            _tracker.UpdateProgress(_operationId, 0, stageKey);
            var started = new ScheduledRunStartedEvent(
                _serviceKey,
                _operationId,
                stageKey,
                context);
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

        await _sendGate.WaitAsync(_token);
        try
        {
            if (Volatile.Read(ref _completed) != 0)
            {
                return;
            }

            var clamped = Math.Max(_highestPercent, Math.Clamp(percent, 0, 100));
            context = context is null ? null : new Dictionary<string, object?>(context);
            var accepted = false;
            _tracker.UpdateProgress(_operationId, clamped, stageKey, operation =>
            {
                _highestPercent = clamped;
                _lastContext = context;
                if (operation.Metadata is Dictionary<string, object?> bag)
                {
                    bag["context"] = context;
                }
                accepted = true;
            });
            if (!accepted)
            {
                return;
            }
            var progress = new ScheduledRunProgressEvent(
                _serviceKey,
                _operationId,
                status.ToWireString(),
                stageKey,
                clamped,
                context);
            await _notifications.NotifyAllAsync(
                _events.Progress,
                _payloadFactories?.Progress(progress) ?? progress);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Names what the run left undone; it then ends amber.</summary>
    public void SetWarning(RunWarning warning) => _tracker.SetWarning(_operationId, warning);

    /// <summary>
    /// Completes the run exactly once. The terminal event is produced by the tracker's terminal-emit
    /// gate, so a later duplicate completion (e.g. a racing force-kill) is a no-op.
    /// </summary>
    public async Task CompleteAsync(
        bool success,
        string? error = null,
        bool canceled = false,
        string? stageKey = null,
        Dictionary<string, object?>? context = null,
        bool skipped = false,
        Action? commit = null)
    {
        if (!_started)
        {
            if (commit != null) throw new InvalidOperationException("Cannot commit an operation that has not started");
            return;
        }

        var committed = false;
        await _sendGate.WaitAsync(CancellationToken.None);
        try
        {
            if (Volatile.Read(ref _completed) == 0)
            {
                var finalContext = context is null ? null : new Dictionary<string, object?>(context);
                // A failure that sent text (an exception message or a sentence) ends on its own stage key, or on the
                // tracker's generic failed key when it has none; the text stays on the run as its error detail.
                var failed = !success && !canceled && !skipped;
                var errorText = failed && error is not null && error != stageKey ? error : null;
                var runError = errorText is null ? error : stageKey;
                _tracker.CompleteOperation(_operationId, success, runError, canceled, skipped, operation =>
                {
                    _terminalStageKey = stageKey ?? _completeStageKey;
                    _terminalStagePublished = true;
                    if (errorText is not null) operation.ErrorDetail = errorText;
                    // A failure that ends on its own stage key keeps the values its sentence names, so the run row and
                    // Schedule History can word it after the live event is gone. The GitHub depot import's failures,
                    // Steam's not-started run, the Epic and Xbox sign-ins that ran out of time and every mapping
                    // failure end so. An Xbox XErr refusal ends before its run starts, so it has no run row to word.
                    if (failed && stageKey is not null && runError == stageKey && finalContext is not null)
                    {
                        operation.FailureReason = new RunWarning(stageKey, finalContext);
                    }
                    if (finalContext is not null)
                    {
                        _lastContext = finalContext;
                        if (operation.Metadata is Dictionary<string, object?> bag)
                        {
                            bag["context"] = finalContext;
                        }
                    }
                }, commit: commit == null ? null : () =>
                {
                    commit();
                    committed = true;
                });
                Interlocked.Exchange(ref _completed, 1);
            }
        }
        finally
        {
            _sendGate.Release();
        }

        var terminal = await _terminalEmitted.Task;
        if (commit != null && !committed)
        {
            if (terminal.Canceled) throw new OperationCanceledException("Operation canceled before its checkpoint");
            throw new InvalidOperationException("The checkpoint could not be committed because the operation ended");
        }
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
        // Read before the first await: this runs inside CompleteOperation, after the run's message is set to the key
        // its failure ended on and before the run can be reaped.
        var failedRunKey = info.Success || info.Canceled || info.Skipped
            ? null
            : _tracker.GetOperation(_operationId)?.Message;
        await _sendGate.WaitAsync(CancellationToken.None);
        try
        {
            // A skipped run keeps whatever percent it actually reached, which for a run that stopped
            // before doing anything is 0. Stamping 100 would claim work that never happened.
            var percent = info.Success && !info.Skipped ? 100d : _highestPercent;
            var status = info.Canceled
                ? OperationStatus.Canceled
                : info.Skipped
                    ? OperationStatus.Skipped
                    : info.Success ? OperationStatus.Completed : OperationStatus.Failed;
            var terminalStageKey = _terminalStagePublished
                ? _terminalStageKey
                : _externalTerminalStageKey?.Invoke(info) ?? _terminalStageKey;
            // A canceled run carries no error: it did not fail, it was stopped. A failure carries the
            // key its run ended on only while the key above is the run's all-outcomes complete key,
            // which reads as success text on a failed card. Once the key was picked for THIS outcome
            // the frontend translates it with the context, but only while this field is null, so
            // anything here would take precedence and hide the real reason.
            // A skipped run keeps whatever reason it was given and gains none it was not: it stopped
            // for something outside itself, and that sentence is the only part of the card worth
            // reading.
            var error = info.Canceled
                ? null
                : info.Skipped
                    ? info.Error
                    : info.Success
                        ? null
                        : terminalStageKey == _completeStageKey ? failedRunKey : null;
            var terminal = new ScheduledRunCompleteEvent(
                _serviceKey,
                _operationId,
                info.Success,
                terminalStageKey,
                percent,
                error,
                _lastContext,
                info.Canceled,
                status);
            var payload = _payloadFactories?.Complete(terminal) ?? terminal;

            if (info.Success || info.Canceled)
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
            _terminalEmitted.TrySetResult(info);
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
            var canceled = _cts.IsCancellationRequested;
            await CompleteAsync(
                success: false,
                error: canceled ? null : "Scheduled run ended without completion",
                canceled: canceled);
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

        _cancelRegistration.Dispose();
        _sendGate.Dispose();
    }
}
