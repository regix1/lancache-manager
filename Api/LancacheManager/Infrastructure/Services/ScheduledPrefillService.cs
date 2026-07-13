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
    // INNER prefill-status poll: how often a running prefill's IsPrefilling flag is checked.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    // The poll interval is waited out in slices THIS small. A user who stops a prefill (from the
    // modal, which cancels the daemon session) expects the card to react at once; sleeping the whole
    // poll interval meant the run only noticed the stop up to ten seconds later, so the card sat
    // there looking alive and the user hammered the button. The slice only affects how fast a STOP
    // is noticed - progress is still emitted on the poll cadence, not per slice.
    private static readonly TimeSpan _stopDetectionSlice = TimeSpan.FromMilliseconds(250);

    // Smallest percent move that re-emits a progress event. A FULL point was too coarse: across a
    // multi-game run one point can be half a game, so a large download left the card motionless for
    // tens of minutes and looked frozen even though it was working. A tenth of a point means the
    // bar advances on essentially every poll while a download is live, and the gate still silences
    // a genuinely idle stretch.
    private const double ProgressEmitMinPercentDelta = 0.1d;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStateService _stateService;

    /// <summary>
    /// Set to 1 by <see cref="TriggerImmediateRun"/> (manual "Run Now" / "Run All") and consumed once
    /// by the next <see cref="ExecuteWorkAsync"/> tick, which then runs EVERY enabled service
    /// regardless of due. Read-and-cleared atomically via <see cref="Interlocked"/>.
    /// </summary>
    private int _manualRunBypass;

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

    /// <summary>
    /// Manual "Run Now" / "Run All" from the Schedules page: flag the next tick to bypass the
    /// per-service due-check and run EVERY enabled service, then wake the loop via the base.
    /// </summary>
    public override void TriggerImmediateRun()
    {
        Interlocked.Exchange(ref _manualRunBypass, 1);
        base.TriggerImmediateRun();
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
        // enabled service. Read-and-clear atomically so exactly one tick honors the request.
        var bypassDueCheck = Interlocked.Exchange(ref _manualRunBypass, 0) == 1;
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
        var notificationMetadata = new ScheduledPrefillOperationMetadata(
            dueServices[0].ShowNotification);
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

                // One tracker operation covers all due platforms. Keep recovery aligned with the
                // platform currently running so a reconnect neither resurrects a silent card nor
                // hides a visible one.
                notificationMetadata.ShowNotification = serviceConfig.ShowNotification;
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
        CancellationToken ct)
    {
        var serviceId = serviceConfig.ServiceId;

        // Percent for the run's universal notification: tracks THIS service's completion fraction
        // (games done + byte fraction of the game currently downloading). See ComputeRunPercent for
        // why the bar follows the active service instead of slicing across every due service.
        static double Percent(double serviceFraction) =>
            ScheduledPrefillRunGates.ComputeRunPercent(serviceFraction);

        // 1. Resolve the concrete daemon service for this platform.
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(serviceProvider, serviceId);
        if (daemon is null)
        {
            await EmitProgressAsync(notifications, operationId, serviceConfig, "skipped", "No daemon registered for this service", percent: Percent(1));
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
                needsLoginReason,
                percent: Percent(1));
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
                ScheduledPrefillRunGates.LoggedOutNeedsLoginReason,
                percent: Percent(1));
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
            await EmitProgressAsync(notifications, operationId, serviceConfig, "skipped", skipMessage, percent: Percent(1));
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceConfig,
            "starting",
            "Reusing persistent container",
            downloadSessionId: sessionId,
            percent: Percent(0));

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
            await EmitProgressAsync(notifications, operationId, serviceConfig, "skipped", "A prefill is already in progress", percent: Percent(1));
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        // A failed start may leave IsPrefilling already false, which would make the poll loop
        // exit immediately and wrongly report "completed". Treat a non-Success start as failed.
        if (!result.Success)
        {
            var failureMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Prefill failed to start"
                : result.ErrorMessage;
            await EmitProgressAsync(notifications, operationId, serviceConfig, "failed", failureMessage, percent: Percent(1));
            return ScheduledPrefillServiceRunResult.Failed;
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceConfig,
            "running",
            "Prefill in progress",
            downloadSessionId: sessionId,
            percent: Percent(0));

        // Poll until the daemon clears IsPrefilling, or a guard trips. The persistent container is
        // never terminated here; on cancellation we only cancel the in-flight prefill.
        var runDeadline = DateTime.UtcNow + config.MaxServiceRuntime;
        // Live progress for the universal notification. PrefillAsync nulls LastProgress before the
        // daemon starts, so the snapshot only ever reflects THIS run. The running updated/cached/
        // failed counters (same source as the Prefill tab's "Game X of N") only advance when a game
        // FINISHES, so a multi-gigabyte download would otherwise sit at the bar's start for hours:
        // downloading ticks also contribute the current game's byte fraction and its name. The
        // completed-count is kept monotonic locally because downloading ticks omit the counters.
        var selectedAppCount = serviceConfig.SelectedAppIds.Count;
        var appsCompleted = 0;
        var lastEmittedMessage = (string?)null;
        var lastEmittedPercent = double.NaN;
        var lastEmittedBytes = -1L;
        while (session.IsPrefilling)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow >= runDeadline)
            {
                await EmitProgressAsync(notifications, operationId, serviceConfig, "failed", "Exceeded maximum service runtime", percent: Percent(1));
                return ScheduledPrefillServiceRunResult.Failed;
            }

            if (PrefillDaemonServiceBase.IsPrefillStalled(session, DateTime.UtcNow, config.StallTimeout))
            {
                await EmitProgressAsync(notifications, operationId, serviceConfig, "failed", "Prefill stalled (no progress)", percent: Percent(1));
                return ScheduledPrefillServiceRunResult.Failed;
            }

            var snapshot = session.LastProgress;
            if (snapshot is not null)
            {
                appsCompleted = Math.Max(
                    appsCompleted,
                    snapshot.UpdatedApps + snapshot.AlreadyUpToDate + snapshot.FailedApps);
                var totalApps = selectedAppCount > 0 ? selectedAppCount : snapshot.TotalApps;

                // Byte-level fraction of the game currently downloading. Only live "downloading"
                // ticks count: an app_completed snapshot's bytes belong to a game that is already
                // inside appsCompleted, so treating them as in-flight would double-count it.
                var currentAppFraction = 0d;
                string? currentAppName = null;
                long? currentAppBytes = null;
                long? currentAppTotalBytes = null;
                if (PrefillProgressStateExtensions.ParseOrUnknown(snapshot.State) == PrefillProgressState.Downloading
                    && snapshot.TotalBytes > 0)
                {
                    currentAppFraction = Math.Clamp((double)snapshot.BytesDownloaded / snapshot.TotalBytes, 0d, 1d);
                    currentAppName = snapshot.CurrentAppName;
                    currentAppBytes = snapshot.BytesDownloaded;
                    currentAppTotalBytes = snapshot.TotalBytes;
                }

                if (totalApps > 0)
                {
                    var fraction = ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted, totalApps, currentAppFraction);
                    var percent = Percent(fraction);
                    var cappedCompleted = Math.Min(appsCompleted, totalApps);
                    var message = string.IsNullOrEmpty(currentAppName)
                        ? $"Prefill in progress ({cappedCompleted} of {totalApps} games)"
                        : $"Downloading {currentAppName} ({Math.Min(appsCompleted + 1, totalApps)} of {totalApps} games)";

                    // Re-emit when anything the card shows actually moved; keeps the 10s poll quiet
                    // through genuinely idle stretches.
                    //
                    // The BYTES are what make this feel alive. The run percent alone is not enough:
                    // it divides the current game's fraction by the number of games, so on a 20-game
                    // run the bar only creeps a tenth of a point once the active download has moved
                    // 2% - minutes on a large game, during which a working prefill looked frozen.
                    // Downloaded bytes advance on every tick of a live download, so the card's byte
                    // readout (and the bar, whenever it does move) keeps proving the run is working.
                    var bytesMoved = currentAppBytes.HasValue && currentAppBytes.Value != lastEmittedBytes;
                    if (message != lastEmittedMessage
                        || bytesMoved
                        || Math.Abs(percent - lastEmittedPercent) >= ProgressEmitMinPercentDelta)
                    {
                        lastEmittedMessage = message;
                        lastEmittedPercent = percent;
                        lastEmittedBytes = currentAppBytes ?? -1L;
                        await EmitProgressAsync(
                            notifications,
                            operationId,
                            serviceConfig,
                            "running",
                            message,
                            downloadSessionId: sessionId,
                            percent: percent,
                            bytesDownloaded: currentAppBytes,
                            totalBytes: currentAppTotalBytes);
                    }
                }
            }

            try
            {
                // Wait out the poll interval in slices, breaking the moment the prefill stops, so a
                // cancel is acted on in ~250ms instead of up to a full ten seconds. The emission
                // cadence is unchanged: a still-running prefill waits the whole interval as before.
                //
                // Counted slices, NOT a wall-clock deadline: DateTime.UtcNow is not monotonic, so an
                // NTP correction, a VM resume or an admin moving the clock backwards would otherwise
                // suspend the poll (and with it the stall detector and the run deadline) for the
                // length of the jump.
                var slices = (int)Math.Ceiling(_pollInterval / _stopDetectionSlice);
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

        // A prefill the user STOPPED leaves the poll loop above exactly like a natural finish: the
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
                downloadSessionId: sessionId,
                percent: Percent(1));
            return ScheduledPrefillServiceRunResult.Cancelled;
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceConfig,
            "completed",
            BuildCompletionMessage(session, hasSelectedApps, serviceConfig.Force),
            bytesDownloaded: session.TotalBytesTransferred,
            downloadSessionId: sessionId,
            percent: Percent(1));
        return ScheduledPrefillServiceRunResult.Ran;
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
            showNotification = serviceConfig.ShowNotification
        });
    }
}
