using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the scheduled-prefill follow-ups: (1) a partial per-service failure must report the run
/// as unsuccessful in <c>ScheduledPrefillCompleted</c> (via the pure <see cref="ScheduledPrefillRunGates.EvaluateRunOutcome"/>
/// helper the orchestrator now delegates to); (2) the DI-boot smoke test proving the
/// auth-orchestrator rip-out left the container able to activate <see cref="ScheduledPrefillService"/>
/// without the deleted scheduled-prefill auth-orchestrator dependency; and (3) a benign, user-initiated
/// cancellation of a scheduled run is swallowed INSIDE <see cref="ScheduledPrefillService"/> (logged at
/// Information and completed as cancelled) so it never surfaces to the shared
/// <c>ConfigurableScheduledService</c> loop as a hard "error in scheduled work", and the recurring
/// schedule keeps ticking afterward.
/// </summary>
public class ScheduledPrefillServiceTests
{
    // ---- Criterion 5: only genuine per-service failures report success:false ----

    [Fact]
    public void EvaluateRunOutcome_ReportsSuccess_WhenServicesRanAndNoneFailed()
    {
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(
            servicesRan: 3, servicesNeedingLogin: 0, servicesSkipped: 0, servicesFailed: 0);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsSuccess_WhenAServiceRan_AndOthersOnlyNeedLogin()
    {
        // The reported bug: Steam prefilled successfully while Epic/Xbox/Battle.net/Riot were
        // skipped awaiting login, and the run claimed "One or more services failed during the run".
        // A needs-login skip is a prerequisite gap, not a failure.
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(
            servicesRan: 1, servicesNeedingLogin: 4, servicesSkipped: 0, servicesFailed: 0);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsFailure_WhenAServiceFailed_EvenIfOthersRan()
    {
        // A service threw (per-service catch), failed to start, stalled, or timed out during an
        // otherwise-progressing run — the run as a whole must not claim full success.
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(
            servicesRan: 2, servicesNeedingLogin: 0, servicesSkipped: 0, servicesFailed: 1);

        Assert.False(outcome.Success);
        Assert.Equal("One or more services failed during the run", outcome.Error);
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsAllSkipped_WhenNoServiceRan()
    {
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(
            servicesRan: 0, servicesNeedingLogin: 0, servicesSkipped: 2, servicesFailed: 0);

        Assert.False(outcome.Success);
        Assert.Equal("All enabled services were skipped", outcome.Error);
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsNeedsLogin_WhenEveryDueServiceNeededLogin()
    {
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(
            servicesRan: 0, servicesNeedingLogin: 3, servicesSkipped: 0, servicesFailed: 0);

        Assert.False(outcome.Success);
        Assert.Equal("All due services need login", outcome.Error);
    }

    // ---- Universal-notification percent: equal slice per due service, filled per game ----

    [Fact]
    public void ComputeRunPercent_StartsAtOnePercent_NotMidBar()
    {
        // First service, nothing completed yet: the bar must start at 1%, not jump to 50%.
        Assert.Equal(1d, ScheduledPrefillRunGates.ComputeRunPercent(servicesDone: 0, serviceCount: 5, currentServiceFraction: 0d));
    }

    [Fact]
    public void ComputeRunPercent_FillsServiceSlicePerCompletedGame()
    {
        // Single-service run with 4 selected games: each completed game advances the bar by 25%.
        var fraction = ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted: 1, totalApps: 4);
        Assert.Equal(25d, ScheduledPrefillRunGates.ComputeRunPercent(servicesDone: 0, serviceCount: 1, currentServiceFraction: fraction));

        fraction = ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted: 3, totalApps: 4);
        Assert.Equal(75d, ScheduledPrefillRunGates.ComputeRunPercent(servicesDone: 0, serviceCount: 1, currentServiceFraction: fraction));
    }

    [Fact]
    public void ComputeRunPercent_CapsAtNinetyNine_ReservingCompletionForTerminalEvent()
    {
        Assert.Equal(99d, ScheduledPrefillRunGates.ComputeRunPercent(servicesDone: 5, serviceCount: 5, currentServiceFraction: 1d));
    }

    [Fact]
    public void ComputeServiceFraction_UnknownTotalYieldsZero()
    {
        Assert.Equal(0d, ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted: 2, totalApps: 0));
    }

    // ---- Criteria 3/4: saving a config anchors first-run so the next poll is NOT instant ----
    // The reported bug end-to-end: SetScheduledPrefillConfig persisted the DTO but never wrote a
    // per-service last-run, so the next 1-minute poll saw null and ran the service immediately. This
    // exercises the REAL StateService save path over a throwaway temp state dir and asserts the anchor
    // is now written for the enabled service, that disabled services stay un-anchored, that the anchored
    // service is not due on the next poll, and that its next-run is ~now+interval instead of "soon".
    // Fails before the fix (GetScheduledPrefillServiceLastRun returns null -> Assert.NotNull throws).

    [Fact]
    public void SetScheduledPrefillConfig_AnchorsFirstRunForEnabledService_NotDueOnNextPoll()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;

        var config = BuildConfig(steamEnabled: true, steamIntervalHours: 48d);

        var beforeSave = DateTime.UtcNow;
        stateService.SetScheduledPrefillConfig(config);
        var afterSave = DateTime.UtcNow;

        // Steam (enabled, 48h) is anchored to save-time...
        var steamLastRun = stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Steam.ToString());
        Assert.NotNull(steamLastRun);
        Assert.InRange(steamLastRun!.Value, beforeSave, afterSave);

