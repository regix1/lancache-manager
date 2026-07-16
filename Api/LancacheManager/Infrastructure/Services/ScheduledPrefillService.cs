using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Orchestrates scheduled prefill runs across all enabled services on a user-configurable
/// interval (managed through the unified Schedules page via <see cref="ServiceScheduleRegistry"/>).
/// Lane B1 provided the compiling skeleton; Lane B2 fills in the per-service lifecycle.
/// </summary>
public sealed class ScheduledPrefillService : ConfigurableScheduledService, IScheduleEnabledGate
{
    // OUTER schedule poll cadence: the base loop wakes once a minute and runs only the services that
    // are DUE per their own IntervalHours + persisted last-run. This is NOT the user-facing schedule.
    private static readonly TimeSpan _pollCadence = TimeSpan.FromMinutes(1);
    // INNER guard cadence: how often a running prefill's deadline and stall state are checked.
    // Live progress is pushed independently and never waits for this interval.
    private static readonly TimeSpan _guardCheckInterval = TimeSpan.FromSeconds(10);

    // The poll interval is waited out in slices THIS small. A user who stops a prefill (from the
    // modal, which cancels the daemon session) expects the card to react at once; sleeping the whole
    // poll interval meant the run only noticed the stop up to ten seconds later, so the card sat
    // there looking alive and the user hammered the button. The slice only affects how fast a STOP
    // is noticed; progress is pushed independently by the daemon relay.
    private static readonly TimeSpan _stopDetectionSlice = TimeSpan.FromMilliseconds(250);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStateService _stateService;

    /// <summary>
    /// Platforms that have actually run at least once in this process. Only the single base scheduling
    /// loop mutates/reads it, so no lock is needed. Backs the startup-only (<c>-1</c>) due-check, which
    /// fires once per process and ignores the persisted last-run.
    /// </summary>
    private readonly HashSet<PrefillPlatform> _ranThisProcess = new();

    /// <summary>
    /// Deterministic pseudo-user Guid that owns every daemon session created by the scheduler.
    /// Derived once from <see cref="ScheduledPrefillConstants.SystemUserId"/> so that the busy
    /// check can reliably distinguish "our" system sessions from real manual-user sessions.
    /// </summary>
    private readonly Guid _systemUserId = ScheduledPrefillConstants.DeriveSystemUserId();

    /// <summary>
    /// Stable service key used by <see cref="ServiceScheduleRegistry"/> (read via reflection)
    /// and by <c>StateService.ServiceIntervals</c>.
    /// </summary>
    public string ScheduleServiceKey => "scheduledPrefill";

    /// <summary>
    /// Log-friendly name surfaced by the base class in log lines.
    /// </summary>
    protected override string ServiceName => "Scheduled Prefill";

    /// <summary>
    /// Scheduled prefill should not fire automatically the instant the app starts.
    /// </summary>
    public override bool DefaultRunOnStartup => false;

    public ScheduledPrefillService(
        ILogger<ScheduledPrefillService> logger,
        IServiceScopeFactory scopeFactory,
        IStateService stateService)
        : base(logger, _pollCadence)
    {
        _scopeFactory = scopeFactory;
        _stateService = stateService;

        // Per-service scheduling: the base loop runs as a fixed 1-minute POLL cadence and each tick
        // runs only the services that are DUE (per-service IntervalHours + persisted last-run). The
        // legacy global ServiceIntervals["scheduledPrefill"] no longer drives the schedule, so it is
        // NOT applied here (no LoadStateOverrides). It is consumed once as the v1->v2 migration seed
        // in StateService.ResolveScheduledPrefillConfig.
    }

    /// <inheritdoc />
    /// <remarks>
    /// Backs <see cref="ServiceScheduleRegistry"/>'s schedule-payload gate: when nothing is
    /// enabled, the registry reports this schedule as paused instead of exposing the fixed
    /// 1-minute outer poll cadence as a live countdown.
    /// </remarks>
    public bool HasAnyServiceEnabled()
        => ScheduledPrefillRunGates.HasAnyEnabledService(_stateService.GetScheduledPrefillConfig().GetServicesInRunOrder());

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var config = _stateService.GetScheduledPrefillConfig();

        // Nothing enabled: skip the whole tick before building the due-set or touching any
        // daemon/session/tracker state. Saves the per-minute work while the feature is fully idle;
        // the schedule resumes normally as soon as any service is re-enabled.
        if (!ScheduledPrefillRunGates.HasAnyEnabledService(config.GetServicesInRunOrder()))
        {
            _logger.LogDebug("[ScheduledPrefill] Skipping tick - no services are enabled");
            return;
        }

        // A manual "Run Now"/"Run All" bypasses the due-check for this single tick and runs every
        // enabled service.
        var bypassDueCheck = CurrentRunTrigger == RunTrigger.Manual;
        var now = DateTime.UtcNow;

