using System.Collections.Concurrent;
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
    /// Platforms that have actually run at least once in this process. The due-check reads it from the
    /// base scheduling loop between runs; the per-service tasks of one run write it concurrently, so
    /// every write takes the set as its lock. Backs the startup-only (<c>-1</c>) due-check, which
    /// fires once per process and ignores the persisted last-run.
    /// </summary>
    private readonly HashSet<PrefillPlatform> _ranThisProcess = new();

    /// <summary>
    /// Platforms a per-row Run button has asked for since the loop last woke. A manual tick with
    /// anything in here runs exactly those platforms instead of every enabled one, which is how a
    /// single platform gets run without widening the argument-free
    /// <see cref="ScheduledServiceBase.TriggerImmediateRun"/> that four other call sites share. [29]
    /// </summary>
    private readonly ConcurrentQueue<PrefillPlatform> _manualServiceRuns = new();

    /// <summary>
    /// Stable service key used by <see cref="ServiceScheduleRegistry"/> (read via reflection)
    /// and by <c>StateService.ServiceIntervals</c>.
    /// </summary>
    public string ScheduleServiceKey => "scheduledPrefill";

    /// <summary>
    /// Log-friendly name surfaced by the base class in log lines.
    /// </summary>
    protected override string ServiceName => "Scheduled Prefill";

    // The outer loop wakes once a minute and no-ops most ticks (nothing due). Emitting a run-START
    // schedule broadcast on every tick would flash this card's status dot green each minute, so opt out
    // of the automatic per-tick start. ExecuteWorkAsync instead calls RaiseExecutionStateChanged() only
    // when a real run begins (>= 1 due service), so the dot still lights for genuine runs. The base loop
    // keeps emitting the run-END broadcast every tick, which keeps the Last/Next-run readouts fresh and
    // reports the idle state.
    protected override bool BroadcastRunStart => false;

    /// <summary>
    /// Scheduled prefill should not fire automatically the instant the app starts.
    /// </summary>
    public override bool DefaultRunOnStartup => false;

    // Prefill runs are long and carry rich per-platform progress, so they keep the full card.
    public override NotificationDisplayMode DefaultNotificationDisplayMode => NotificationDisplayMode.Full;

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

    /// <inheritdoc />
    /// <remarks>
    /// The whole-schedule Run Now runs every enabled service, so it drops any platform a per-row Run
    /// button had queued. Left in place, one queued platform would narrow this run down to that
    /// platform alone, because the loop honors a single pending-run flag whichever button set it.
    /// </remarks>
    public override void TriggerImmediateRun()
    {
        _manualServiceRuns.Clear();
        base.TriggerImmediateRun();
    }

    /// <summary>
    /// Runs ONE platform immediately, outside the per-service due-check. Backs the Schedules page's
    /// per-row Run button, which needs a run it can name a platform for: the shared
    /// <see cref="ScheduledServiceBase.TriggerImmediateRun"/> takes no arguments and
    /// <c>scheduledPrefill</c> is a single registry key, so neither can express one platform.
    /// Throws when <paramref name="serviceId"/> is not a configured platform, matching
    /// <see cref="ScheduledPrefillConfigDto.GetEffectivePersistenceMode"/>'s treatment of the same
    /// bad input. [29]
    /// </summary>
    public void TriggerServiceRun(PrefillPlatform serviceId)
    {
        if (!Enum.IsDefined(serviceId))
        {
            throw new ArgumentOutOfRangeException(nameof(serviceId), serviceId, "Unknown scheduled prefill service id.");
        }

        _manualServiceRuns.Enqueue(serviceId);

        // base, not this: the override above exists to clear the queue for a whole-schedule run, and
        // clearing the platform this call just queued would run every enabled service instead.
        base.TriggerImmediateRun();
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var config = _stateService.GetScheduledPrefillConfig();

        // Platforms a per-row Run named since the last tick. A disabled service is never run, however
        // it was asked for: the enabled flag is the operator's statement that this platform should not
        // prefill, and an explicit request is not an exemption from it. The route rejects a disabled
        // platform up front so the press still gets an answer instead of a silent no-op. [47]
        var requestedServices = new HashSet<PrefillPlatform>();
        while (_manualServiceRuns.TryDequeue(out var requestedService))
        {
            requestedServices.Add(requestedService);
        }

        // Nothing enabled: skip the whole tick before building the due-set or touching any
        // daemon/session/tracker state. Saves the per-minute work while the feature is fully idle; the
        // schedule resumes normally as soon as any service is re-enabled. A queued request cannot keep
        // the tick alive on its own, because a disabled platform no longer runs on request. [47]
        if (!ScheduledPrefillRunGates.HasAnyEnabledService(config.GetServicesInRunOrder()))
        {
            _logger.LogDebug("[ScheduledPrefill] Skipping tick - no services are enabled");
            return;
        }

        // A whole-schedule "Run Now"/"Run All" bypasses the due-check for this single tick and runs
        // every enabled service. A per-row Run also arrives as a manual trigger, but it must NOT
        // bypass: it asked for one platform, and sweeping in every enabled service would make the row
        // button behave like the card-level one. The queue tells the two apart, because the
        // whole-schedule path clears it on its way through TriggerImmediateRun. [29][44]
        var bypassDueCheck = CurrentRunTrigger == RunTrigger.Manual && requestedServices.Count == 0;
        var now = DateTime.UtcNow;

        // Build the DUE set: every platform a per-row Run named, UNIONED with each enabled service
        // whose own custom schedule (when it has one) or IntervalHours, plus its persisted last-run,
        // says it should run this tick (or every enabled service when a whole-schedule manual run
        // bypassed the due-check). A union rather than a replacement: running one service on demand
        // must not cost a genuinely due sibling its turn on the same tick. [44]
        var dueServices = new List<ScheduledPrefillServiceConfigDto>();
        foreach (var serviceConfig in config.GetServicesInRunOrder())
        {
            // The enabled flag is answered FIRST and it governs every path into a run, including an
            // explicit per-row request. A disabled service does not prefill. [47]
            if (!serviceConfig.Enabled)
            {
                continue;
            }

            if (requestedServices.Contains(serviceConfig.ServiceId))
            {
                dueServices.Add(serviceConfig);
                continue;
            }

            if (bypassDueCheck)
            {
                dueServices.Add(serviceConfig);
                continue;
            }

            var lastRun = _stateService.GetScheduledPrefillServiceLastRun(serviceConfig.ServiceId.ToString());
            var hasRunThisProcess = _ranThisProcess.Contains(serviceConfig.ServiceId);
            if (ScheduledPrefillRunGates.IsServiceDue(serviceConfig.IntervalHours, lastRun, now, hasRunThisProcess, serviceConfig.CustomSchedule))
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

        await RunDueServicesAsync(dueServices, config, stoppingToken);
    }

    /// <summary>
    /// Runs one tick's due services side by side and reports the run as a whole. Each service also
    /// gets its OWN tracked operation, so its notification card carries its own id, its own progress
    /// and its own terminal, and cancelling that card stops only that platform. The run-level
    /// operation stays alongside them: it is what tells one run from the next and what the Schedules
    /// card's running state reads. [7][19][24]
    /// </summary>
    private async Task RunDueServicesAsync(
        List<ScheduledPrefillServiceConfigDto> dueServices,
        ScheduledPrefillConfigDto config,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ScheduledPrefill] Starting run for {Count} due service(s)", dueServices.Count);

        // The tracker owns every CTS handed to it after a successful RegisterOperation, so each one
        // below is linked to a parent token and never disposed here.
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

        // One tracked operation per due platform. Its id keys that platform's notification card, and
        // cancelling it cancels ONLY that platform: the token taken here is the one RunServiceAsync
        // hands to PrefillAsync and to the guard loop, whose cancellation path stops that platform's
        // daemon session. Each is linked to the run's token, so cancelling the whole run still stops
        // every platform. [3][19]
        var serviceRuns = new List<ScheduledPrefillServiceRun>(dueServices.Count);
        foreach (var dueService in dueServices)
        {
            var serviceCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            var serviceToken = serviceCts.Token;
            var serviceState = new ScheduledPrefillServiceRunState(dueService.ServiceId);
            var serviceOperationId = tracker.RegisterOperation(
                OperationType.ScheduledPrefill,
                $"Scheduled Prefill - {dueService.ServiceId}",
                serviceCts,
                serviceState);

            serviceRuns.Add(new ScheduledPrefillServiceRun(
                dueService,
                serviceOperationId,
                serviceOperationId.ToString(),
                operationIdString,
                serviceState,
                serviceToken));
        }

        // Light the Schedules status dot for this genuine run. The base loop suppresses the automatic
        // per-tick start (BroadcastRunStart = false) so an idle poll never flashes the card; raise it
        // here, AFTER RegisterOperation, so the serialized SchedulesUpdated snapshot sees the active
        // tracked operation (which is how this card's IsRunning is now derived). The base loop still
        // broadcasts the run-end once this method returns and the operation has completed.
        RaiseExecutionStateChanged();

        bool success = true;
        string? error = null;
        // Names the failure reason the card shows. Null on the paths whose text has no key: a
        // canceled run (the card writes its own translated line) and an unexpected exception,
        // whose message comes from .NET.
        string? errorStageKey = null;
        bool cancelled = false;

        try
        {
            // The run-level Started carries a null serviceId because it announces the RUN. Each due
            // service announces itself separately from RunAndStampServiceAsync, so a card is created
            // per service rather than one card five services take turns overwriting. [24]
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillStarted, new
            {
                operationId = operationIdString,
                runOperationId = operationIdString,
                serviceId = (string?)null,
                serviceCount = dueServices.Count,
                showNotification = notificationMetadata.ShowNotification
            });

            // Every due platform downloads inside its own persistent container, so the services run
            // side by side. Running them one after another made a multi-hour Steam prefill hold the
            // other four platforms until it finished.
            var results = await Task.WhenAll(serviceRuns.Select(serviceRun => RunAndStampServiceAsync(
                serviceRun,
                tracker,
                scope.ServiceProvider,
                notifications,
                config,
                runShowNotification,
                runToken)));

            var tally = ScheduledPrefillRunGates.TallyRunResults(results);
            if (tally.ReportsCancelled)
            {
                success = false;
                cancelled = true;
                error = "Scheduled prefill stopped";
                _logger.LogInformation("[ScheduledPrefill] Scheduled run was cancelled");
            }
            else
            {
                var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(tally.Ran, tally.NeedingLogin, tally.Skipped, tally.Failed);
                success = outcome.Success;
                error = outcome.Error;
                errorStageKey = outcome.StageKey;
            }

            // Make a stop STICK. A service the user stopped before it finished did not advance its
            // schedule basis in RunAndStampServiceAsync, so without this the one-minute poll
            // relaunches the very batch that was just stopped - a cancel that lasts 60 seconds. APP
            // SHUTDOWN is deliberately exempt: there the batch should run on the next start.
            if (runToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                StampScheduleBasis(dueServices);
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
                StampScheduleBasis(dueServices);
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
                runOperationId = operationIdString,
                serviceId = (string?)null,
                success,
                error,
                stageKey = errorStageKey,
                cancelled,
                showNotification = notificationMetadata.ShowNotification
            });

            // Tracker disposes the adopted CTS exactly once inside CompleteOperation; we must not.
            tracker.CompleteOperation(operationId, success, error, cancelled);
        }

        _logger.LogInformation("[ScheduledPrefill] Run complete");
    }

    /// <summary>
    /// Advances the SCHEDULE BASIS (and the process-ran marker behind the startup-only due-check) for
    /// every service given, so the one-minute poll does not immediately re-run a service this tick
    /// already attempted nor relaunch a batch the user just stopped. Deliberately NOT the genuine
    /// last-run: only a service that prefilled to completion stamps that. [11][12]
    /// </summary>
    private void StampScheduleBasis(IReadOnlyList<ScheduledPrefillServiceConfigDto> services)
    {
        foreach (var serviceConfig in services)
        {
            lock (_ranThisProcess)
            {
                _ranThisProcess.Add(serviceConfig.ServiceId);
            }

            _stateService.SetScheduledPrefillServiceLastRun(serviceConfig.ServiceId.ToString(), DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Runs one due service, records its attempt, and closes that service's own tracked operation and
    /// notification card. Neither a failure nor a stop inside one service may abort its siblings, so
    /// everything is caught here and nothing is rethrown: a rethrow makes the awaited
    /// <c>Task.WhenAll</c> throw, which discards every sibling's result along with the genuine
    /// last-run each of them had already earned. A cancel of this service's own token - its card's
    /// cancel, or the whole run's - reports <see cref="ScheduledPrefillServiceRunResult.Cancelled"/>;
    /// anything else reports <see cref="ScheduledPrefillServiceRunResult.Failed"/>. [4][9]
    /// </summary>
    private async Task<ScheduledPrefillServiceRunResult> RunAndStampServiceAsync(
        ScheduledPrefillServiceRun serviceRun,
        IUnifiedOperationTracker tracker,
        IServiceProvider serviceProvider,
        ISignalRNotificationService notifications,
        ScheduledPrefillConfigDto config,
        bool runShowNotification,
        CancellationToken runToken)
    {
        var serviceConfig = serviceRun.ServiceConfig;
        var result = ScheduledPrefillServiceRunResult.Skipped;

        // Set only when the service failed by THROWING. The gated failures below (max runtime,
        // stalled, failed to start) report themselves through a progress event first, so their text
        // is already the recorded line and this stays null for them. [42]
        string? failureMessage = null;
        try
        {
            // This service's own card is created here rather than by the run, so a platform that
            // skips in milliseconds still gets a card saying why instead of being invisible. [2][24]
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillStarted, new
            {
                operationId = serviceRun.OperationIdString,
                runOperationId = serviceRun.RunOperationId,
                serviceId = serviceConfig.ServiceId.ToString(),
                showNotification = runShowNotification
            });

            serviceRun.Token.ThrowIfCancellationRequested();

            result = await RunServiceAsync(
                serviceRun,
                serviceProvider,
                notifications,
                config,
                runShowNotification);
        }
        catch (OperationCanceledException) when (serviceRun.Token.IsCancellationRequested)
        {
            // Filtered on this service's token, not on any cancellation: an unrelated internal
            // timeout surfaces with the token un-cancelled and must still be reported as a failure.
            result = ScheduledPrefillServiceRunResult.Cancelled;
            _logger.LogInformation("[ScheduledPrefill] Service {Service} was stopped", serviceConfig.ServiceId);
        }
        catch (Exception ex)
        {
            result = ScheduledPrefillServiceRunResult.Failed;
            failureMessage = ex.Message;
            _logger.LogError(ex, "[ScheduledPrefill] Service {Service} failed; continuing", serviceConfig.ServiceId);
        }
        finally
        {
            // Stamp the SCHEDULE-BASIS last-run + mark process-ran for EVERY due service we attempted
            // this tick (including skips/failures), so the 1-minute poll does not immediately re-run it
            // (recurring) nor re-fire a startup-only service. A still-needs-login service then
            // retries on its next interval rather than spamming a Started/Completed cycle every
            // minute. A cancelled run is exempt here and handled once by the run itself, which is the
            // only place that can tell a user's stop (stamp, so it sticks) from app shutdown.
            if (!runToken.IsCancellationRequested)
            {
                StampScheduleBasis([serviceConfig]);
            }

            // Stamp the GENUINE last-run (the "Last run" the schedule view shows) ONLY when the
            // service actually ran its prefill to completion. A skip / needs-login / failure advances
            // the schedule basis above but must NOT count as a real run, so the UI keeps reading
            // "Never" until the service has truly prefilled at least once. Deliberately OUTSIDE the
            // cancellation guard: a platform that finished its prefill finished it whether or not a
            // sibling was stopped a moment later. [9][12]
            if (result == ScheduledPrefillServiceRunResult.Ran)
            {
                _stateService.SetScheduledPrefillServiceLastActualRun(serviceConfig.ServiceId.ToString(), DateTime.UtcNow);
            }

            await CompleteServiceRunAsync(serviceRun, tracker, notifications, result, runShowNotification, failureMessage);
        }

        return result;
    }

    /// <summary>
    /// Closes one service's tracked operation and its notification card, so each card reaches its own
    /// terminal when THAT service finishes rather than when the slowest sibling does. A skip is
    /// completed as skipped, not failed: a prerequisite gap is not an error. [4][24]
    /// </summary>
    /// <param name="failureMessage">
    /// The thrown exception's message when the service failed by throwing, otherwise null. It takes
    /// precedence over the recorded progress line for a failure, because that line says what the
    /// service was DOING when it threw rather than what went wrong: without this the card reads
    /// "failed: Prefill in progress" and the real error never reaches the operator. A throw landing
    /// before the first progress event leaves the recorded line empty, which is the same defect
    /// wearing a blanker face. [42]
    /// </param>
    private static async Task CompleteServiceRunAsync(
        ScheduledPrefillServiceRun serviceRun,
        IUnifiedOperationTracker tracker,
        ISignalRNotificationService notifications,
        ScheduledPrefillServiceRunResult result,
        bool showNotification,
        string? failureMessage)
    {
        var success = result == ScheduledPrefillServiceRunResult.Ran;
        var cancelled = result == ScheduledPrefillServiceRunResult.Cancelled;
        var skipped = result is ScheduledPrefillServiceRunResult.Skipped or ScheduledPrefillServiceRunResult.NeedsLogin;

        // A skip and a needs-login keep the recorded line, which IS their reason. A gated failure
        // keeps it too, because it reported itself through a progress event before returning.
        var error = success || cancelled ? null : failureMessage ?? serviceRun.State.Message;

        // The .NET exception message has no key to translate it by, exactly as the run-level terminal
        // already documents, and the recorded key belongs to the progress line this is replacing.
        var stageKey = success || cancelled || failureMessage is not null ? null : serviceRun.State.StageKey;

        await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillCompleted, new
        {
            operationId = serviceRun.OperationIdString,
            runOperationId = serviceRun.RunOperationId,
            serviceId = serviceRun.ServiceConfig.ServiceId.ToString(),
            success = success || skipped,
            error,
            stageKey,
            cancelled,
            // The same wire word the tracker puts on a run that did nothing, so the card closes as
            // skipped rather than reading "failed" for a missing container or a logged-out one. [2]
            status = skipped ? "skipped" : null,
            showNotification
        });

        tracker.CompleteOperation(serviceRun.OperationId, success || skipped, error, cancelled, skipped);
    }

    /// <summary>
    /// Runs a single service's scheduled prefill: resolves the concrete daemon, reuses the running
    /// persistent admin container (which authenticates itself from its named auth volume), performs
    /// the needs-login + busy gates, and drives the prefill on that persistent session. The
    /// persistent container is system-owned and long-lived, so it is never created or torn down
    /// here. Emits <see cref="SignalREvents.ScheduledPrefillProgress"/> at each stage, stamped with
    /// this service's own operation id.
    /// </summary>
    /// <returns>How the service's run ended — see <see cref="ScheduledPrefillServiceRunResult"/>.</returns>
    private async Task<ScheduledPrefillServiceRunResult> RunServiceAsync(
        ScheduledPrefillServiceRun serviceRun,
        IServiceProvider serviceProvider,
        ISignalRNotificationService notifications,
        ScheduledPrefillConfigDto config,
        bool runShowNotification)
    {
        var serviceConfig = serviceRun.ServiceConfig;
        var serviceId = serviceConfig.ServiceId;
        var ct = serviceRun.Token;

        // 1. Resolve the concrete daemon service for this platform.
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(serviceProvider, serviceId);
        if (daemon is null)
        {
            await ReportProgressAsync(notifications, serviceRun, "skipped", "No daemon registered for this service", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1), stageKey: "signalr.scheduledPrefill.skippedNoDaemon");
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        // 2. Reuse the running persistent admin container. Scheduled prefill is admin-only and
        // downloads INSIDE the long-lived persistent container, which authenticates itself from its
        // named auth volume. It never spawns a temporary guest container and never injects a token.
        // Battle.net and Riot prefill anonymously, so neither gate below can be a login problem for
        // them. Reporting one would tell the user to sign in to something that has no sign-in, and
        // would make the run summary read "All due services need login" for a platform that has none.
        var requiresLogin = serviceId.RequiresLogin();

        var persistentSession = daemon.GetActivePersistentSession();
        if (!ScheduledPrefillRunGates.TryGetRunnablePersistentSession(persistentSession, out var sessionId, out var needsLoginReason))
        {
            await ReportProgressAsync(
                notifications,
                serviceRun,
                requiresLogin ? "needs-login" : "skipped",
                ScheduledPrefillRunGates.BuildNeedsLoginMessage(serviceId, containerRunning: false),
                runShowNotification,
                requiresLogin ? needsLoginReason : ScheduledPrefillRunGates.NoContainerReason,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                // The card shows the needs-login reason on one branch and the message on the other,
                // so one key covers whichever sentence this event actually puts on screen.
                stageKey: requiresLogin
                    ? "signalr.scheduledPrefill.needsPersistentContainer"
                    : "signalr.scheduledPrefill.skippedNoContainer");
            return requiresLogin
                ? ScheduledPrefillServiceRunResult.NeedsLogin
                : ScheduledPrefillServiceRunResult.Skipped;
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
            await ReportProgressAsync(
                notifications,
                serviceRun,
                requiresLogin ? "needs-login" : "skipped",
                requiresLogin
                    ? ScheduledPrefillRunGates.BuildNeedsLoginMessage(serviceId, containerRunning: true)
                    : ScheduledPrefillRunGates.BuildNotReadyMessage(serviceId),
                runShowNotification,
                requiresLogin
                    ? ScheduledPrefillRunGates.LoggedOutNeedsLoginReason
                    : ScheduledPrefillRunGates.ContainerNotReadyReason,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                stageKey: requiresLogin
                    ? "signalr.scheduledPrefill.needsPersistentLogin"
                    : "signalr.scheduledPrefill.skippedContainerNotReady");
            return requiresLogin
                ? ScheduledPrefillServiceRunResult.NeedsLogin
                : ScheduledPrefillServiceRunResult.Skipped;
        }

        // 3. Busy check: defer only when the persistent container this run already resolved is itself
        // prefilling, i.e. a prior run is still going. A temporary or guest container is a separate
        // entity with its own container and its own download, so it never blocks a scheduled run,
        // whether or not it is downloading. [5][13]
        if (ScheduledPrefillRunGates.ShouldSkipForBusySessions(
                session,
                out var skipMessage,
                out var skipStageKey))
        {
            await ReportProgressAsync(notifications, serviceRun, "skipped", skipMessage, runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1), stageKey: skipStageKey);
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        await ReportProgressAsync(
            notifications,
            serviceRun,
            "starting",
            "Reusing persistent container",
            runShowNotification,
            downloadSessionId: sessionId,
            percent: ScheduledPrefillRunGates.ComputeRunPercent(0),
            stageKey: "signalr.scheduledPrefill.reusingContainer");

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
            await ReportProgressAsync(notifications, serviceRun, "skipped", "A prefill is already in progress", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1));
            return ScheduledPrefillServiceRunResult.Skipped;
        }

        // A failed start may leave IsPrefilling already false, which would make the poll loop
        // exit immediately and wrongly report "completed". Treat a non-Success start as failed.
        if (!result.Success)
        {
            var daemonFailure = !string.IsNullOrWhiteSpace(result.ErrorMessage);
            var failureMessage = daemonFailure ? result.ErrorMessage! : "Prefill failed to start";
            await ReportProgressAsync(
                notifications,
                serviceRun,
                "failed",
                failureMessage,
                runShowNotification,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                // The daemon's own text has no key to translate it by, so only the generic sentence
                // this method wrote itself carries one.
                stageKey: daemonFailure ? null : "signalr.scheduledPrefill.failedToStart");
            return ScheduledPrefillServiceRunResult.Failed;
        }

        await ReportProgressAsync(
            notifications,
            serviceRun,
            "running",
            "Prefill in progress",
            runShowNotification,
            downloadSessionId: sessionId,
            percent: ScheduledPrefillRunGates.ComputeRunPercent(0),
            stageKey: "signalr.scheduledPrefill.running");

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
            serviceRun,
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
                // The whole body sits inside the try, because the catch is the only thing that stops
                // the container. A cancel landing between the delay loop below exiting and the check
                // at the top would otherwise throw straight past it, leaving the daemon downloading
                // while this service's card says it stopped. The window is microseconds wide, but
                // every card now has its own cancel, so it is entered once per running service
                // instead of once per run. [45]
                try
                {
                    ct.ThrowIfCancellationRequested();

                    if (DateTime.UtcNow >= runDeadline)
                    {
                        await StopRelayAsync();
                        await ReportProgressAsync(notifications, serviceRun, "failed", "Exceeded maximum service runtime", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1), stageKey: "signalr.scheduledPrefill.failedMaxRuntime");
                        return ScheduledPrefillServiceRunResult.Failed;
                    }

                    if (PrefillDaemonServiceBase.IsPrefillStalled(session, DateTime.UtcNow, config.StallTimeout))
                    {
                        await StopRelayAsync();
                        await ReportProgressAsync(notifications, serviceRun, "failed", "Prefill stalled (no progress)", runShowNotification, percent: ScheduledPrefillRunGates.ComputeRunPercent(1), stageKey: "signalr.scheduledPrefill.failedStalled");
                        return ScheduledPrefillServiceRunResult.Failed;
                    }

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
                await ReportProgressAsync(
                    notifications,
                    serviceRun,
                    "cancelled",
                    "Prefill stopped",
                    runShowNotification,
                    downloadSessionId: sessionId,
                    percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                    stageKey: "signalr.scheduledPrefill.stopped");
                return ScheduledPrefillServiceRunResult.Cancelled;
            }

            var completion = BuildCompletionMessage(session, hasSelectedApps, serviceConfig.Force);
            await ReportProgressAsync(
                notifications,
                serviceRun,
                "completed",
                completion.Message,
                runShowNotification,
                bytesDownloaded: session.TotalBytesTransferred,
                downloadSessionId: sessionId,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                stageKey: completion.StageKey,
                stageContext: completion.Context);
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
        private readonly ScheduledPrefillServiceRun _serviceRun;
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
            ScheduledPrefillServiceRun serviceRun,
            string sessionId,
            bool showNotification)
        {
            _owner = owner;
            _notifications = notifications;
            _session = session;
            _serviceRun = serviceRun;
            _sessionId = sessionId;
            _selectedAppCount = serviceRun.ServiceConfig.SelectedAppIds.Count;
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
                var downloadingCount = Math.Min(_appsCompleted + 1, totalApps);
                var message = string.IsNullOrEmpty(currentAppName)
                    ? $"Prefill in progress ({cappedCompleted} of {totalApps} games)"
                    : $"Downloading {currentAppName} ({downloadingCount} of {totalApps} games)";
                var stageKey = string.IsNullOrEmpty(currentAppName)
                    ? "signalr.scheduledPrefill.runningWithCounts"
                    : "signalr.scheduledPrefill.downloadingGame";
                var stageContext = string.IsNullOrEmpty(currentAppName)
                    ? new Dictionary<string, object?>
                    {
                        ["completed"] = cappedCompleted,
                        ["total"] = totalApps
                    }
                    : new Dictionary<string, object?>
                    {
                        ["game"] = currentAppName,
                        ["completed"] = downloadingCount,
                        ["total"] = totalApps
                    };

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

                await _owner.ReportProgressAsync(
                    _notifications,
                    _serviceRun,
                    "running",
                    message,
                    _showNotification,
                    downloadSessionId: _sessionId,
                    percent: percent,
                    bytesDownloaded: currentAppBytes,
                    totalBytes: currentAppTotalBytes,
                    stageKey: stageKey,
                    stageContext: stageContext);
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

    private static (string Message, string StageKey, Dictionary<string, object?>? Context) BuildCompletionMessage(
        DaemonSession session,
        bool hasSelectedApps,
        bool force)
    {
        var bytes = session.TotalBytesTransferred;
        if (bytes > 0)
        {
            var downloaded = FormattingUtils.FormatBytes(bytes);
            return (
                $"Prefill completed ({downloaded} downloaded)",
                "signalr.scheduledPrefill.completeWithBytes",
                new Dictionary<string, object?> { ["bytes"] = downloaded });
        }

        if (hasSelectedApps && !force)
        {
            return (
                "Prefill completed, all selected games were already cached (0 bytes). Enable Force to re-download.",
                "signalr.scheduledPrefill.completeAllCached",
                null);
        }

        return (
            "Prefill completed (0 bytes downloaded)",
            "signalr.scheduledPrefill.completeNoBytes",
            null);
    }

    private Task ReportProgressAsync(
        ISignalRNotificationService notifications,
        ScheduledPrefillServiceRun serviceRun,
        string stage,
        string message,
        bool showNotification,
        string? needsLoginReason = null,
        long? bytesDownloaded = null,
        string? downloadSessionId = null,
        double? percent = null,
        long? totalBytes = null,
        string? stageKey = null,
        Dictionary<string, object?>? stageContext = null)
    {
        var serviceId = serviceRun.ServiceConfig.ServiceId;

        // Mirror the line onto this service's own tracked operation before sending it, so a browser
        // that reloads mid-run rebuilds this service's card from the run-status endpoint instead of
        // waiting for the next tick that may be minutes away. [25]
        serviceRun.State.Record(stage, message, stageKey, percent);

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
            // This SERVICE's operation id: the card it lands on is keyed by service, and its cancel
            // must reach this platform alone. The run's id rides alongside so the browser can still
            // tell one run from the next. [19]
            operationId = serviceRun.OperationIdString,
            runOperationId = serviceRun.RunOperationId,
            serviceId = serviceId.ToString(),
            stage,
            message,
            // Names the one sentence this event puts on the card - the skip reason, the needs-login
            // reason, or the progress line - so the browser can render it in the reader's language.
            // The English above stays on the wire for the log and as the fallback for a build whose
            // locale has no words for the key yet.
            stageKey,
            stageContext,
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