        // ...while the disabled services are left un-anchored (null), so nothing runs for them either.
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Epic.ToString()));
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.BattleNet.ToString()));

        // The next poll must NOT treat Steam as due (the bug: it used to be due immediately at null).
        Assert.False(ScheduledPrefillRunGates.IsServiceDue(
            48d, steamLastRun, DateTime.UtcNow, hasRunThisProcess: false));

        // And the schedule view shows a concrete next-run one interval out (not null / "soon").
        var nextRun = ScheduledPrefillRunGates.ComputeNextRunUtc(48d, steamLastRun);
        Assert.NotNull(nextRun);
        Assert.Equal(steamLastRun!.Value.AddHours(48d), nextRun!.Value);
    }

    // ---- Fix 2: load-path anchor seeds a MISSING key but never clobbers an EXISTING one ----
    // The restart-no-shift invariant (the #1 regression risk): on a normal restart the last-run map is
    // persisted and reloaded, so an enabled service already has a key and must keep its genuine last-run
    // (re-anchoring it would push its schedule out one interval every restart). A service enabled but
    // never anchored (e.g. persisted by a pre-anchor build) has no key and must be seeded to ~now so it
    // waits one interval instead of instant-running on the next poll.

    [Fact]
    public void GetState_OnLoad_SeedsMissingAnchor_ButPreservesExistingLastRun()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;

        // Steam enabled (48h) WITH a genuine past last-run (key present); BattleNet enabled (24h) with NO
        // last-run key yet. Persist it, then drop the in-memory cache to force a real load from disk.
        var steamPastRun = DateTime.UtcNow.AddHours(-10d);
        var persistedState = new AppState
        {
            ScheduledPrefill = BuildConfig(
                steamEnabled: true, steamIntervalHours: 48d,
                battleNetEnabled: true, battleNetIntervalHours: 24d),
            ScheduledPrefillServiceLastRunUtc = new Dictionary<string, DateTime>
            {
                [PrefillPlatform.Steam.ToString()] = steamPastRun
            }
        };

        SetCachedState(stateService, persistedState);
        stateService.SaveState(persistedState);
        SetCachedState(stateService, null);

        var beforeLoad = DateTime.UtcNow;
        stateService.GetState(); // triggers FromPersisted + the in-memory normalize/seed on load
        var afterLoad = DateTime.UtcNow;

        // Steam already had a key -> a restart must NOT re-anchor it (restart-no-shift invariant).
        var steamLastRun = stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Steam.ToString());
        Assert.NotNull(steamLastRun);
        Assert.True(
            Math.Abs((steamLastRun!.Value - steamPastRun).TotalSeconds) < 1d,
            "Steam's persisted last-run must be preserved across a restart, not reseeded to now.");

        // BattleNet had no key -> load seeds it to ~now so it waits one interval instead of instant-running.
        var battleNetLastRun = stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.BattleNet.ToString());
        Assert.NotNull(battleNetLastRun);
        Assert.InRange(battleNetLastRun!.Value, beforeLoad.AddSeconds(-1), afterLoad.AddSeconds(1));

        // Disabled services stay un-anchored.
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Epic.ToString()));
    }

    // ---- Fix 2: reset (clear) reseeds the still-enabled services so the next poll is not due ----
    // ResetToDefaults clears the per-service last-run map; a bare clear would make every enabled service
    // look never-run and instant-run on the next poll. The clear now reseeds enabled positive-interval
    // services to ~now. Fails before the fix (Steam last-run is null after clear); passes after.

    [Fact]
    public void ClearScheduledPrefillServiceLastRun_ReseedsEnabledServices_NotDueOnNextPoll()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;

        stateService.SetScheduledPrefillConfig(BuildConfig(steamEnabled: true, steamIntervalHours: 48d));
        Assert.NotNull(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Steam.ToString()));

        var beforeReset = DateTime.UtcNow;
        stateService.ClearScheduledPrefillServiceLastRun();
        var afterReset = DateTime.UtcNow;

        var steamLastRun = stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Steam.ToString());
        Assert.NotNull(steamLastRun);
        Assert.InRange(steamLastRun!.Value, beforeReset.AddSeconds(-1), afterReset.AddSeconds(1));
        Assert.False(ScheduledPrefillRunGates.IsServiceDue(
            48d, steamLastRun, DateTime.UtcNow, hasRunThisProcess: false));

        // Disabled services are not reseeded.
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Epic.ToString()));
    }

    // ---- BUG FIX: a service that has only been ANCHORED (enabled/saved) but never genuinely run must
    // show "Never" for Last run, not the anchor time. The schedule basis and the genuine-run map diverge
    // until the first real run: enabling stamps the basis (so Next run is one interval out) but leaves the
    // actual-run null (so Last run reads "Never"). Reported: every just-enabled service showed a fake
    // "Last run: Nm ago" (the anchor time) even though it had never actually run. ----

    [Fact]
    public void SetScheduledPrefillConfig_AnchorsScheduleBasis_ButLeavesActualRunNull()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;

        stateService.SetScheduledPrefillConfig(BuildConfig(steamEnabled: true, steamIntervalHours: 48d));

        // Enabling anchors the schedule basis so the next poll is not instant...
        Assert.NotNull(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Steam.ToString()));
        // ...but the GENUINE last-run stays null until the service actually runs, so the schedule view
        // reads "Never" instead of the anchor time.
        Assert.Null(stateService.GetScheduledPrefillServiceLastActualRun(PrefillPlatform.Steam.ToString()));
    }

    [Fact]
    public void ScheduledPrefillServiceLastActualRun_PersistsAcrossReload_AndIsClearedByReset()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;

        stateService.SetScheduledPrefillConfig(BuildConfig(steamEnabled: true, steamIntervalHours: 48d));

        var ranAt = DateTime.UtcNow;
        stateService.SetScheduledPrefillServiceLastActualRun(PrefillPlatform.Steam.ToString(), ranAt);

        // Durable across restart: persist to disk, drop the in-memory cache, and reload.
        stateService.SaveState(stateService.GetState());
        SetCachedState(stateService, null);
        var reloaded = stateService.GetScheduledPrefillServiceLastActualRun(PrefillPlatform.Steam.ToString());
        Assert.NotNull(reloaded);
        Assert.True(
            Math.Abs((reloaded!.Value - ranAt).TotalSeconds) < 1d,
            "The genuine last-run must round-trip through save/load.");

        // Reset wipes the genuine-run history (nothing has run post-reset -> "Never") while the schedule
        // basis is reseeded so the next poll is still not instant.
        stateService.ClearScheduledPrefillServiceLastRun();
        Assert.Null(stateService.GetScheduledPrefillServiceLastActualRun(PrefillPlatform.Steam.ToString()));
        Assert.NotNull(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Steam.ToString()));
    }

    // Overwrites StateService's private _cachedState so a test can stage a persisted state (then null the
    // cache to force a real disk load). Mirrors the reflection the context uses to seed an empty state.
    private static void SetCachedState(StateService stateService, AppState? state)
    {
        var field = typeof(StateService).GetField(
            "_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(stateService, state);
    }

    // Starts from a known-valid default so every non-Steam service stays valid through
    // ScheduledPrefillConfigFactory.Validate, then flips Steam to (enabled, interval) and disables the
    // rest so the anchor under test is isolated to a single service.
    private static ScheduledPrefillConfigDto BuildConfig(
        bool steamEnabled,
        double steamIntervalHours,
        bool battleNetEnabled = false,
        double battleNetIntervalHours = ScheduledPrefillConfigFactory.DefaultIntervalHours)
    {
        var template = ScheduledPrefillConfigFactory.CreateDefault();
        return new ScheduledPrefillConfigDto
        {
            Version = template.Version,
            MaxServiceRuntime = template.MaxServiceRuntime,
            StallTimeout = template.StallTimeout,
            Steam = Reconfigure(template.Steam, steamEnabled, steamIntervalHours),
            Epic = Reconfigure(template.Epic, enabled: false, template.Epic.IntervalHours),
            Xbox = Reconfigure(template.Xbox, enabled: false, template.Xbox.IntervalHours),
            BattleNet = Reconfigure(template.BattleNet, battleNetEnabled, battleNetIntervalHours),
            Riot = Reconfigure(template.Riot, enabled: false, template.Riot.IntervalHours)
        };
    }

    private static ScheduledPrefillServiceConfigDto Reconfigure(
        ScheduledPrefillServiceConfigDto template, bool enabled, double intervalHours)
        => new ScheduledPrefillServiceConfigDto
        {
            ServiceId = template.ServiceId,
            Enabled = enabled,
            IntervalHours = intervalHours,
            Preset = template.Preset,
            TopCount = template.TopCount,
            SelectedAppIds = template.SelectedAppIds,
            OperatingSystems = template.OperatingSystems,
            Force = template.Force,
            MaxConcurrency = template.MaxConcurrency
        };

    // Builds a REAL StateService rooted at a throwaway temp directory. The encryption / steam-auth deps
    // are constructed for real but never exercised (no SteamAuth is set, so Encrypt short-circuits and
    // no key material is needed); the in-memory state starts empty so the save path runs only the
    // anchor logic and never touches the disk-migration / steam-auth machinery.
    private sealed class TempStateServiceContext : IDisposable
    {
        private readonly string _root;

        public StateService StateService { get; }

        public TempStateServiceContext()
        {
            _root = Path.Combine(Path.GetTempPath(), "lcm-scheduled-prefill-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            var pathResolver = new TempDirPathResolver(_root);
            var configuration = new ConfigurationBuilder().Build();
            var apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver);
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
            var encryption = new SecureStateEncryptionService(
                dataProtection, apiKeyService, NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption);

            StateService = new StateService(
                NullLogger<StateService>.Instance, pathResolver, encryption, steamAuthStorage);

            // Seed an empty in-memory state so GetState() short-circuits on the cache and the save path
            // never runs the legacy-file migration - this isolates the test to the anchor behaviour.
            var cachedStateField = typeof(StateService).GetField(
                "_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            cachedStateField.SetValue(StateService, new AppState());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the throwaway temp dir; a locked file must not fail the test.
            }
        }
    }

    private sealed class TempDirPathResolver : PathResolverBase
    {
        private readonly string _basePath;

        public TempDirPathResolver(string basePath) : base(NullLogger.Instance)
        {
            _basePath = basePath;
        }

        protected override string BasePath => _basePath;
        protected override string RustExecutableExtension => string.Empty;

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }

    // ---- Criterion 3: DI-boot smoke test for the auth-orchestrator rip-out ----
    // After removing the dead auth-orchestrator dependency from the ScheduledPrefillService
    // constructor and from Program.cs DI, the container must still build and the hosted service must
    // activate WITHOUT that (now deleted) dependency. ValidateOnBuild proves the constructor's
    // call-site graph resolves with no missing dependency (a missing one throws here); the explicit
    // resolve then runs the real constructor. A plain unit test would not catch a DI-startup crash.

    [Fact]
    public void ServiceProvider_BuildsAndActivatesScheduledPrefillService_WithoutAuthService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStateService>(CreateNullStateService());
        services.AddSingleton<ScheduledPrefillService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var resolved = provider.GetRequiredService<ScheduledPrefillService>();

        Assert.NotNull(resolved);
    }

    // ---- Benign cancellation is handled in ScheduledPrefillService, not the shared base loop ----
    // Regression guard: a user cancel of a running scheduled prefill must be swallowed inside
    // ExecuteWorkAsync (Information log + operation completed as cancelled) and must NOT propagate as an
    // OperationCanceledException to ConfigurableScheduledService.ExecuteAsync, whose generic catch would
    // mis-log the benign cancel as a hard "error in scheduled work" and where an over-broad OCE catch
    // would instead silently swallow genuinely-unrelated internal timeouts.

    [Fact]
    public async Task ExecuteWorkAsync_BenignCancellation_SwallowsException_AndCompletesOperationAsCancelled()
    {
        var harness = CreateCancellingHarness();
        using var provider = harness.Provider;

        var executeWork = typeof(ScheduledPrefillService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // The base scheduling loop calls ExecuteWorkAsync once per tick, so invoking it twice in a row
        // and getting a normal return each time is the method-level guarantee that a cancel (surfaced
        // here by the fake tracker cancelling the adopted CTS) is handled locally and never escapes to
        // the shared loop, which would otherwise log it as "error in scheduled work".
        var thrown = await Record.ExceptionAsync(async () =>
        {
            await (Task)executeWork.Invoke(harness.Service, new object[] { CancellationToken.None })!;
            await (Task)executeWork.Invoke(harness.Service, new object[] { CancellationToken.None })!;
        });

        harness.Service.Dispose();

        Assert.Null(thrown);
        Assert.Equal(2, harness.Tracker.RegisterCount);
        // Cleanup preserved: each run still completes its tracked operation, marked as cancelled.
        Assert.Equal(2, harness.Tracker.CompleteCount);
        Assert.False(harness.Tracker.LastCompleteSuccess);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("cancelled"));
    }

    [Fact]
    public async Task SchedulingLoop_ContinuesTicking_AfterBenignCancellation()
    {
        var harness = CreateCancellingHarness();
        using var provider = harness.Provider;
        var service = harness.Service;

        await service.StartAsync(CancellationToken.None);
        try
        {
            // DefaultRunOnStartup is false, so the loop skips its first iteration and then sleeps on the
            // 1-minute poll cadence. Nudge it (TriggerImmediateRun also flags a bypass so every enabled
            // service is due) until it has executed work at least twice — proving a benign cancel on one
            // tick does not tear down the recurring schedule.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (harness.Tracker.RegisterCount < 2 && DateTime.UtcNow < deadline)
            {
                service.TriggerImmediateRun();
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        Assert.True(
            harness.Tracker.RegisterCount >= 2,
            $"Scheduling loop should keep running work after a benign cancel; it ran {harness.Tracker.RegisterCount} time(s).");
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("error in scheduled work"));
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("cancelled"));
    }

    private static CancellingHarness CreateCancellingHarness()
    {
        var trackerProxy = DispatchProxy.Create<IUnifiedOperationTracker, CancellingTrackerProxy>();
        var tracker = (CancellingTrackerProxy)trackerProxy;
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, PrefillConfigStateServiceProxy>();

        var services = new ServiceCollection();
        services.AddSingleton((IUnifiedOperationTracker)trackerProxy);
        services.AddSingleton(notifications);
        var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger();
        var service = new ScheduledPrefillService(
            logger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            stateService);

        return new CancellingHarness(service, logger, tracker, provider);
    }

    private sealed record CancellingHarness(
        ScheduledPrefillService Service,
        CapturingLogger Logger,
        CancellingTrackerProxy Tracker,
        ServiceProvider Provider);

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<ScheduledPrefillService>
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_sync) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }

    // Reproduces the user/tracker cancel path (OperationsController -> tracker.CancelOperation ->
    // cts.Cancel): cancelling the adopted CTS the instant the run registers makes runToken fire, which
    // ScheduledPrefillService.ExecuteWorkAsync must treat as a benign, already-handled cancellation.
    // Not sealed: DispatchProxy.Create derives the concrete proxy type from this class.
    private class CancellingTrackerProxy : DispatchProxy
    {
        private int _registerCount;
        private int _completeCount;

        public int RegisterCount => Volatile.Read(ref _registerCount);
        public int CompleteCount => Volatile.Read(ref _completeCount);
        public bool? LastCompleteSuccess { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                    Interlocked.Increment(ref _registerCount);
                    // args[2] is the CancellationTokenSource the run hands over (RegisterOperation's
                    // third parameter). Cancelling it here stands in for the user pressing Cancel.
                    (args?[2] as CancellationTokenSource)?.Cancel();
                    return Guid.NewGuid();
                case nameof(IUnifiedOperationTracker.CompleteOperation):
                    Interlocked.Increment(ref _completeCount);
                    // args[1] is the success flag; a benign cancel must complete with success:false.
                    LastCompleteSuccess = args?[1] as bool?;
                    return null;
                default:
                    return null;
            }
        }
    }

    // IStateService stub whose GetScheduledPrefillConfig returns a real default config (BattleNet + Riot
    // enabled), so ExecuteWorkAsync finds due services and reaches the operation-register/cancel path.
    // Every other member returns its type default (mirrors NullReturningProxy).
    private class PrefillConfigStateServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return ScheduledPrefillConfigFactory.CreateDefault();
            }

            var returnType = targetMethod?.ReturnType;

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType is not null && returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }

    private static IStateService CreateNullStateService()
        => (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();

    /// <summary>
    /// Minimal <see cref="IStateService"/> stub. The <see cref="ScheduledPrefillService"/> constructor
    /// only reads <c>GetServiceInterval</c> / <c>GetServiceRunOnStartup</c> (both nullable) via
    /// <c>LoadStateOverrides</c>; returning null is the "no saved override" path. Every other member
    /// returns its type default — none are exercised during construction.
    /// </summary>
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Target method was null.");
            }

            var returnType = targetMethod.ReturnType;

            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            // Non-nullable value types need a concrete default; reference types and Nullable<T>
            // (e.g. double? / bool?) resolve to null.
            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}