        // Build the DUE set: each enabled service whose own IntervalHours + persisted last-run says it
        // should run this tick (or every enabled service when a manual run bypassed the due-check).
        var dueServices = new List<ScheduledPrefillServiceConfigDto>();
        foreach (var serviceConfig in config.GetEnabledServicesInRunOrder())
        {
            if (bypassDueCheck)
            {
                dueServices.Add(serviceConfig);
                continue;
            }

            var lastRun = _stateService.GetScheduledPrefillServiceLastRun(serviceConfig.ServiceId.ToString());
            var hasRunThisProcess = _ranThisProcess.Contains(serviceConfig.ServiceId);
            if (ScheduledPrefillRunGates.IsServiceDue(serviceConfig.IntervalHours, lastRun, now, hasRunThisProcess))
            {
                dueServices.Add(serviceConfig);
            }
        }

        // #1 HAZARD: an empty poll tick (no due service) must emit NO Started/Completed notification,
        // otherwise the 1-minute poll would spam the UI every minute. Only notify when >= 1 runs.
        if (dueServices.Count == 0)
        {
            return;
        }

        _logger.LogInformation("[ScheduledPrefill] Starting run for {Count} due service(s)", dueServices.Count);

        // Register a single tracker operation for the whole run. The tracker owns the CTS after a
        // successful RegisterOperation, so we link it to stoppingToken and never dispose it here.
        using var scope = _scopeFactory.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<IUnifiedOperationTracker>();
        var notifications = scope.ServiceProvider.GetRequiredService<ISignalRNotificationService>();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Run-level visibility is fixed for the whole run: if ANY due platform's mode allows a
        // notification for the trigger that produced this tick, the run's single card is visible.
        // Computed once and never mutated, so a silent child can neither remove a visible sibling's
        // card nor make the terminal event disagree with the Started event's visibility. A run whose
        // due platforms are all silent emits every lifecycle event with showNotification=false.
        var runShowNotification = dueServices.Any(ResolveShowNotification);
        var notificationMetadata = new ScheduledPrefillOperationMetadata(runShowNotification);
        var operationId = tracker.RegisterOperation(
            OperationType.ScheduledPrefill,
            "Scheduled Prefill",
            cts,
            notificationMetadata);
        var operationIdString = operationId.ToString();
        var runToken = cts.Token;

        bool success = true;
        string? error = null;
        bool cancelled = false;

        try
        {
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillStarted, new
            {
                operationId = operationIdString,
                serviceCount = dueServices.Count,
                showNotification = notificationMetadata.ShowNotification
            });

            var servicesRan = 0;
            var servicesNeedingLogin = 0;
            var servicesSkipped = 0;
            var servicesFailed = 0;

            for (var i = 0; i < dueServices.Count; i++)
            {
                var serviceConfig = dueServices[i];

                runToken.ThrowIfCancellationRequested();

                var result = ScheduledPrefillServiceRunResult.Skipped;
                try
                {
                    result = await RunServiceAsync(
                        serviceConfig,
                        operationIdString,
                        scope.ServiceProvider,
                        notifications,
                        config,
                        runShowNotification,
                        runToken);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One service failing must not abort the rest of the run.
                    result = ScheduledPrefillServiceRunResult.Failed;
                    _logger.LogError(ex, "[ScheduledPrefill] Service {Service} failed; continuing", serviceConfig.ServiceId);
                }
                finally
                {
                    // Stamp the SCHEDULE-BASIS last-run + mark process-ran for EVERY due service we attempted
                    // this tick (including skips/failures), so the 1-minute poll does not immediately re-run it
                    // (recurring) nor re-fire a startup-only service. A still-needs-login service then
                    // retries on its next interval rather than spamming a Started/Completed cycle every
                    // minute. Cancellation is exempt: do not stamp when the run is being torn down.
                    if (!runToken.IsCancellationRequested)
                    {
                        _ranThisProcess.Add(serviceConfig.ServiceId);
                        _stateService.SetScheduledPrefillServiceLastRun(serviceConfig.ServiceId.ToString(), DateTime.UtcNow);

                        // Stamp the GENUINE last-run (the "Last run" the schedule view shows) ONLY when the
                        // service actually ran its prefill to completion. A skip / needs-login / failure
                        // advances the schedule basis above but must NOT count as a real run, so the UI keeps
                        // reading "Never" until the service has truly prefilled at least once.
                        if (result == ScheduledPrefillServiceRunResult.Ran)
                        {
                            _stateService.SetScheduledPrefillServiceLastActualRun(serviceConfig.ServiceId.ToString(), DateTime.UtcNow);
                        }
                    }
                }

                switch (result)
                {
                    case ScheduledPrefillServiceRunResult.Ran:
                        servicesRan++;
                        break;
                    case ScheduledPrefillServiceRunResult.NeedsLogin:
                        servicesNeedingLogin++;
                        break;
                    case ScheduledPrefillServiceRunResult.Failed:
                        servicesFailed++;
                        break;
                    case ScheduledPrefillServiceRunResult.Cancelled:
                        // The user stopped the prefill this run was driving. Honour that and end the
                        // run instead of marching on through the remaining due services: continuing
                        // is what made a stopped STEAM prefill finish by reporting the LAST service's
                        // status ("Riot needs login...") as though it were the stop's result.
                        cancelled = true;
                        break;
                    default:
                        servicesSkipped++;
                        break;
                }

                if (cancelled)
                {
                    // Advance the SCHEDULE BASIS of the services this stopped batch never reached, so
                    // the one-minute poll does not relaunch the very batch the user just stopped (and
                    // re-raise the same needs-login cards a minute later). They resume on their own
                    // next interval. The genuine "last run" is untouched: none of them prefilled.
                    for (var skipped = i + 1; skipped < dueServices.Count; skipped++)
                    {
                        var pending = dueServices[skipped];
                        _ranThisProcess.Add(pending.ServiceId);
                        _stateService.SetScheduledPrefillServiceLastRun(
                            pending.ServiceId.ToString(),
                            DateTime.UtcNow);
                    }

                    break;
                }
            }

