using System.Collections.Concurrent;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
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

    // The poll interval is waited out in slices THIS small. A user who stops a prefill (from the
    // modal, which cancels the daemon session) expects the card to react at once; sleeping the whole
    // poll interval meant the run only noticed the stop up to ten seconds later, so the card sat
    // there looking alive and the user hammered the button. The slice only affects how fast a STOP
    // is noticed; progress is pushed independently by the daemon relay.
    private static readonly TimeSpan _stopDetectionSlice = TimeSpan.FromMilliseconds(250);

    // The longest a sign-in that ends by itself holds the login lock is a person's "Use my saved login":
    // two 30 s round trips, a 10 s status read, up to 2 min for the daemon and a 10 s cancel, 3 min 20 s
    // in all. An automatic sign-in holds it about 80 s.
    private static readonly TimeSpan _signInLockWait = TimeSpan.FromMinutes(4);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStateService _stateService;
    private readonly object _scheduleLock = new();

    /// <summary>
    /// Platforms that have actually run at least once in this process. The due-check reads it from the
    /// base scheduling loop between runs; the per-service tasks of one run write it concurrently, so
    /// every write takes the set as its lock. Backs the startup-only (<c>-1</c>) due-check, which
    /// fires once per process and ignores the persisted last-run.
    /// </summary>
    private readonly HashSet<Guid> _ranThisProcess = new();

    /// <summary>
    /// Each daemon reserves its own operation slots; these schedule claims prevent duplicate records.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Guid> _runningSchedules = new();

    /// <summary>
    /// Per-row runs execute OUTSIDE the scheduling loop. The loop awaits one tick at a time, so a tick
    /// holding a multi-hour download made an on-demand run for a different platform wait for it to
    /// finish. Each entry is one detached run, kept so <see cref="StopAsync"/> can await it: this
    /// service is registered after the daemons and therefore stops FIRST, so a run left unawaited would
    /// have its container torn down underneath it.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task> _detachedRuns = new();

    /// <summary>
    /// Cancels the detached runs above. Tied to this service's own stop rather than the loop's tick
    /// token, because a detached run outlives the tick that started it.
    /// </summary>
    private readonly CancellationTokenSource _detachedRunLifetime = new();

    /// <summary>
    /// The app's one stop deadline. A cancel sent to a daemon while the app stops ends at it, because the
    /// daemon services stop only after this service has, and Docker's stop time is shared by all of them.
    /// Never canceled until the app begins to stop.
    /// </summary>
    private CancellationToken _stopBudget;

    internal void SetStopBudget(CancellationToken budget) => _stopBudget = budget;

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
    protected override bool QueueManualRuns => false;

    // A pass a person or Run All started does the work from the moment it is taken. Otherwise the
    // schedule is busy only while a run-level operation is active, which also covers a run the pass
    // started in the background and outlives it; that is GetRunStatus's signal. A per-platform
    // operation alone (a restored run, or the per-row Run button) does not count.
    protected internal override bool IsDoingWork(RunNotice? taken)
    {
        if (taken is { Trigger: RunTrigger.Manual or RunTrigger.RunAll }) return true;
        if (Tracker is null) return taken is not null;
        return Tracker.GetActiveOperations(OperationType.ScheduledPrefill)
            .Any(operation => operation.Metadata is not ScheduledPrefillServiceRunState);
    }

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
        => ScheduledPrefillRunGates.HasAnyEnabledService(
            _stateService.GetScheduledPrefillConfig().GetSchedulesInRunOrder());

    public ScheduledPrefillConfigDto UpdateConfig(Func<ScheduledPrefillConfigDto, ScheduledPrefillConfigDto> update)
    {
        using var scope = _scopeFactory.CreateScope();
        lock (_scheduleLock)
        {
            var activeIds = _runningSchedules.Keys.ToHashSet();
            var tracker = scope.ServiceProvider.GetRequiredService<IUnifiedOperationTracker>();
            foreach (var operation in tracker.GetActiveOperations(OperationType.ScheduledPrefill))
                if (operation.Metadata is ScheduledPrefillServiceRunState state)
                    activeIds.Add(state.ScheduleId);

            var recovering = false;
            foreach (var platform in Enum.GetValues<PrefillPlatform>())
            {
                var session = PrefillDaemonServiceBase.ResolveDaemon(scope.ServiceProvider, platform)?.GetActivePersistentSession();
                if (session is null) continue;
                recovering |= session.Recovering || session.AdmissionClosed;
                foreach (var run in session.Runs.Values)
                {
                    recovering |= run.Recovering;
                    if (!run.CompletedAtUtc.HasValue && run.PrefillScheduleId is { } id)
                        activeIds.Add(id);
                }
            }

            return _stateService.UpdateScheduledPrefillConfig(update, removedIds =>
            {
                if (removedIds.Count > 0 && (recovering || removedIds.Overlaps(activeIds)))
                    throw new ConflictException("An active or recovering scheduled prefill record cannot be deleted.");
            });
        }
    }

    /// <summary>
    /// Runs ONE platform right now, on its own task, outside the scheduling loop. Backs the Schedules
    /// page's per-row Run button. It cannot go through the loop: the loop awaits one tick at a time, so
    /// a tick holding a multi-hour download would make this wait for that download to finish, which is
    /// the opposite of what the button says. Returns the operation id when a run was started, or
    /// null when the named record does not exist or its platform is already running.
    /// Throws when <paramref name="serviceId"/> is not a configured platform, matching
    /// <see cref="ScheduledPrefillConfigDto.GetEffectivePersistenceMode"/>'s treatment of the same
    /// bad input.
    /// </summary>
    public Guid? TriggerServiceRun(
        PrefillPlatform serviceId,
        Guid scheduleId,
        ScheduleActor? actor = null)
    {
        if (!Enum.IsDefined(serviceId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(serviceId),
                serviceId,
                "Unknown scheduled prefill service id.");
        }

        IServiceScope? scope = null;
        CancellationTokenSource? cts = null;
        Guid? operationId = null;
        var claimId = Guid.Empty;
        try
        {
            scope = _scopeFactory.CreateScope();
            ScheduledPrefillConfigDto config;
            ScheduledPrefillServiceConfigDto? serviceConfig;
            lock (_scheduleLock)
            {
                config = _stateService.GetScheduledPrefillConfig();
                serviceConfig = config.GetSchedulesInRunOrder().FirstOrDefault(schedule =>
                    schedule.ServiceId == serviceId && schedule.ScheduleId == scheduleId);
                if (serviceConfig is null || !TryClaimRun(serviceConfig, out claimId))
                {
                    scope.Dispose();
                    return null;
                }
            }
            var tracker = scope.ServiceProvider.GetRequiredService<IUnifiedOperationTracker>();
            var notifications = scope.ServiceProvider.GetRequiredService<ISignalRNotificationService>();
            var mode = serviceConfig.NotificationMode
                ?? throw new InvalidOperationException("Scheduled prefill notification mode is required.");
            var state = new ScheduledPrefillServiceRunState(
                serviceId, serviceConfig.ScheduleId, serviceConfig.ScheduleName,
                new RunNotice(mode, RunTrigger.Manual, actor));
            cts = CancellationTokenSource.CreateLinkedTokenSource(_detachedRunLifetime.Token);
            var token = cts.Token;
            operationId = tracker.RegisterOperation(
                OperationType.ScheduledPrefill,
                $"Scheduled Prefill - {serviceId} - {serviceConfig.ScheduleName}",
                cts, state, notice: state.Notice);
            var operationIdString = operationId.Value.ToString();
            var run = new ScheduledPrefillServiceRun(
                serviceConfig, operationId.Value, operationIdString, operationIdString, state, token, ClaimId: claimId);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _detachedRuns[operationId.Value] = RunOneServiceAsync(
                run, config, scope, tracker, notifications, ready.Task);
            ready.SetResult();
            return operationId;
        }
        catch
        {
            if (!operationId.HasValue) cts?.Dispose();
            scope?.Dispose();
            ReleaseRun(scheduleId, claimId);
            throw;
        }
    }

    /// <summary>
    /// Reserves one schedule.
    /// </summary>
    private bool TryClaimRun(ScheduledPrefillServiceConfigDto schedule, out Guid claimId)
    {
        claimId = Guid.NewGuid();
        return _runningSchedules.TryAdd(schedule.ScheduleId, claimId);
    }

    private void ReleaseRun(Guid scheduleId, Guid claimId)
    {
        _runningSchedules.TryRemove(new KeyValuePair<Guid, Guid>(scheduleId, claimId));
    }

    private async Task RunOneServiceAsync(
        ScheduledPrefillServiceRun serviceRun,
        ScheduledPrefillConfigDto config,
        IServiceScope scope,
        IUnifiedOperationTracker tracker,
        ISignalRNotificationService notifications,
        Task ready)
    {
        await ready;
        var dispatched = false;
        try
        {
            RaiseExecutionStateChanged();
            dispatched = true;
            await RunAndStampServiceAsync(
                serviceRun,
                tracker,
                scope.ServiceProvider,
                notifications,
                config,
                _detachedRunLifetime.Token,
                admitted: true,
                tickAdopt: null);
        }
        catch (OperationCanceledException) when (_detachedRunLifetime.IsCancellationRequested)
        {
            _logger.LogInformation(
                "[ScheduledPrefill] On-demand run for {Service} stopped at shutdown",
                serviceRun.ServiceConfig.ServiceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[ScheduledPrefill] On-demand run for {Service} failed",
                serviceRun.ServiceConfig.ServiceId);
        }
        finally
        {
            try
            {
                if (!dispatched)
                {
                    await ReportProgressAsync(notifications, serviceRun, "failed", "Scheduled prefill could not start.",
                        percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                        stageKey: "signalr.scheduledPrefill.failedToStart");
                    await CompleteServiceRunAsync(serviceRun, tracker, notifications,
                        ScheduledPrefillServiceRunResult.Failed, null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ScheduledPrefill] Could not publish completion for {Service}", serviceRun.ServiceConfig.ServiceId);
            }
            finally
            {
                _detachedRuns.TryRemove(serviceRun.OperationId, out _);
                ReleaseRun(serviceRun.ServiceConfig.ScheduleId, serviceRun.ClaimId);
                scope.Dispose();
                RaiseExecutionStateChanged();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Detached per-row runs outlive the tick that started them, so the base stop is not enough. This
    /// service is registered after the prefill daemons and therefore stops FIRST: returning while a run
    /// is still going would let the daemons tear its session down underneath it. Cancel, then wait,
    /// bounded by the caller's own shutdown token so a stuck run cannot hold the host open.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _detachedRunLifetime.CancelAsync();

        var running = _detachedRuns.Values.ToArray();
        if (running.Length > 0)
        {
            _logger.LogInformation("[ScheduledPrefill] Waiting for {Count} on-demand run(s) to stop", running.Length);
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(Timeout.Infinite, cancellationToken));
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var config = _stateService.GetScheduledPrefillConfig();
        var schedules = config.GetSchedulesInRunOrder();
        RestoreRuns(config);

        if (!ScheduledPrefillRunGates.HasAnyEnabledService(schedules))
        {
            _logger.LogDebug("[ScheduledPrefill] Skipping tick - no schedules are enabled");
            return;
        }

        var parentNotice = CurrentRunNotice;
        var trigger = parentNotice.Trigger;
        var bypassDueCheck = trigger is RunTrigger.Manual or RunTrigger.RunAll;
        var now = DateTime.UtcNow;
        var dueSchedules = new List<ScheduledPrefillServiceConfigDto>();
        foreach (var schedule in schedules)
        {
            if (!schedule.Enabled)
            {
                continue;
            }

            if (bypassDueCheck)
            {
                dueSchedules.Add(schedule);
                continue;
            }

            var key = schedule.ScheduleId.ToString("N");
            var lastRun = _stateService.GetScheduledPrefillServiceLastRun(key);
            bool hasRunThisProcess;
            lock (_ranThisProcess)
            {
                hasRunThisProcess = _ranThisProcess.Contains(schedule.ScheduleId);
            }

            if (ScheduledPrefillRunGates.IsServiceDue(
                    schedule.IntervalHours,
                    lastRun,
                    now,
                    hasRunThisProcess,
                    schedule.CustomSchedule))
            {
                dueSchedules.Add(schedule);
            }
        }

        if (dueSchedules.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();

        // Each run of this tick reuses its platform's adopt, so a Docker that never answers costs one wait per platform.
        var tickAdopts = new Dictionary<PrefillPlatform, Task<DaemonSession?>>();
        foreach (var serviceId in dueSchedules.Select((ScheduledPrefillServiceConfigDto schedule) => schedule.ServiceId).Distinct())
        {
            var daemon = PrefillDaemonServiceBase.ResolveDaemon(scope.ServiceProvider, serviceId);
            if (daemon is null || daemon.GetActivePersistentSession() is not null)
            {
                continue;
            }

            var adopt = daemon.AdoptRunningPersistentSessionAsync(stoppingToken);
            tickAdopts[serviceId] = adopt;
            try
            {
                await adopt;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The run's own adopt meets the same failure and reports it on the card.
                _logger.LogDebug(ex, "[ScheduledPrefill] Could not adopt the {Service} container before the tick", serviceId);
            }
        }

        var tickId = Guid.NewGuid();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = RunDueServicesAsync(dueSchedules, config, _detachedRunLifetime.Token, parentNotice, tickAdopts);
        _detachedRuns[tickId] = WatchTickAsync(work, tickId, ready.Task);
        ready.SetResult();
    }

    private async Task WatchTickAsync(Task work, Guid tickId, Task ready)
    {
        await ready;
        try
        {
            await work;
        }
        catch (OperationCanceledException) when (_detachedRunLifetime.IsCancellationRequested)
        {
            _logger.LogInformation("[ScheduledPrefill] Scheduled run detached at shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ScheduledPrefill] Scheduled run failed");
        }
        finally
        {
            _detachedRuns.TryRemove(tickId, out _);
            RaiseExecutionStateChanged();
        }
    }

    /// <summary>
    /// Runs one tick's due services side by side and reports the run as a whole. Each service also
    /// gets its OWN tracked operation, so its notification card carries its own id, its own progress
    /// and its own terminal, and cancelling that card stops only that platform. The run-level
    /// operation stays alongside them: it is what tells one run from the next and what the Schedules
    /// card's running state reads.
    /// </summary>
    private async Task RunDueServicesAsync(
        List<ScheduledPrefillServiceConfigDto> dueServices,
        ScheduledPrefillConfigDto config,
        CancellationToken stoppingToken,
        RunNotice parentNotice,
        Dictionary<PrefillPlatform, Task<DaemonSession?>> tickAdopts)
    {
        var trigger = parentNotice.Trigger;
        _logger.LogInformation("[ScheduledPrefill] Starting run for {Count} due service(s)", dueServices.Count);
        using var scope = _scopeFactory.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<IUnifiedOperationTracker>();
        var notifications = scope.ServiceProvider.GetRequiredService<ISignalRNotificationService>();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var runToken = cts.Token;
        var serviceRuns = new List<ScheduledPrefillServiceRun>(dueServices.Count);
        var claimedPlatforms = new Dictionary<Guid, Guid>();
        var dispatched = new HashSet<Guid>();
        Guid? operationId = null;
        var success = true;
        string? error = null;
        string? errorStageKey = null;
        var cancelled = false;

        try
        {
            lock (_scheduleLock)
            {
                config = _stateService.GetScheduledPrefillConfig();
                var candidates = dueServices.Select(schedule => schedule.ScheduleId).ToHashSet();
                dueServices = config.GetSchedulesInRunOrder()
                    .Where(schedule => candidates.Contains(schedule.ScheduleId) && schedule.Enabled)
                    .Where(schedule =>
                    {
                        if (trigger is RunTrigger.Manual or RunTrigger.RunAll) return true;
                        lock (_ranThisProcess)
                            return ScheduledPrefillRunGates.IsServiceDue(schedule.IntervalHours,
                                _stateService.GetScheduledPrefillServiceLastRun(schedule.ScheduleId.ToString("N")),
                                DateTime.UtcNow, _ranThisProcess.Contains(schedule.ScheduleId), schedule.CustomSchedule);
                    }).ToList();
                foreach (var schedule in dueServices)
                    if (TryClaimRun(schedule, out var claimId))
                        claimedPlatforms.Add(schedule.ScheduleId, claimId);
                if (dueServices.Count == 0) return;

                operationId = tracker.RegisterOperation(
                    OperationType.ScheduledPrefill,
                    "Scheduled Prefill",
                    cts,
                    new ScheduledPrefillOperationMetadata(),
                    notice: parentNotice);
                var operationIdString = operationId.Value.ToString();
                foreach (var dueService in dueServices)
                {
                    // NotificationMode is a required field that ScheduledPrefillConfigFactory.Validate
                    // guarantees before the scheduler reads a config; a null here is a contract violation,
                    // not a case to paper over by defaulting to All.
                    var serviceState = new ScheduledPrefillServiceRunState(
                        dueService.ServiceId, dueService.ScheduleId, dueService.ScheduleName,
                        new RunNotice(dueService.NotificationMode ?? throw new InvalidOperationException(
                            $"Scheduled prefill service {dueService.ServiceId} has a null NotificationMode; "
                                + "ScheduledPrefillConfigFactory.Validate must run before the scheduler reads it."),
                            trigger,
                            parentNotice.Actor,
                            parentNotice.RestoredOrigin));
                    var serviceCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                    var serviceToken = serviceCts.Token;
                    Guid serviceOperationId;
                    try
                    {
                        serviceOperationId = tracker.RegisterOperation(
                            OperationType.ScheduledPrefill,
                            $"Scheduled Prefill - {dueService.ServiceId} - {dueService.ScheduleName}",
                            serviceCts, serviceState, notice: serviceState.Notice);
                    }
                    catch
                    {
                        serviceCts.Dispose();
                        throw;
                    }

                    serviceRuns.Add(new ScheduledPrefillServiceRun(
                        dueService, serviceOperationId, serviceOperationId.ToString(),
                        operationIdString, serviceState, serviceToken, ClaimId: claimedPlatforms.GetValueOrDefault(dueService.ScheduleId)));
                }
            }

            RaiseExecutionStateChanged();
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillStarted, new
            {
                operationId = operationId.Value.ToString(),
                runOperationId = operationId.Value.ToString(),
                serviceId = (string?)null,
                serviceCount = dueServices.Count
            });

            var tasks = new List<Task<ScheduledPrefillServiceRunResult>>(serviceRuns.Count);
            foreach (var serviceRun in serviceRuns)
            {
                dispatched.Add(serviceRun.OperationId);
                tasks.Add(RunAndStampServiceAsync(
                    serviceRun, tracker, scope.ServiceProvider, notifications, config, runToken,
                    claimedPlatforms.ContainsKey(serviceRun.ServiceConfig.ScheduleId),
                    tickAdopts.GetValueOrDefault(serviceRun.ServiceConfig.ServiceId)));
            }
            var results = await Task.WhenAll(tasks);
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

            if (runToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                StampScheduleBasis(dueServices);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            success = false;
            cancelled = true;
            error = "Scheduled prefill run cancelled";
            if (!stoppingToken.IsCancellationRequested) StampScheduleBasis(dueServices);
            _logger.LogInformation("[ScheduledPrefill] Scheduled run was cancelled");
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
            throw;
        }
        finally
        {
            try
            {
                foreach (var serviceRun in serviceRuns.Where(run => !dispatched.Contains(run.OperationId)))
                {
                    try
                    {
                        // An undispatched run that was not cancelled left through the catch above, so error is the
                        // exception's message: the server log keeps it and the card and the run row read the key.
                        if (!cancelled)
                            await ReportProgressAsync(notifications, serviceRun, "failed", error!,
                                percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                                stageKey: "signalr.scheduledPrefill.failed");
                        await CompleteServiceRunAsync(
                            serviceRun, tracker, notifications,
                            cancelled ? ScheduledPrefillServiceRunResult.Cancelled : ScheduledPrefillServiceRunResult.Failed,
                            cancelled ? error : null);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[ScheduledPrefill] Could not publish completion for {Service}", serviceRun.ServiceConfig.ServiceId);
                    }
                }

                if (operationId.HasValue)
                {
                    try
                    {
                        await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillCompleted, new
                        {
                            operationId = operationId.Value.ToString(),
                            runOperationId = operationId.Value.ToString(),
                            serviceId = (string?)null,
                            success,
                            error,
                            stageKey = errorStageKey,
                            cancelled
                        });
                    }
                    finally
                    {
                        tracker.CompleteOperation(operationId.Value, success, error, cancelled);
                    }
                }
            }
            finally
            {
                if (!operationId.HasValue) cts.Dispose();
                foreach (var claim in claimedPlatforms)
                    ReleaseRun(claim.Key, claim.Value);
            }
        }

        _logger.LogInformation("[ScheduledPrefill] Run complete");
    }

    /// <summary>
    /// Advances the SCHEDULE BASIS (and the process-ran marker behind the startup-only due-check) for
    /// every service given, so the one-minute poll does not immediately re-run a service this tick
    /// already attempted nor relaunch a batch the user just stopped. Deliberately NOT the genuine
    /// last-run: only a service that prefilled to completion stamps that.
    /// </summary>
    private void StampScheduleBasis(IReadOnlyList<ScheduledPrefillServiceConfigDto> services)
    {
        foreach (var serviceConfig in services)
        {
            lock (_ranThisProcess)
            {
                _ranThisProcess.Add(serviceConfig.ScheduleId);
            }

            var stillExists = _stateService.GetScheduledPrefillConfig()
                .GetSchedulesInRunOrder()
                .Any(schedule => schedule.ScheduleId == serviceConfig.ScheduleId);
            if (stillExists)
            {
                _stateService.SetScheduledPrefillServiceLastRun(
                    serviceConfig.ScheduleId.ToString("N"),
                    DateTime.UtcNow);
            }
        }
    }

    /// <summary>
    /// Runs one due service, records its attempt, and closes that service's own tracked operation and
    /// notification card. Neither a failure nor a stop inside one service may abort its siblings, so
    /// everything is caught here and nothing is rethrown: a rethrow makes the awaited
    /// <c>Task.WhenAll</c> throw, which discards every sibling's result along with the genuine
    /// last-run each of them had already earned. A cancel of this service's own token - its card's
    /// cancel, or the whole run's - reports <see cref="ScheduledPrefillServiceRunResult.Cancelled"/>;
    /// anything else reports <see cref="ScheduledPrefillServiceRunResult.Failed"/>.
    /// </summary>
    private async Task<ScheduledPrefillServiceRunResult> RunAndStampServiceAsync(
        ScheduledPrefillServiceRun serviceRun,
        IUnifiedOperationTracker tracker,
        IServiceProvider serviceProvider,
        ISignalRNotificationService notifications,
        ScheduledPrefillConfigDto config,
        CancellationToken runToken,
        bool admitted,
        Task<DaemonSession?>? tickAdopt)
    {
        var serviceConfig = serviceRun.ServiceConfig;
        var result = ScheduledPrefillServiceRunResult.Skipped;

        try
        {
            // This service's own card is created here rather than by the run, so a platform that
            // skips in milliseconds still gets a card saying why instead of being invisible.
            var restored = serviceRun.RestoredRun;
            var initialStage = restored?.CancelRequested == true ? "cancelling"
                : restored?.Recovering == true ? "recovering"
                : restored is not null ? "running" : "starting";
            var current = serviceRun.State.Snapshot;
            var line = DescribeStage(initialStage);
            var started = serviceRun.State.Record(current.EventSequence > 0 ? current.Stage : initialStage,
                current.EventSequence > 0 ? current.Message : line.Message,
                current.EventSequence > 0 ? current.StageKey : line.StageKey, null, stageContext: current.StageContext?.ToDictionary(pair => pair.Key, pair => pair.Value),
                bytesDownloaded: restored?.Snapshot.BytesTransferred, run: restored, started: true);
            if (started is null) return result;
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillStarted, new
            {
                operationId = serviceRun.OperationIdString,
                runOperationId = serviceRun.RunOperationId,
                serviceId = serviceConfig.ServiceId.ToString(),
                scheduleId = serviceConfig.ScheduleId,
                name = serviceConfig.ScheduleName,
                eventEpoch = started.EventEpoch,
                eventSequence = started.EventSequence,
                daemonInstanceId = started.DaemonInstanceId,
                stage = started.Stage,
                message = started.Message,
                stageKey = started.StageKey,
                stageContext = started.StageContext,
                percentComplete = started.PercentComplete,
                bytesDownloaded = started.BytesDownloaded,
                totalBytes = started.TotalBytes,
                downloadSessionId = started.DownloadSessionId,
                needsLoginReason = started.NeedsLoginReason,
                recovering = started.Recovering
            });

            if (admitted)
            {
                if (serviceRun.RestoredRun is null)
                    serviceRun.Token.ThrowIfCancellationRequested();
                result = await RunServiceAsync(
                    serviceRun, serviceProvider, notifications, config, tickAdopt);
            }
            else
            {
                await ReportProgressAsync(
                    notifications, serviceRun, "skipped",
                    "Another schedule is using this platform. This attempt was skipped and was not queued.",
                    stageKey: "signalr.scheduledPrefill.skippedOverlap");
            }
        }
        catch (OperationCanceledException) when (serviceRun.State.Detached)
        {
            result = ScheduledPrefillServiceRunResult.Cancelled;
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
            _logger.LogError(ex, "[ScheduledPrefill] Service {Service} failed; continuing", serviceConfig.ServiceId);
            // A Docker call that did not answer is reported as Docker unavailable, as the HTTP and hub
            // boundaries report it; the type derives from TimeoutException, so it is not an ApiException.
            var failure = ex switch
            {
                ApiException api => api,
                DockerTimeoutException => new ServiceUnavailableException(ex.Message)
                {
                    StageKey = "errors.prefill.dockerUnavailable"
                },
                _ => new DaemonCommandException()
            };
            await ReportProgressAsync(notifications, serviceRun, "failed", failure.Message,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                stageKey: failure.StageKey);
        }
        finally
        {
            try
            {
                // Stamp the SCHEDULE-BASIS last-run + mark process-ran for EVERY due service we attempted
                // this tick (including skips/failures), so the 1-minute poll does not immediately re-run it
                // (recurring) nor re-fire a startup-only service. A still-needs-login service then
                // retries on its next interval rather than spamming a Started/Completed cycle every
                // minute. A cancelled run is exempt here and handled once by the run itself, which is the
                // only place that can tell a user's stop (stamp, so it sticks) from app shutdown.
                if (!runToken.IsCancellationRequested && !serviceRun.State.Detached)
                {
                    StampScheduleBasis([serviceConfig]);
                }

                // Stamp the GENUINE last-run (the "Last run" the schedule view shows) ONLY when the
                // service actually ran its prefill to completion. A skip / needs-login / failure advances
                // the schedule basis above but must NOT count as a real run, so the UI keeps reading
                // "Never" until the service has truly prefilled at least once. Deliberately OUTSIDE the
                // cancellation guard: a platform that finished its prefill finished it whether or not a
                // sibling was stopped a moment later.
                if (result == ScheduledPrefillServiceRunResult.Ran)
                {
                    var stillExists = _stateService.GetScheduledPrefillConfig()
                        .GetSchedulesInRunOrder()
                        .Any(schedule => schedule.ScheduleId == serviceConfig.ScheduleId);
                    if (stillExists)
                    {
                        _stateService.SetScheduledPrefillServiceLastActualRun(
                            serviceConfig.ScheduleId.ToString("N"),
                            serviceRun.State.CompletedAtUtc ?? DateTime.UtcNow);
                    }
                }

            }
            finally
            {
                try
                {
                    if (!serviceRun.State.Detached)
                        await CompleteServiceRunAsync(serviceRun, tracker, notifications, result, null);
                }
                finally
                {
                    ReleaseRun(serviceConfig.ScheduleId, serviceRun.ClaimId);
                }
            }
        }

        return result;
    }

    private void RestoreRuns(ScheduledPrefillConfigDto config)
    {
        using var lookup = _scopeFactory.CreateScope();
        foreach (var platform in Enum.GetValues<PrefillPlatform>())
        {
            var daemon = PrefillDaemonServiceBase.ResolveDaemon(lookup.ServiceProvider, platform);
            var session = daemon?.GetActivePersistentSession();
            if (session is null) continue;
            foreach (var status in daemon!.GetRuns(session.Id).Where(run => run.ScheduleId.HasValue))
            {
                var scheduleId = status.ScheduleId!.Value;
                var run = daemon.GetRun(session.Id, status.RunId);
                if (status.CompletedAtUtc.HasValue)
                {
                    // The rule a watched run ends by: a run in which some game landed is a run, failed games or an early stop included.
                    if (status.Snapshot.State is "completed" or "failed"
                        && status.Snapshot.CompletedApps + status.Snapshot.CachedApps > 0
                        && config.GetSchedulesInRunOrder().Any(schedule => schedule.ScheduleId == scheduleId))
                        _stateService.SetScheduledPrefillServiceLastActualRun(scheduleId.ToString("N"), status.CompletedAtUtc.Value);
                    continue;
                }
                var claimId = Guid.NewGuid();
                ScheduledPrefillServiceConfigDto? savedSchedule;
                lock (_scheduleLock)
                {
                    config = _stateService.GetScheduledPrefillConfig();
                    savedSchedule = config.GetSchedulesInRunOrder().FirstOrDefault(schedule =>
                        schedule.ScheduleId == scheduleId && schedule.ServiceId == platform);
                    if (run is null || savedSchedule is null
                        || !_runningSchedules.TryAdd(scheduleId, claimId)) continue;
                }
                var scope = _scopeFactory.CreateScope();
                var tracker = scope.ServiceProvider.GetRequiredService<IUnifiedOperationTracker>();
                var notifications = scope.ServiceProvider.GetRequiredService<ISignalRNotificationService>();
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_detachedRunLifetime.Token);
                // The daemon stores the run's visibility as the string StartRunAsync persisted; turn it
                // back into a notice that draws the same way. A missing or unknown string stays silent.
                var state = new ScheduledPrefillServiceRunState(platform, scheduleId,
                    savedSchedule.ScheduleName, status.NotificationMode switch
                    {
                        "visible" => new RunNotice(
                            NotificationMode.All,
                            RunTrigger.Manual,
                            restoredOrigin: true),
                        "hidden" => new RunNotice(
                            NotificationMode.Hidden,
                            RunTrigger.Scheduled,
                            restoredOrigin: true),
                        _ => new RunNotice(
                            NotificationMode.Silent,
                            RunTrigger.Scheduled,
                            restoredOrigin: true)
                    });
                var restored = false;
                try
                {
                    restored = tracker.TryRestoreOperation(status.RunId, OperationType.ScheduledPrefill,
                        $"Scheduled Prefill - {platform} - {state.Name}", cts, state,
                        parentOperationId: status.ParentOperationId, startedAt: status.Snapshot.StartedAt.UtcDateTime,
                        notice: state.Notice);
                    if (!restored) continue;
                    var schedule = new ScheduledPrefillServiceConfigDto
                    {
                        ServiceId = platform,
                        ScheduleId = scheduleId,
                        ScheduleName = state.Name,
                        Enabled = true,
                        NotificationMode = state.Notice.Mode,
                        IntervalHours = 0,
                        Preset = ScheduledPrefillPreset.All,
                        TopCount = status.Options.TopCount,
                        SelectedAppIds = status.Options.AppIds?.ToList() ?? [],
                        OperatingSystems = [],
                        Force = status.Options.Force,
                        MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto
                        {
                            Mode = ScheduledPrefillMaxConcurrencyMode.Fixed,
                            Value = status.Options.MaxConcurrency
                        }
                    };
                    var serviceRun = new ScheduledPrefillServiceRun(schedule, status.RunId, status.RunId.ToString(),
                        (status.ParentOperationId ?? status.RunId).ToString(), state, cts.Token, run, claimId);
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _detachedRuns[status.RunId] = RunOneServiceAsync(serviceRun, config, scope, tracker, notifications, ready.Task);
                    ready.SetResult();
                }
                finally
                {
                    if (!restored)
                    {
                        cts.Dispose();
                        scope.Dispose();
                        ReleaseRun(scheduleId, claimId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Closes one service's tracked operation and its notification card, so each card reaches its own
    /// terminal when THAT service finishes rather than when the slowest sibling does. A skip (nothing
    /// to do, or put off) is completed as skipped. A run that could not start because it needs a login
    /// or something else is missing ends failed with that reason, so its card is red.
    /// </summary>
    /// <param name="failureMessage">
    /// The thrown exception's message when the service failed by throwing, otherwise null. It takes
    /// precedence over the recorded progress line for a failure, because that line says what the
    /// service was DOING when it threw rather than what went wrong: without this the card reads
    /// "failed: Prefill in progress" and the real error never reaches the operator. A throw landing
    /// before the first progress event leaves the recorded line empty, which is the same defect
    /// wearing a blanker face.
    /// </param>
    private static async Task CompleteServiceRunAsync(
        ScheduledPrefillServiceRun serviceRun,
        IUnifiedOperationTracker tracker,
        ISignalRNotificationService notifications,
        ScheduledPrefillServiceRunResult result,
        string? failureMessage)
    {
        var success = result == ScheduledPrefillServiceRunResult.Ran;
        var cancelled = result == ScheduledPrefillServiceRunResult.Cancelled;
        var skipped = result == ScheduledPrefillServiceRunResult.Skipped;

        // A skip, a needs-login and an unavailable run keep the recorded line, which IS their reason. A
        // gated failure keeps it too, because it reported itself through a progress event before returning.
        var snapshot = serviceRun.State.Record(cancelled ? "cancelled" : skipped ? "skipped"
                : success ? "completed" : "failed", failureMessage ?? string.Empty, null, null, terminal: true);
        if (snapshot is null) return;
        var error = success || cancelled ? null : snapshot.Message;

        // The .NET exception message has no key to translate it by, exactly as the run-level terminal
        // already documents, and the recorded key belongs to the progress line this is replacing.
        var stageKey = snapshot.StageKey;

        try
        {
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillCompleted, new
            {
                operationId = serviceRun.OperationIdString,
                runOperationId = serviceRun.RunOperationId,
                serviceId = serviceRun.ServiceConfig.ServiceId.ToString(),
                scheduleId = serviceRun.ServiceConfig.ScheduleId,
                name = serviceRun.ServiceConfig.ScheduleName,
                eventEpoch = snapshot.EventEpoch,
                eventSequence = snapshot.EventSequence,
                daemonInstanceId = snapshot.DaemonInstanceId,
                stage = snapshot.Stage,
                message = snapshot.Message,
                stageContext = snapshot.StageContext,
                percentComplete = snapshot.PercentComplete,
                bytesDownloaded = snapshot.BytesDownloaded,
                totalBytes = snapshot.TotalBytes,
                downloadSessionId = snapshot.DownloadSessionId,
                needsLoginReason = snapshot.NeedsLoginReason,
                recovering = snapshot.Recovering,
                success = success || skipped,
                error,
                stageKey,
                cancelled,
                // The same wire word the tracker puts on a run that did nothing, so the card closes as
                // skipped rather than reading "failed" for a missing container or a logged-out one.
                status = skipped ? "skipped" : null
            });
        }
        finally
        {
            // A platform that completed with some games failed ends amber, naming how many.
            if (success && serviceRun.State.Warning is { } warning)
            {
                tracker.SetWarning(serviceRun.OperationId, warning);
            }
            // The run row keeps this as its error, and a card drawn from the row after a reload translates it with no
            // values, so a key that reads whole stands in for the English line.
            tracker.CompleteOperation(serviceRun.OperationId, success || skipped,
                error is not null && !skipped && snapshot.StageContext is null ? snapshot.StageKey : error,
                cancelled, skipped);
        }
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
        Task<DaemonSession?>? tickAdopt)
    {
        var serviceConfig = serviceRun.ServiceConfig;
        var serviceId = serviceConfig.ServiceId;
        var ct = serviceRun.Token;

        // 1. Resolve the concrete daemon service for this platform.
        var daemon = PrefillDaemonServiceBase.ResolveDaemon(serviceProvider, serviceId);
        if (daemon is null)
        {
            await ReportProgressAsync(notifications, serviceRun, "skipped", "No daemon registered for this service", percent: ScheduledPrefillRunGates.ComputeRunPercent(1), stageKey: "signalr.scheduledPrefill.skippedNoDaemon");
            return ScheduledPrefillServiceRunResult.Unavailable;
        }

        // 2. Reuse the running persistent admin container. Scheduled prefill is admin-only and
        // downloads INSIDE the long-lived persistent container, which authenticates itself from its
        // named auth volume. It never spawns a temporary guest container and never injects a token.
        // Battle.net and Riot prefill anonymously, so neither gate below can be a login problem for
        // them. Reporting one would tell the user to sign in to something that has no sign-in, and
        // would make the run summary read "All due services need login" for a platform that has none.
        var requiresLogin = serviceId.RequiresLogin();

        // A container that outlived a startup Docker timeout has no registered session until something
        // adopts it, so the run adopts it here, as a Start would. A run of the tick whose adopt failed takes
        // that failure, so a Docker that never answers costs one wait per tick and the card reports it. A
        // session the tick adopted may have been ended since by a Stop or an image update, so any other
        // run adopts again.
        var persistentSession = daemon.GetActivePersistentSession()
            ?? await (tickAdopt is { IsFaulted: true } ? tickAdopt : daemon.AdoptRunningPersistentSessionAsync(ct));
        if (!ScheduledPrefillRunGates.TryGetRunnablePersistentSession(persistentSession, out _, out _))
            return await ReportNoContainerAsync(notifications, serviceRun);

        // TryGetRunnablePersistentSession only returns true for a non-null session.
        var session = persistentSession!;

        if (serviceRun.RestoredRun is { } restoredRun)
            return await WatchRunAsync(daemon, session, restoredRun, serviceRun, notifications, config);

        // An automatic sign-in (the startup try, a drop retry, an image update's try) holds the login lock
        // until it ends, so the run reads the session as that sign-in leaves it instead of signed out.
        bool signingInAutomatically;
        lock (session.PrefillLock)
        {
            signingInAutomatically = session.SigningInAutomatically;
        }
        if (signingInAutomatically && await session.LoginLock.WaitAsync(_signInLockWait, ct))
        {
            session.LoginLock.Release();
        }

        // Only the daemon knows why it is signed out. It reports the drop code after a dropped connection and
        // after a person's cancel of a sign-in, never after a log-off or a Log out; the sign-in below and the
        // background retry both skip a sign-in a person canceled. Reading its status here also applies a drop
        // the app missed, which starts the background retry.
        if (serviceId == PrefillPlatform.Steam)
        {
            try
            {
                var status = await daemon.GetSessionStatusAsync(session.Id, ct);
                if (status?.ErrorCode == "auth-lost"
                    && await daemon.AttemptHeadlessPersistentSelfAuthAsync(session) is null
                    && await session.LoginLock.WaitAsync(_signInLockWait, ct))
                {
                    // Another sign-in held the lock and has finished, or a person's sign-in is open; the
                    // run below reads the session as it is now.
                    session.LoginLock.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[ScheduledPrefill] Reading the daemon status or signing in again failed for {Service}", serviceId);
            }
            ct.ThrowIfCancellationRequested();
        }

        // A person's cancel leaves the container signed out even when the daemon still says logged-in.
        // Read after the status above so a later drop can clear the mark and start its one sign-in first.
        var personLeftSignedOut = requiresLogin && ScheduledPrefillRunGates.PersonCanceledSignIn(session);
        if (!personLeftSignedOut)
            return await StartRunAsync(daemon, session, serviceRun, notifications, config);

        if (!daemon.PersistentChangeGate.TryEnterChange(out var changeLease))
            return await ReportContainerBusyAsync(notifications, serviceRun);

        await using var change = changeLease!;
        if (!ReferenceEquals(daemon.GetActivePersistentSession(), session))
        {
            await change.DisposeAsync();
            return await ReportSessionEndedAsync(daemon, notifications, serviceRun);
        }

        // 2b. Confirm the persistent container is actually logged in using the daemon's LIVE status,
        // exactly like the persistent-list endpoint (status?.Status == "logged-in"). The daemon is the
        // source of truth: the in-memory AuthState is unreliable for a persistent container re-adopted
        // on a manager restart (it stays NotAuthenticated until interactive login). A null status or a
        // failing/cancelled poll is treated as needs-login rather than crashing the run.
        bool isLoggedIn;
        try
        {
            var status = await daemon.GetSessionStatusAsync(session.Id, ct);
            isLoggedIn = status?.Status == "logged-in"
                && !ScheduledPrefillRunGates.PersonCanceledSignIn(session);
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
            // A Stop, an image update or a person's Start replacing an errored session during the read ends
            // the session and fails or empties the read. The report adopts, so the change lease goes first.
            if (!daemon.IsSessionLive(session))
            {
                await change.DisposeAsync();
                return await ReportSessionEndedAsync(daemon, notifications, serviceRun);
            }

            await ReportProgressAsync(
                notifications,
                serviceRun,
                "needs-login",
                ScheduledPrefillRunGates.BuildNeedsLoginMessage(serviceId, containerRunning: true),
                ScheduledPrefillRunGates.LoggedOutNeedsLoginReason,
                percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
                stageKey: "signalr.scheduledPrefill.needsPersistentLogin");
            return ScheduledPrefillServiceRunResult.NeedsLogin;
        }

        await change.DisposeAsync();
        return await StartRunAsync(daemon, session, serviceRun, notifications, config);
    }

    /// <summary>
    /// Ends a run whose platform has no running persistent container: no container was adopted, or a Stop
    /// or an image update ended the session the run had read.
    /// </summary>
    private async Task<ScheduledPrefillServiceRunResult> ReportNoContainerAsync(
        ISignalRNotificationService notifications, ScheduledPrefillServiceRun serviceRun)
    {
        var serviceId = serviceRun.ServiceConfig.ServiceId;
        var requiresLogin = serviceId.RequiresLogin();
        ScheduledPrefillRunGates.TryGetRunnablePersistentSession(null, out _, out var needsLoginReason);
        await ReportProgressAsync(
            notifications,
            serviceRun,
            requiresLogin ? "needs-login" : "skipped",
            ScheduledPrefillRunGates.BuildNeedsLoginMessage(serviceId, containerRunning: false),
            requiresLogin ? needsLoginReason : ScheduledPrefillRunGates.NoContainerReason,
            percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
            // The card shows the needs-login reason on one branch and the message on the other,
            // so one key covers whichever sentence this event actually puts on screen.
            stageKey: requiresLogin
                ? "signalr.scheduledPrefill.needsPersistentContainer"
                : "signalr.scheduledPrefill.skippedNoContainer");
        return requiresLogin
            ? ScheduledPrefillServiceRunResult.NeedsLogin
            : ScheduledPrefillServiceRunResult.Unavailable;
    }

    /// <summary>
    /// Ends a run whose session ended after the run read it. An image update ends the session and starts the
    /// new container itself, and the adopt finishes a pending one, so only a run left with no container is
    /// told to start one; a replaced container is busy and the schedule tries again at its next interval.
    /// </summary>
    private async Task<ScheduledPrefillServiceRunResult> ReportSessionEndedAsync(
        PrefillDaemonServiceBase daemon, ISignalRNotificationService notifications, ScheduledPrefillServiceRun serviceRun)
    {
        if ((daemon.GetActivePersistentSession() ?? await daemon.AdoptRunningPersistentSessionAsync(serviceRun.Token)) is null)
            return await ReportNoContainerAsync(notifications, serviceRun);
        return await ReportContainerBusyAsync(notifications, serviceRun);
    }

    private async Task<ScheduledPrefillServiceRunResult> ReportContainerBusyAsync(
        ISignalRNotificationService notifications, ScheduledPrefillServiceRun serviceRun)
    {
        await ReportProgressAsync(
            notifications,
            serviceRun,
            "skipped",
            "The persistent container is busy",
            percent: ScheduledPrefillRunGates.ComputeRunPercent(1),
            stageKey: "signalr.scheduledPrefill.skippedContainerBusy");
        return ScheduledPrefillServiceRunResult.Skipped;
    }

    private async Task<ScheduledPrefillServiceRunResult> StartRunAsync(
        PrefillDaemonServiceBase daemon, DaemonSession session, ScheduledPrefillServiceRun serviceRun,
        ISignalRNotificationService notifications, ScheduledPrefillConfigDto config)
    {
        var schedule = serviceRun.ServiceConfig;
        var all = false;
        var recent = false;
        int? top = null;
        if (schedule.SelectedAppIds.Count == 0)
            MapPreset(schedule, out all, out recent, out top);
        try
        {
            var result = await daemon.PrefillAsync(session.Id, all: all, recent: recent, top: top,
                force: schedule.Force, operatingSystems: MapOperatingSystems(schedule.OperatingSystems),
                maxConcurrency: schedule.MaxConcurrency.Mode == ScheduledPrefillMaxConcurrencyMode.Fixed
                    ? schedule.MaxConcurrency.Value : null,
                cancellationToken: serviceRun.Token, scheduleId: schedule.ScheduleId,
                appIds: schedule.SelectedAppIds.Count > 0 ? schedule.SelectedAppIds.ToList() : null,
                operationId: serviceRun.OperationId, scheduleName: schedule.ScheduleName,
                notificationMode: serviceRun.State.Notice.HideNotification ? "hidden" : serviceRun.State.Notice.ShowNotification ? "visible" : "silent",
                parentOperationId: Guid.TryParse(serviceRun.RunOperationId, out var parent) && parent != serviceRun.OperationId
                    ? parent : null);
            if (!result.Success)
                throw new DaemonCommandException(result.ErrorCode, result.RequiresLogin);
        }
        catch (PrefillAlreadyRunningException)
        {
            await ReportProgressAsync(notifications, serviceRun, "skipped",
                "The prefill daemon has no available run slots. This attempt was not queued.",
                stageKey: "errors.prefill.runLimit");
            return ScheduledPrefillServiceRunResult.Unavailable;
        }
        catch (DaemonCommandException ex) when (ex.ErrorCode == "daemon-outdated")
        {
            await ReportProgressAsync(notifications, serviceRun, "skipped",
                "The prefill daemon image is too old for this version.",
                stageKey: "errors.prefill.daemonOutdated");
            return ScheduledPrefillServiceRunResult.Unavailable;
        }
        catch (DaemonCommandException ex) when (ex.RequiresLogin)
        {
            await ReportProgressAsync(notifications, serviceRun, "needs-login",
                ScheduledPrefillRunGates.BuildNeedsLoginMessage(schedule.ServiceId, containerRunning: true),
                ScheduledPrefillRunGates.LoggedOutNeedsLoginReason,
                stageKey: "signalr.scheduledPrefill.needsPersistentLogin");
            return ScheduledPrefillServiceRunResult.NeedsLogin;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or DaemonCommandException && !daemon.IsSessionLive(session))
        {
            // A Stop or an image update ended the session after the run read it.
            return await ReportSessionEndedAsync(daemon, notifications, serviceRun);
        }
        catch (DaemonCommandException ex) when (ex.ErrorCode == "outcome-unknown")
        {
            // The live session refused the run before adding it, while an image update or a recovery holds it.
            return await ReportContainerBusyAsync(notifications, serviceRun);
        }
        var run = daemon.GetRun(session.Id, serviceRun.OperationId);
        if (run is null)
        {
            // The same ending when the session ended after the daemon accepted the run.
            if (!daemon.IsSessionLive(session))
                return await ReportSessionEndedAsync(daemon, notifications, serviceRun);
            throw new DaemonCommandException("operation-not-found");
        }
        return await WatchRunAsync(daemon, session, run, serviceRun, notifications, config);
    }

    /// <summary>
    /// The line a run's card shows for a lifecycle stage, with the key the browser translates it by.
    /// </summary>
    private static (string Message, string StageKey) DescribeStage(string stage) => stage switch
    {
        "starting" => ("Starting scheduled prefill", "signalr.scheduledPrefill.starting"),
        "preparing" => ("Preparing prefill", "signalr.scheduledPrefill.preparing"),
        "recovering" => ("Waiting for the prefill daemon to reconnect", "signalr.scheduledPrefill.reconnecting"),
        "cancelling" => ("Stopping prefill", "signalr.scheduledPrefill.stopping"),
        _ => ("Prefill in progress", "signalr.scheduledPrefill.running")
    };

    private async Task<ScheduledPrefillServiceRunResult> WatchRunAsync(
        PrefillDaemonServiceBase daemon, DaemonSession session, DaemonRun run,
        ScheduledPrefillServiceRun serviceRun, ISignalRNotificationService notifications,
        ScheduledPrefillConfigDto config)
    {
        var relay = new ScheduledPrefillProgressRelay(this, notifications, session, serviceRun, session.Id);
        daemon.PrefillProgressUpdated += relay.OnProgressAsync;
        try
        {
            relay.Arm();
            await relay.ReplayLatestAsync();
            var deadline = run.Snapshot.StartedAt.UtcDateTime + config.MaxServiceRuntime;
            while (!run.Completion.Task.IsCompleted)
            {
                if (_detachedRunLifetime.IsCancellationRequested)
                {
                    serviceRun.State.Detached = true;
                    _detachedRunLifetime.Token.ThrowIfCancellationRequested();
                }
                if (!run.CancelRequested)
                {
                    var now = DateTime.UtcNow;
                    var expired = now >= deadline;
                    var stalled = !run.Recovering && !session.Recovering
                        && now - new DateTime(Interlocked.Read(ref run.LastProgressTicksUtc), DateTimeKind.Utc)
                            >= config.StallTimeout;
                    if (serviceRun.Token.IsCancellationRequested || expired)
                    {
                        // A cancel the person sent is theirs even when the limit passed before this pass saw it.
                        await daemon.CancelPrefillRunAsync(session.Id, run.PrefillRunId, _stopBudget,
                            expired && !serviceRun.Token.IsCancellationRequested ? "runtime-exceeded" : null);
                    }
                    else if (stalled)
                        await daemon.FailStalledSessionAsync(session, run.PrefillRunId, now,
                            config.StallTimeout, scheduleId: serviceRun.ServiceConfig.ScheduleId);
                }
                var stage = run.CancelRequested ? "cancelling" : run.Recovering || session.Recovering ? "recovering"
                    : run.Snapshot.State is "started" or "preparing" ? "preparing" : "running";
                var previous = serviceRun.State.Snapshot;
                var resumed = previous.Stage == "recovering" && stage is not ("recovering" or "cancelling");
                if (resumed) await relay.ReplayLatestAsync(resume: true);
                if (stage is "recovering" or "cancelling" || previous.Stage == "starting"
                    || resumed && serviceRun.State.Stage == "recovering")
                {
                    var line = DescribeStage(stage);
                    await ReportProgressAsync(notifications, serviceRun, stage, line.Message, stageKey: line.StageKey,
                        downloadSessionId: session.Id, run: run, session: session);
                }
                await Task.WhenAny(run.Completion.Task, Task.Delay(_stopDetectionSlice, _detachedRunLifetime.Token));
            }
        }
        finally
        {
            daemon.PrefillProgressUpdated -= relay.OnProgressAsync;
            await relay.DeactivateAndDrainAsync();
        }

        var terminal = await run.Completion.Task;
        serviceRun.State.CompletedAtUtc = terminal.CompletedAtUtc ?? terminal.Snapshot.UpdatedAt.UtcDateTime;
        var summary = terminal.Snapshot;
        // Some games failed while others downloaded or were already cached: the run completes and its
        // card names how many failed. Every game failed, or a run that stopped early, is a failed run.
        var someGamesFailed = EndedWithSomeGamesFailed(run);
        // A run the app or an outside problem stopped after some games landed worked, with errors: it
        // completes amber naming what stopped it.
        var done = summary.CompletedApps + summary.CachedApps;
        var stoppedPartway = summary.State == "failed" && !someGamesFailed && done > 0;
        var outcome = summary.State == "cancelled" ? ScheduledPrefillServiceRunResult.Cancelled
            : summary.State != "completed" && !someGamesFailed && !stoppedPartway ? ScheduledPrefillServiceRunResult.Failed
            : summary.CompletedApps + summary.CachedApps == 0 ? ScheduledPrefillServiceRunResult.Skipped
            : ScheduledPrefillServiceRunResult.Ran;
        if (someGamesFailed)
        {
            serviceRun.State.Warning = new RunWarning(
                "signalr.scheduledPrefill.failedApps",
                new Dictionary<string, object?>
                {
                    ["failed"] = summary.FailedApps,
                    ["total"] = Math.Max(summary.TotalApps, summary.FailedApps)
                });
        }
        else if (stoppedPartway)
        {
            serviceRun.State.Warning = new RunWarning(
                summary.Reason switch
                {
                    "runtime-exceeded" => "signalr.scheduledPrefill.partialMaxRuntime",
                    "stalled" => "signalr.scheduledPrefill.partialStalled",
                    "auth-lost" => "signalr.scheduledPrefill.partialSignInLost",
                    _ => "signalr.scheduledPrefill.partialStopped"
                },
                new Dictionary<string, object?>
                {
                    ["done"] = done,
                    ["total"] = Math.Max(summary.TotalApps, done)
                });
        }
        var completion = BuildCompletionMessage(summary.BytesTransferred,
            summary.CachedApps > 0 && summary.CachedApps == summary.TotalApps);
        var stageKey = outcome switch
        {
            ScheduledPrefillServiceRunResult.Cancelled => "signalr.scheduledPrefill.stopped",
            ScheduledPrefillServiceRunResult.Skipped => "signalr.scheduledPrefill.skippedOverlap",
            ScheduledPrefillServiceRunResult.Ran => completion.StageKey,
            _ => run.CancelReason == "runtime-exceeded" ? "signalr.scheduledPrefill.failedMaxRuntime"
                : run.CancelReason == "stalled" ? "signalr.scheduledPrefill.failedStalled"
                : run.ErrorStageKey!
        };
        await ReportProgressAsync(notifications, serviceRun, outcome switch
        {
            ScheduledPrefillServiceRunResult.Ran => "completed",
            ScheduledPrefillServiceRunResult.Cancelled => "cancelled",
            ScheduledPrefillServiceRunResult.Skipped => "skipped",
            _ => "failed"
        }, outcome switch
        {
            ScheduledPrefillServiceRunResult.Ran => completion.Message,
            ScheduledPrefillServiceRunResult.Cancelled => "Prefill stopped",
            ScheduledPrefillServiceRunResult.Skipped => "All selected games overlapped another prefill; no work was performed.",
            _ => run.ErrorMessage!
        }, bytesDownloaded: summary.BytesTransferred, downloadSessionId: session.Id,
            percent: 100, stageKey: stageKey, stageContext: outcome == ScheduledPrefillServiceRunResult.Ran ? completion.Context : null);
        return outcome;
    }

    // The prefill daemons end a run in which any game failed as "failed", and Battle.net adds the same
    // "download-failed" reason its crashes carry, so neither the state nor the reason tells a run that lost
    // some games from one that stopped. The games do: a run that stopped early (a crash, a stall, the time
    // limit, sign-in lost, a daemon restart) leaves games that never finished, which the daemon ends as
    // "cancelled" or as "skipped" with its reason, or which keep no result at all. A game the daemon
    // skipped on its own carries no reason.
    private static bool EndedWithSomeGamesFailed(DaemonRun run)
        => run.Snapshot is { State: "failed", FailedApps: > 0 } snapshot
            && snapshot.CompletedApps + snapshot.CachedApps > 0
            && run.Items.Count > 0
            && run.Items.Values.All(item => item.Result is "success" or "already_cached" or "failed"
                || item.Reason == "skippedOverlap"
                // A game the daemon skipped on its own (no depots for the chosen OS or language) ended; a
                // game a stopped run never reached is skipped with the run's reason or "notAttempted".
                || item.Result == "skipped" && item.Reason is null);

    /// <summary>
    /// Turns the daemon's live progress PUSH into this run's universal-notification events.
    /// Replaces the ten-second sampler that used to re-read the session's progress.
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
        private bool _replayPending;
        private int _appsCompleted;
        private int _failedApps;
        private int _totalApps;
        private double _highestPercent;
        private string? _lastEmittedMessage;
        private long _lastEmittedBytes = -1L;

        internal ScheduledPrefillProgressRelay(
            ScheduledPrefillService owner,
            ISignalRNotificationService notifications,
            DaemonSession session,
            ScheduledPrefillServiceRun serviceRun,
            string sessionId)
        {
            _owner = owner;
            _notifications = notifications;
            _session = session;
            _serviceRun = serviceRun;
            _sessionId = sessionId;
        }

        /// <summary>
        /// Games this run's daemon reported as failed, highest seen. Read by the run's terminal to
        /// decide whether a run the daemon called successful actually was.
        /// </summary>
        internal int FailedApps => _failedApps;

        /// <summary>
        /// Games this run set out to prefill, highest seen. Names the denominator in the terminal's
        /// "N of M games failed to download".
        /// </summary>
        internal int TotalApps => _totalApps;

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
        internal Task ReplayLatestAsync(bool resume = false)
        {
            if (resume) _replayPending = true;
            return _session.Runs.TryGetValue(_serviceRun.OperationId, out var run) && run.LastProgress is { } progress
                ? OnProgressAsync(_session, progress, progress.Sequence) : Task.CompletedTask;
        }

        internal async Task OnProgressAsync(DaemonSession pushedSession, PrefillProgress progress, long sequence)
        {
            if (!_armed || !_active || !ReferenceEquals(pushedSession, _session))
            {
                return;
            }

            if (!_session.Runs.TryGetValue(_serviceRun.OperationId, out var run)
                || progress.OperationId != _serviceRun.OperationIdString
                || progress.DaemonInstanceId != run.DaemonInstanceId)
                return;

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
                if (sequence < _highestSequence || sequence == _highestSequence && !_replayPending)
                    return;
                // Completion counters advance even for an OVERTAKEN tick: dropping a stale
                // app-completed payload must not lose the knowledge that the game actually finished,
                // or "game X of N" would count backwards. Downloading ticks omit these counters, which
                // is exactly why this is a running Math.Max and not a plain assignment.
                //
                // Counted BEFORE the terminal re-check below, which is about not DISPLAYING a live
                // line on a finished card. Counting is not displaying, and the run's terminal reads
                // these numbers to decide the outcome, so a tick that lands as the run ends must
                // still be counted or a failure it reported would be lost.
                _appsCompleted = Math.Max(
                    _appsCompleted,
                    progress.UpdatedApps + progress.AlreadyUpToDate + progress.FailedApps
                        + progress.SkippedApps + progress.CancelledApps);

                // Same running Math.Max, and for the same reason: the daemon finishes a run whose
                // every app failed by reporting success, so this count is the only evidence the run
                // is worth failing, and a downloading tick would otherwise reset it to zero.
                _failedApps = Math.Max(_failedApps, progress.FailedApps);
                _totalApps = Math.Max(_totalApps, progress.TotalApps);

                // Re-check inside the gate: the run's terminal path may have won the race while this
                // tick was queued. A live line must never land on a card that is already terminal.
                if (!_active || Volatile.Read(ref run.TerminalCompletedFlag) != 0)
                {
                    return;
                }

                if (sequence < _highestSequence || sequence == _highestSequence && !_replayPending)
                {
                    return;
                }

                _highestSequence = sequence;

                var totalApps = _totalApps;

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

                double? percent = null;
                string message;
                string stageKey;
                Dictionary<string, object?> stageContext;
                if (totalApps > 0)
                {
                    var fraction = ScheduledPrefillRunGates.ComputeServiceFraction(_appsCompleted, totalApps, currentAppFraction);
                    var computedPercent = ScheduledPrefillRunGates.ComputeRunPercent(fraction);

                    // The bar never goes backwards, even if an out-of-order tick reports a lower fraction.
                    computedPercent = Math.Max(computedPercent, _highestPercent);
                    _highestPercent = computedPercent;
                    percent = computedPercent;

                    var cappedCompleted = Math.Min(_appsCompleted, totalApps);
                    var downloadingCount = Math.Min(_appsCompleted + 1, totalApps);
                    message = string.IsNullOrEmpty(currentAppName)
                        ? $"Prefill in progress ({cappedCompleted} of {totalApps} games)"
                        : $"Downloading {currentAppName} ({downloadingCount} of {totalApps} games)";
                    stageKey = string.IsNullOrEmpty(currentAppName)
                        ? "signalr.scheduledPrefill.runningWithCounts"
                        : "signalr.scheduledPrefill.downloadingGame";
                    stageContext = string.IsNullOrEmpty(currentAppName)
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
                }
                else
                {
                    if (string.IsNullOrEmpty(currentAppName))
                    {
                        return;
                    }

                    message = $"Downloading {currentAppName}";
                    stageKey = "signalr.scheduledPrefill.downloadingGameUnknownTotal";
                    stageContext = new Dictionary<string, object?> { ["game"] = currentAppName };
                }

                var bytesMoved = currentAppBytes.HasValue && currentAppBytes.Value != _lastEmittedBytes;
                // Percent is derived exclusively from the completed-count/message and the current
                // downloaded bytes. Those two source values are the real change detector; the old
                // percent-delta threshold only compensated for the former ten-second sampler and is
                // redundant now that every daemon tick reaches this relay.
                var somethingMoved = message != _lastEmittedMessage || bytesMoved;
                if (!somethingMoved && !_replayPending)
                {
                    return;
                }

                // Stage = the displayed message, so a new game emits AT ONCE; same-message byte/percent
                // revisions are held to LiveEmitMinIntervalMs. Suppressing a tick costs nothing - the
                // next push carries the newer bytes anyway.
                if (!_replayPending && !_emitGate.ShouldEmit(message, Interlocked.Increment(ref _revision)))
                {
                    return;
                }

                var published = await _owner.ReportProgressAsync(
                    _notifications,
                    _serviceRun,
                    "running",
                    message,
                    downloadSessionId: _sessionId,
                    percent: percent,
                    bytesDownloaded: currentAppBytes,
                    totalBytes: currentAppTotalBytes,
                    stageKey: stageKey,
                    stageContext: stageContext,
                    clearPercent: !percent.HasValue,
                    run: run,
                    session: _session,
                    ordinaryProgress: true,
                    resume: _replayPending);
                if (published is not null)
                {
                    _lastEmittedMessage = message;
                    _lastEmittedBytes = currentAppBytes ?? -1L;
                    _replayPending = false;
                }
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
        long bytes,
        bool allCached)
    {
        if (bytes > 0)
        {
            var downloaded = FormattingUtils.FormatBytes(bytes);
            return (
                $"Prefill completed ({downloaded} downloaded)",
                "signalr.scheduledPrefill.completeWithBytes",
                new Dictionary<string, object?> { ["bytes"] = downloaded });
        }

        if (allCached)
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

    private async Task<ScheduledPrefillSnapshot?> ReportProgressAsync(
        ISignalRNotificationService notifications,
        ScheduledPrefillServiceRun serviceRun,
        string stage,
        string message,
        string? needsLoginReason = null,
        long? bytesDownloaded = null,
        string? downloadSessionId = null,
        double? percent = null,
        long? totalBytes = null,
        string? stageKey = null,
        Dictionary<string, object?>? stageContext = null,
        bool clearPercent = false,
        DaemonRun? run = null,
        DaemonSession? session = null,
        bool ordinaryProgress = false,
        bool resume = false)
    {
        var serviceId = serviceRun.ServiceConfig.ServiceId;
        // Every scheduled run cut off by a drop gets the prefill text, whichever route ends it: the app may sign the
        // always-on container in again by itself, which the interactive Steam text does not say.
        if (stageKey == "errors.steam.signInLost") stageKey = "errors.prefill.signInLost";

        // Mirror the line onto this service's own tracked operation before sending it, so a browser
        // that reloads mid-run rebuilds this service's card from the run-status endpoint instead of
        // waiting for the next tick that may be minutes away.
        var snapshot = serviceRun.State.Record(stage, message, stageKey, percent, clearPercent,
            stageContext, bytesDownloaded, totalBytes, downloadSessionId, needsLoginReason,
            run, session, ordinaryProgress, resume: resume);
        if (snapshot is null) return null;

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
        await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillProgress, new
        {
            // This SERVICE's operation id: the card it lands on is keyed by service, and its cancel
            // must reach this platform alone. The run's id rides alongside so the browser can still
            // tell one run from the next.
            operationId = serviceRun.OperationIdString,
            runOperationId = serviceRun.RunOperationId,
            serviceId = serviceId.ToString(),
            scheduleId = serviceRun.ServiceConfig.ScheduleId,
            name = serviceRun.ServiceConfig.ScheduleName,
            eventEpoch = snapshot.EventEpoch,
            eventSequence = snapshot.EventSequence,
            daemonInstanceId = snapshot.DaemonInstanceId,
            stage = snapshot.Stage,
            message = snapshot.Message,
            // Names the one sentence this event puts on the card - the skip reason, the needs-login
            // reason, or the progress line - so the browser can render it in the reader's language.
            // The English above is the server log's line; the browser shows it only when an event has
            // no key or the key's placeholders have no values.
            stageKey = snapshot.StageKey,
            stageContext = snapshot.StageContext,
            needsLoginReason = snapshot.NeedsLoginReason,
            bytesDownloaded = snapshot.BytesDownloaded,
            totalBytes = snapshot.TotalBytes,
            downloadSessionId = snapshot.DownloadSessionId,
            percentComplete = snapshot.PercentComplete,
            recovering = snapshot.Recovering
        });
        return snapshot;
    }
}
