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

            foreach (var serviceConfig in dueServices)
            {
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
                    default:
                        servicesSkipped++;
                        break;
                }
            }

            var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesRan, servicesNeedingLogin, servicesSkipped, servicesFailed);
            success = outcome.Success;
            error = outcome.Error;
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
            error = "Scheduled prefill run cancelled";
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
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillCompleted, new
            {
                operationId = operationIdString,
                success,
                error,
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
                if (PrefillProgressStateExtensions.ParseOrUnknown(snapshot.State) == PrefillProgressState.Downloading
                    && snapshot.TotalBytes > 0)
                {
                    currentAppFraction = Math.Clamp((double)snapshot.BytesDownloaded / snapshot.TotalBytes, 0d, 1d);
                    currentAppName = snapshot.CurrentAppName;
                }

                if (totalApps > 0)
                {
                    var fraction = ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted, totalApps, currentAppFraction);
                    var percent = Percent(fraction);
                    var cappedCompleted = Math.Min(appsCompleted, totalApps);
                    var message = string.IsNullOrEmpty(currentAppName)
                        ? $"Prefill in progress ({cappedCompleted} of {totalApps} games)"
                        : $"Downloading {currentAppName} ({Math.Min(appsCompleted + 1, totalApps)} of {totalApps} games)";

                    // Re-emit only when something the card shows actually moved; keeps the 10s poll
                    // quiet through long unchanged stretches.
                    if (message != lastEmittedMessage || Math.Abs(percent - lastEmittedPercent) >= 1d)
                    {
                        lastEmittedMessage = message;
                        lastEmittedPercent = percent;
                        await EmitProgressAsync(
                            notifications,
                            operationId,
                            serviceConfig,
                            "running",
                            message,
                            downloadSessionId: sessionId,
                            percent: percent);
                    }
                }
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                await daemon.CancelPrefillAsync(sessionId, CancellationToken.None);
                throw;
            }
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
        double? percent = null)
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
            downloadSessionId,
            percentComplete = percent,
            showNotification = serviceConfig.ShowNotification
        });
    }
}