            if (cancelled)
            {
                success = false;
                error = "Scheduled prefill stopped";

                // Record the tracked operation as CANCELLED rather than Failed. CompleteOperation in
                // the finally maps success:false to Failed unless the operation carries the cancelled
                // flag, and a run the user stopped is not an error. Cancelling the (already-drained)
                // token here is harmless: the service loop has been broken out of.
                tracker.CancelOperation(operationId);
            }
            else
            {
                var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesRan, servicesNeedingLogin, servicesSkipped, servicesFailed);
                success = outcome.Success;
                error = outcome.Error;
            }
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            // Cancelled via this run's own operation-tracker CTS (user pressed Cancel) or app
            // shutdown - NOT an unrelated internal timeout, which surfaces with runToken still
            // un-cancelled and therefore falls through to the generic catch below and is re-thrown
            // as a real error. This benign cancel is fully handled here: the finally still notifies
            // and completes the tracked operation as cancelled, so we swallow the exception instead
            // of re-throwing. That keeps it out of the shared ConfigurableScheduledService loop,
            // which would otherwise mis-log a user cancel as a hard "error in scheduled work".
            // Returning lets the base loop treat this tick as a normal completion and keep ticking.
            success = false;
            cancelled = true;
            error = "Scheduled prefill run cancelled";

            // Make the cancel STICK. Every due service that was not reached is still due, so without
            // advancing the schedule basis the one-minute poll relaunches the very batch the user
            // just cancelled - a cancel that lasts 60 seconds. Services already attempted were
            // stamped in the loop; re-stamping them is harmless. APP SHUTDOWN is deliberately exempt:
            // there we want the batch to run on the next start, so nothing is stamped.
            if (!stoppingToken.IsCancellationRequested)
            {
                foreach (var pending in dueServices)
                {
                    _ranThisProcess.Add(pending.ServiceId);
                    _stateService.SetScheduledPrefillServiceLastRun(
                        pending.ServiceId.ToString(),
                        DateTime.UtcNow);
                }
            }

            _logger.LogInformation("[ScheduledPrefill] Scheduled run was cancelled");
            return;
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
            throw;
        }
        finally
        {
            // `cancelled` is a distinct terminal from a failure: the card renders a stop in red with
            // the cancel icon rather than reporting an error the user did not cause.
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillCompleted, new
            {
                operationId = operationIdString,
                success,
                error,
                cancelled,
                showNotification = notificationMetadata.ShowNotification
            });

            // Tracker disposes the adopted CTS exactly once inside CompleteOperation; we must not.
            tracker.CompleteOperation(operationId, success, error);
        }

        _logger.LogInformation("[ScheduledPrefill] Run complete");
    }

    /// <summary>
    /// Runs a single service's scheduled prefill: resolves the concrete daemon, reuses the running
    /// persistent admin container (which authenticates itself from its named auth volume), performs
    /// the needs-login + busy gates, and drives the prefill on that persistent session. The
    /// persistent container is system-owned and long-lived, so it is never created or torn down
    /// here. Emits <see cref="SignalREvents.ScheduledPrefillProgress"/> at each stage.
    /// </summary>
    /// <returns>How the service's run ended — see <see cref="ScheduledPrefillServiceRunResult"/>.</returns>
    private async Task<ScheduledPrefillServiceRunResult> RunServiceAsync(
        ScheduledPrefillServiceConfigDto serviceConfig,
        string operationId,
        IServiceProvider serviceProvider,
        ISignalRNotificationService notifications,
        ScheduledPrefillConfigDto config,
        bool runShowNotification,
        CancellationToken ct)
    {
        var serviceId = serviceConfig.ServiceId;

        // 1. Resolve the concrete daemon service for this platform.
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(serviceProvider, serviceId);
        if (daemon is null)
        {
            await EmitProgressAsync(notifications, operationId, serviceConfig, "skipped", "No daemon registered for this service", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        // 2. Reuse the running persistent admin container. Scheduled prefill is admin-only and
        // downloads INSIDE the long-lived persistent container, which authenticates itself from its
        // named auth volume. It never spawns a temporary guest container and never injects a token.
        var persistentSession = daemon.GetActivePersistentSession();
        if (!ScheduledPrefillRunGates.TryGetRunnablePersistentSession(persistentSession, out var sessionId, out var needsLoginReason))
        {
            await EmitProgressAsync(
                notifications,
                operationId,
                serviceConfig,
                "needs-login",
                ScheduledPrefillRunGates.BuildNeedsLoginMessage(serviceId, containerRunning: false),
                runShowNotification,
                needsLoginReason,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.NeedsLogin;
        }

        // TryGetRunnablePersistentSession only returns true for a non-null session.
        var session = persistentSession!;

        // 2b. Confirm the persistent container is actually logged in using the daemon's LIVE status,
        // exactly like the persistent-list endpoint (status?.Status == "logged-in"). The daemon is the
        // source of truth: the in-memory AuthState is unreliable for a persistent container re-adopted
        // on a manager restart (it stays NotAuthenticated until interactive login). A null status or a
        // failing/cancelled poll is treated as needs-login rather than crashing the run.
        bool isLoggedIn;
        try
        {
            var status = await daemon.GetSessionStatusAsync(session.Id, ct);
            isLoggedIn = status?.Status == "logged-in";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ScheduledPrefill] Failed to query persistent container status for {Service}", serviceId);
            isLoggedIn = false;
        }

        if (!isLoggedIn)
        {
            await EmitProgressAsync(
                notifications,
                operationId,
                serviceConfig,
                "needs-login",
                ScheduledPrefillRunGates.BuildNeedsLoginMessage(serviceId, containerRunning: true),
                runShowNotification,
                ScheduledPrefillRunGates.LoggedOutNeedsLoginReason,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.NeedsLogin;
        }

        // 3. Busy check: defer when the persistent container is already prefilling (a prior run still
        // going) or a manual/guest interactive session is live. An idle persistent container does not
        // block, because it shares the system user id with the scheduler.
        if (ScheduledPrefillRunGates.ShouldSkipForBusySessions(
                daemon.GetAllSessions(),
                _systemUserId,
                out var skipMessage))
        {
            await EmitProgressAsync(notifications, operationId, serviceConfig, "skipped", skipMessage, runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceConfig,
            "starting",
            "Reusing persistent container",
            runShowNotification,
            downloadSessionId: sessionId,
            percent: ScheduledPrefillRunGates.ComputeRunPercent(0));

        // 4. Kick off the prefill. Map preset + OS list to the real daemon signature.
        // When specific apps are selected, prefill exactly those and ignore the All/Recent/Top
        // preset; otherwise fall back to the preset selection.
        var hasSelectedApps = serviceConfig.SelectedAppIds.Count > 0;
        bool all;
        bool recent;
        int? top;
        if (hasSelectedApps)
        {
            all = false;
            recent = false;
            top = null;
        }
        else
        {
            MapPreset(serviceConfig, out all, out recent, out top);
        }

        var operatingSystems = MapOperatingSystems(serviceConfig.OperatingSystems);
        var maxConcurrency = serviceConfig.MaxConcurrency.Mode == ScheduledPrefillMaxConcurrencyMode.Fixed
            ? serviceConfig.MaxConcurrency.Value
            : null;

        if (hasSelectedApps)
        {
            _logger.LogInformation(
                "[ScheduledPrefill] Setting {Count} selected app(s) on session {SessionId}",
                serviceConfig.SelectedAppIds.Count,
                sessionId);
            await daemon.SetSelectedAppsAsync(sessionId, serviceConfig.SelectedAppIds, ct);
        }

        _logger.LogInformation(
            "[ScheduledPrefill] Starting prefill on persistent session {SessionId} (force={Force}, selectedApps={SelectedCount})",
            sessionId,
            serviceConfig.Force,
            serviceConfig.SelectedAppIds.Count);

        PrefillResult result;
        try
        {
            result = await daemon.PrefillAsync(
                sessionId,
                all: all,
                recent: recent,
                recentlyPurchased: false,
                top: top,
                force: serviceConfig.Force,
                operatingSystems: operatingSystems,
                maxConcurrency: maxConcurrency,
                cancellationToken: ct);
        }
        catch (PrefillAlreadyRunningException)
        {
            await EmitProgressAsync(notifications, operationId, serviceConfig, "skipped", "A prefill is already in progress", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        // A failed start may leave IsPrefilling already false, which would make the poll loop
        // exit immediately and wrongly report "completed". Treat a non-Success start as failed.
        if (!result.Success)
        {
            var failureMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Prefill failed to start"
                : result.ErrorMessage;
            await EmitProgressAsync(notifications, operationId, serviceConfig, "failed", failureMessage, runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.Failed;
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceConfig,
            "running",
            "Prefill in progress",
            runShowNotification,
            downloadSessionId: sessionId,
            percent: ScheduledPrefillRunGates.ComputeRunPercent(0));

        // Live progress is PUSHED, never sampled. The daemon already raises a tick for every chunk it
        // finishes (it has to - the prefill page renders from those very ticks); the scheduler used to
        // ignore that and re-read session.LastProgress on a ten-second timer, which is why the card
        // lagged by up to ten seconds and moved in coarse steps. The relay below subscribes to that
        // push instead. The loop that follows never touches LastProgress again: it is purely a guard
        // for the run deadline, the stall detector, cancellation, and stop detection - none of which a
        // progress push can do, because all four are about the ABSENCE of progress or an external stop.
        var relay = new ScheduledPrefillProgressRelay(
            this,
            notifications,
            session,
            serviceConfig,
            operationId,
            sessionId,
            runShowNotification);

        Func<DaemonSession, PrefillProgress, long, Task> onDaemonProgress = relay.OnProgressAsync;
        daemon.PrefillProgressUpdated += onDaemonProgress;
        var relayStopped = false;

        // Silences the relay and waits for any send already inside its gate to finish. MUST run before
        // this service emits any terminal event, or a live tick still in flight could land on the card
        // after "completed"/"cancelled". Idempotent: the finally calls it again on the exception paths.
        async Task StopRelayAsync()
        {
            if (relayStopped)
            {
                return;
            }

            relayStopped = true;
            daemon.PrefillProgressUpdated -= onDaemonProgress;
            await relay.DeactivateAndDrainAsync();
        }

        try
        {
            // Arm only now, so the explicit "running / 0%" event above is always the card's first
            // live line. Then replay whatever the daemon has already pushed while we were wiring up -
            // a one-shot catch-up for the dispatch-to-subscribe window, not a poll.
            relay.Arm();
            await relay.ReplayLatestAsync();

            var runDeadline = DateTime.UtcNow + config.MaxServiceRuntime;
            while (session.IsPrefilling)
            {
                ct.ThrowIfCancellationRequested();

                if (DateTime.UtcNow >= runDeadline)
                {
                    await StopRelayAsync();
                    await EmitProgressAsync(notifications, operationId, serviceConfig, "failed", "Exceeded maximum service runtime", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
                    return ScheduledPrefillServiceRunResult.Failed;
                }

                if (PrefillDaemonServiceBase.IsPrefillStalled(session, DateTime.UtcNow, config.StallTimeout))
                {
                    await StopRelayAsync();
                    await EmitProgressAsync(notifications, operationId, serviceConfig, "failed", "Prefill stalled (no progress)", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
                    return ScheduledPrefillServiceRunResult.Failed;
                }

                try
                {
                    // Wait out the guard cadence in slices, breaking the moment the prefill stops, so a
                    // stop is acted on in ~250ms instead of up to a full ten seconds.
                    //
                    // Counted slices, NOT a wall-clock deadline: DateTime.UtcNow is not monotonic, so an
                    // NTP correction, a VM resume or an admin moving the clock backwards would otherwise
                    // suspend the guard checks for the length of the jump.
                    var slices = (int)Math.Ceiling(_guardCheckInterval / _stopDetectionSlice);
                    for (var slice = 0; slice < slices && session.IsPrefilling; slice++)
                    {
                        await Task.Delay(_stopDetectionSlice, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    await daemon.CancelPrefillAsync(sessionId, CancellationToken.None);
                    throw;
                }
            }

            await StopRelayAsync();

            // A prefill the user STOPPED leaves the loop above exactly like a natural finish: the
            // modal's stop cancels the DAEMON session (not this run's token), and the terminal funnel is
            // the sole writer of IsPrefilling=false, stamping the reason on the session as it goes.
            // Without this check a stopped prefill was reported as a completed run - it stamped the
            // genuine "Last run" and told the user their cancelled prefill had succeeded.
            if (session.PrefillState == PrefillState.Cancelled)
            {
                await EmitProgressAsync(
                    notifications,
                    operationId,
                    serviceConfig,
                    "cancelled",
                    "Prefill stopped",
                    runShowNotification,
                    downloadSessionId: sessionId,
                    percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
                return ScheduledPrefillServiceRunResult.Cancelled;
            }

            await EmitProgressAsync(
                notifications,
                operationId,
                serviceConfig,
                "completed",
                BuildCompletionMessage(session, hasSelectedApps, serviceConfig.Force),
                runShowNotification,
                bytesDownloaded: session.TotalBytesTransferred,
                downloadSessionId: sessionId,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.Ran;
        }
        finally
        {
            // The handler must never outlive the service that owns it: a leaked closure would keep
            // emitting this service's card from the NEXT service's daemon ticks.
            await StopRelayAsync();
        }
    }

    /// <summary>
    /// Turns the daemon's live progress PUSH into this run's universal-notification events.
    /// Replaces the ten-second sampler that used to re-read <see cref="DaemonSession.LastProgress"/>.
    ///
    /// It is deliberately more than an event handler, because the push is not as tame as it looks:
    /// socket events are dispatched fire-and-forget, so two ticks can be in flight at once and an
    /// app-transition tick (which awaits a history write) can be OVERTAKEN by a later downloading
    /// tick. Hence the semaphore held across the awaited send, the sequence check, and the monotonic
    /// counters - without them the card could render progress backwards or interleave two sends.
    /// </summary>
    private sealed class ScheduledPrefillProgressRelay
    {
        /// <summary>
        /// Floor on how often the SAME message re-emits. Every daemon already throttles its own ticks
        /// to ~250ms (2-4/s), and this event fans out to EVERY connected client, so relaying each tick
        /// would be pointless load. One hertz reads as live and still drops up to 75% of the stream.
        /// A message change (new game, new count) bypasses the interval and emits immediately.
        /// </summary>
        private const long LiveEmitMinIntervalMs = 1_000;

        /// <summary>
        /// How long the teardown will wait for an in-flight send before abandoning it. Bounded on
        /// purpose: the run's terminal event must never be held hostage by the notification hub.
        /// </summary>
        private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(5);

        private readonly ScheduledPrefillService _owner;
        private readonly ISignalRNotificationService _notifications;
        private readonly DaemonSession _session;
        private readonly ScheduledPrefillServiceConfigDto _serviceConfig;
        private readonly string _operationId;
        private readonly string _sessionId;
        private readonly int _selectedAppCount;

        // Run-level visibility flag, fixed for the whole run by the orchestrator and stamped verbatim
        // into every relayed progress event so a live tick never disagrees with the Started/terminal
        // visibility the card was created with.
        private readonly bool _showNotification;

        /// <summary>
        /// Serializes decide-and-send. A plain lock cannot span the awaited SignalR send, so two
        /// accepted ticks could otherwise overtake each other on the wire.
        /// </summary>
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ProgressEmitGate _emitGate = new(LiveEmitMinIntervalMs);

        private volatile bool _armed;
        private volatile bool _active = true;

        private long _highestSequence = -1L;
        private long _revision;
        private int _appsCompleted;
        private double _highestPercent;
        private string? _lastEmittedMessage;
        private long _lastEmittedBytes = -1L;

        internal ScheduledPrefillProgressRelay(
            ScheduledPrefillService owner,
            ISignalRNotificationService notifications,
            DaemonSession session,
            ScheduledPrefillServiceConfigDto serviceConfig,
            string operationId,
            string sessionId,
            bool showNotification)
        {
            _owner = owner;
            _notifications = notifications;
            _session = session;
            _serviceConfig = serviceConfig;
            _operationId = operationId;
            _sessionId = sessionId;
            _selectedAppCount = serviceConfig.SelectedAppIds.Count;
            _showNotification = showNotification;
        }

        internal void Arm() => _armed = true;

        /// <summary>
        /// Feeds the daemon's latest snapshot through the relay exactly once, closing the window
        /// between dispatching the prefill and subscribing to its pushes. One shot, not a poll.
        ///
        /// The sequence is read BEFORE the snapshot, deliberately. The two reads are not atomic, so
        /// reading it after could stamp an OLD snapshot with a NEWER tick's number and make the relay
        /// discard the genuine push that number belonged to. Reading first can only UNDER-state the
        /// snapshot's age, which costs at most one duplicate emit - and the emit gate swallows that.
        /// </summary>
        internal Task ReplayLatestAsync()
        {
            var sequence = Interlocked.Read(ref _session.ProgressSequence);
            var snapshot = _session.LastProgress;
            return snapshot is null
                ? Task.CompletedTask
                : OnProgressAsync(_session, snapshot, sequence);
        }

        internal async Task OnProgressAsync(DaemonSession pushedSession, PrefillProgress progress, long sequence)
        {
            if (!_armed || !_active || !ReferenceEquals(pushedSession, _session))
            {
                return;
            }

            // TRY-enter, never queue. If a send is already in flight this tick is DROPPED - the next
            // push (the daemons tick at 2-4Hz) carries fresher numbers anyway, so a dropped tick costs
            // nothing. Queueing here would be actively dangerous: the daemon AWAITS this handler, so a
            // slow hub would back up an unbounded queue of callbacks behind the gate, stalling the
            // daemon's own progress path, and a HUNG send would leave the gate held forever - the
            // drain below would then wait on it and the run would never emit its terminal at all.
            if (!await _gate.WaitAsync(0, CancellationToken.None))
            {
                return;
            }

            try
            {
                // Re-check inside the gate: the run's terminal path may have won the race while this
                // tick was queued. A live line must never land on a card that is already terminal.
                if (!_active
                    || !_session.IsPrefilling
                    || Volatile.Read(ref _session.TerminalCompletedFlag) != 0)
                {
                    return;
                }

                // Completion counters advance even for an OVERTAKEN tick: dropping a stale
                // app-completed payload must not lose the knowledge that the game actually finished,
                // or "game X of N" would count backwards. Downloading ticks omit these counters, which
                // is exactly why this is a running Math.Max and not a plain assignment.
                _appsCompleted = Math.Max(
                    _appsCompleted,
                    progress.UpdatedApps + progress.AlreadyUpToDate + progress.FailedApps);

                if (sequence <= _highestSequence)
                {
                    return;
                }

                _highestSequence = sequence;

                var totalApps = _selectedAppCount > 0 ? _selectedAppCount : progress.TotalApps;
                if (totalApps <= 0)
                {
                    return;
                }

                // Byte fraction of the game downloading RIGHT NOW. Only live "downloading" ticks count:
                // an app_completed tick's bytes belong to a game already inside _appsCompleted, so
                // treating them as in-flight would double-count it.
                var currentAppFraction = 0d;
                string? currentAppName = null;
                long? currentAppBytes = null;
                long? currentAppTotalBytes = null;
                if (PrefillProgressStateExtensions.ParseOrUnknown(progress.State) == PrefillProgressState.Downloading
                    && progress.TotalBytes > 0)
                {
                    currentAppFraction = Math.Clamp((double)progress.BytesDownloaded / progress.TotalBytes, 0d, 1d);
                    currentAppName = progress.CurrentAppName;
                    currentAppBytes = progress.BytesDownloaded;
                    currentAppTotalBytes = progress.TotalBytes;
                }

                var fraction = ScheduledPrefillRunGates.ComputeServiceFraction(_appsCompleted, totalApps, currentAppFraction);
                var percent = ScheduledPrefillRunGates.ComputeRunPercent(fraction);

                // The bar never goes backwards, even if an out-of-order tick reports a lower fraction.
                percent = Math.Max(percent, _highestPercent);
                _highestPercent = percent;

                var cappedCompleted = Math.Min(_appsCompleted, totalApps);
                var message = string.IsNullOrEmpty(currentAppName)
                    ? $"Prefill in progress ({cappedCompleted} of {totalApps} games)"
                    : $"Downloading {currentAppName} ({Math.Min(_appsCompleted + 1, totalApps)} of {totalApps} games)";

                var bytesMoved = currentAppBytes.HasValue && currentAppBytes.Value != _lastEmittedBytes;
                // Percent is derived exclusively from the completed-count/message and the current
                // downloaded bytes. Those two source values are the real change detector; the old
                // percent-delta threshold only compensated for the former ten-second sampler and is
                // redundant now that every daemon tick reaches this relay.
                var somethingMoved = message != _lastEmittedMessage || bytesMoved;
                if (!somethingMoved)
                {
                    return;
                }

                // Stage = the displayed message, so a new game emits AT ONCE; same-message byte/percent
                // revisions are held to LiveEmitMinIntervalMs. Suppressing a tick costs nothing - the
                // next push carries the newer bytes anyway.
                if (!_emitGate.ShouldEmit(message, Interlocked.Increment(ref _revision)))
                {
                    return;
                }

                _lastEmittedMessage = message;
                _lastEmittedBytes = currentAppBytes ?? -1L;

                await _owner.EmitProgressAsync(
                    _notifications,
                    _operationId,
                    _serviceConfig,
                    "running",
                    message,
                    _showNotification,
                    downloadSessionId: _sessionId,
                    percent: percent,
                    bytesDownloaded: currentAppBytes,
                    totalBytes: currentAppTotalBytes);
            }
            catch (Exception ex)
            {
                // A notification failure must never break the daemon's progress path, which awaits us.
                _owner._logger.LogWarning(
                    ex,
                    "[ScheduledPrefill] Failed to relay progress for session {SessionId}",
                    _sessionId);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Stops accepting pushes and waits for a send already inside the gate to finish. Deactivate
        /// THEN drain: unsubscribing alone is not enough, because a tick can already have snapshotted
        /// the delegate list, and one can already be awaiting SignalR.
        ///
        /// The wait is BOUNDED. An unbounded one would hand a hung hub send the power to wedge the
        /// whole run: the gate would never be released, so this would never return, and the service
        /// would never emit its completed/cancelled/failed terminal nor complete its tracked
        /// operation. Giving up after the timeout is strictly better than that - the relay is already
        /// deactivated, and if the stuck send ever does land, it is a "running" line on a card the
        /// client has already seen go terminal, which the notification handlers ignore.
        /// </summary>
        internal async Task DeactivateAndDrainAsync()
        {
            _active = false;

            if (await _gate.WaitAsync(_drainTimeout, CancellationToken.None))
            {
                _gate.Release();
                return;
            }

            _owner._logger.LogWarning(
                "[ScheduledPrefill] Progress relay for session {SessionId} did not drain within {Timeout}s; "
                    + "continuing to the terminal event without it",
                _sessionId,
                _drainTimeout.TotalSeconds);
        }
    }

    private static void MapPreset(ScheduledPrefillServiceConfigDto serviceConfig, out bool all, out bool recent, out int? top)
    {
        all = false;
        recent = false;
        top = null;

        switch (serviceConfig.Preset)
        {
            case ScheduledPrefillPreset.All:
                all = true;
                break;
            case ScheduledPrefillPreset.Recent:
                recent = true;
                break;
            case ScheduledPrefillPreset.Top:
                top = serviceConfig.TopCount;
                break;
        }
    }

    private static List<string>? MapOperatingSystems(List<ScheduledPrefillOperatingSystem> operatingSystems)
    {
        if (operatingSystems.Count == 0)
        {
            return null;
        }

        var mapped = new List<string>(operatingSystems.Count);
        foreach (var os in operatingSystems)
        {
            mapped.Add(os switch
            {
                ScheduledPrefillOperatingSystem.Windows => "windows",
                ScheduledPrefillOperatingSystem.Linux => "linux",
                ScheduledPrefillOperatingSystem.Macos => "macos",
                _ => os.ToString().ToLowerInvariant()
            });
        }

        return mapped;
    }

    private static string BuildCompletionMessage(DaemonSession session, bool hasSelectedApps, bool force)
    {
        var bytes = session.TotalBytesTransferred;
        if (bytes > 0)
        {
            return $"Prefill completed ({FormattingUtils.FormatBytes(bytes)} downloaded)";
        }

        if (hasSelectedApps && !force)
        {
            return "Prefill completed — all selected games were already cached (0 bytes). Enable Force to re-download.";
        }

        return "Prefill completed (0 bytes downloaded)";
    }

    private Task EmitProgressAsync(
        ISignalRNotificationService notifications,
        string operationId,
        ScheduledPrefillServiceConfigDto serviceConfig,
        string stage,
        string message,
        bool showNotification,
        string? needsLoginReason = null,
        long? bytesDownloaded = null,
        string? downloadSessionId = null,
        double? percent = null,
        long? totalBytes = null)
    {
        var serviceId = serviceConfig.ServiceId;

        if (string.IsNullOrEmpty(needsLoginReason))
        {
            _logger.LogInformation("[ScheduledPrefill] {Service} {Stage}: {Message}", serviceId, stage, message);
        }
        else
        {
            _logger.LogInformation(
                "[ScheduledPrefill] {Service} {Stage}: {Message} ({Reason})",
                serviceId,
                stage,
                message,
                needsLoginReason);
        }
        return notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillProgress, new
        {
            operationId,
            serviceId = serviceId.ToString(),
            stage,
            message,
            needsLoginReason,
            bytesDownloaded,
            totalBytes,
            downloadSessionId,
            percentComplete = percent,
            showNotification
        });
    }

    /// <summary>
    /// Resolves whether a platform's run allows a notification: its own per-platform
    /// NotificationMode evaluated against the trigger that produced the CURRENT tick. Consumed by
    /// the orchestrator to compute the run-level visibility (an OR across all due platforms), which
    /// then flows unchanged through <see cref="ScheduledPrefillOperationMetadata"/> and every emitted
    /// SignalR payload so the frontend never has to re-derive trigger provenance itself.
    /// NotificationMode is a required v4 field that <see cref="ScheduledPrefillConfigFactory.Validate"/>
    /// guarantees non-null before the scheduler reads a config; a null here is a contract violation,
    /// not a case to silently paper over by defaulting to All.
    /// </summary>
    private bool ResolveShowNotification(ScheduledPrefillServiceConfigDto serviceConfig)
    {
        var mode = serviceConfig.NotificationMode
            ?? throw new InvalidOperationException(
                $"Scheduled prefill service {serviceConfig.ServiceId} has a null NotificationMode; "
                    + "ScheduledPrefillConfigFactory.Validate must run before the scheduler reads it.");
        return mode.AllowsTrigger(CurrentRunTrigger);
    }
}
