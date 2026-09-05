using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
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
    // ---- Only genuine per-service failures report success:false ----

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

    // ---- Universal-notification percent: tracks the active service, per game + per byte ----

    [Fact]
    public void ComputeRunPercent_StartsAtOnePercent_NotMidBar()
    {
        // Nothing completed yet: the bar must start at 1%, not jump to 50%.
        Assert.Equal(1d, ScheduledPrefillRunGates.ComputeRunPercent(serviceFraction: 0d));
    }

    [Fact]
    public void ComputeRunPercent_AdvancesPerCompletedGame()
    {
        // 4 selected games: each completed game advances the bar by 25%.
        var fraction = ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted: 1, totalApps: 4);
        Assert.Equal(25d, ScheduledPrefillRunGates.ComputeRunPercent(fraction));

        fraction = ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted: 3, totalApps: 4);
        Assert.Equal(75d, ScheduledPrefillRunGates.ComputeRunPercent(fraction));
    }

    [Fact]
    public void ComputeServiceFraction_IncludesCurrentGameByteProgress()
    {
        // 1 of 4 games done, second game half-downloaded: the bar must keep moving DURING the
        // download (the reported bug: an 87 GB game left the card frozen at 1% for hours).
        var fraction = ScheduledPrefillRunGates.ComputeServiceFraction(
            appsCompleted: 1, totalApps: 4, currentAppFraction: 0.5d);
        Assert.Equal(37.5d, ScheduledPrefillRunGates.ComputeRunPercent(fraction));
    }

    [Fact]
    public void ComputeRunPercent_CapsAtNinetyNine_ReservingCompletionForTerminalEvent()
    {
        Assert.Equal(99d, ScheduledPrefillRunGates.ComputeRunPercent(serviceFraction: 1d));
    }

    [Fact]
    public void ComputeServiceFraction_UnknownTotalYieldsZero()
    {
        Assert.Equal(0d, ScheduledPrefillRunGates.ComputeServiceFraction(appsCompleted: 2, totalApps: 0));
    }

    // ---- Saving a config anchors first-run so the next poll is NOT instant ----
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
        var steamLastRun = stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N"));
        Assert.NotNull(steamLastRun);
        Assert.InRange(steamLastRun!.Value, beforeSave, afterSave);

        // ...while the disabled services are left un-anchored (null), so nothing runs for them either.
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Epic).ToString("N")));
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet).ToString("N")));

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
                [ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N")] = steamPastRun
            }
        };

        SetCachedState(stateService, persistedState);
        stateService.SaveState(persistedState);
        SetCachedState(stateService, null);

        var beforeLoad = DateTime.UtcNow;
        stateService.GetState(); // triggers FromPersisted + the in-memory normalize/seed on load
        var afterLoad = DateTime.UtcNow;

        // Steam already had a key -> a restart must NOT re-anchor it (restart-no-shift invariant).
        var steamLastRun = stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N"));
        Assert.NotNull(steamLastRun);
        Assert.True(
            Math.Abs((steamLastRun!.Value - steamPastRun).TotalSeconds) < 1d,
            "Steam's persisted last-run must be preserved across a restart, not reseeded to now.");

        // BattleNet had no key -> load seeds it to ~now so it waits one interval instead of instant-running.
        var battleNetLastRun = stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet).ToString("N"));
        Assert.NotNull(battleNetLastRun);
        Assert.InRange(battleNetLastRun!.Value, beforeLoad.AddSeconds(-1), afterLoad.AddSeconds(1));

        // Disabled services stay un-anchored.
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Epic).ToString("N")));
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
        Assert.NotNull(stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N")));

        var beforeReset = DateTime.UtcNow;
        stateService.ClearScheduledPrefillServiceLastRun();
        var afterReset = DateTime.UtcNow;

        var steamLastRun = stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N"));
        Assert.NotNull(steamLastRun);
        Assert.InRange(steamLastRun!.Value, beforeReset.AddSeconds(-1), afterReset.AddSeconds(1));
        Assert.False(ScheduledPrefillRunGates.IsServiceDue(
            48d, steamLastRun, DateTime.UtcNow, hasRunThisProcess: false));

        // Disabled services are not reseeded.
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Epic).ToString("N")));
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
        Assert.NotNull(stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N")));
        // ...but the GENUINE last-run stays null until the service actually runs, so the schedule view
        // reads "Never" instead of the anchor time.
        Assert.Null(stateService.GetScheduledPrefillServiceLastActualRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N")));
    }

    [Fact]
    public void ScheduledPrefillServiceLastActualRun_PersistsAcrossReload_AndIsClearedByReset()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;

        stateService.SetScheduledPrefillConfig(BuildConfig(steamEnabled: true, steamIntervalHours: 48d));

        var ranAt = DateTime.UtcNow;
        stateService.SetScheduledPrefillServiceLastActualRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N"), ranAt);

        // Durable across restart: persist to disk, drop the in-memory cache, and reload.
        stateService.SaveState(stateService.GetState());
        SetCachedState(stateService, null);
        var reloaded = stateService.GetScheduledPrefillServiceLastActualRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N"));
        Assert.NotNull(reloaded);
        Assert.True(
            Math.Abs((reloaded!.Value - ranAt).TotalSeconds) < 1d,
            "The genuine last-run must round-trip through save/load.");

        // Reset wipes the genuine-run history (nothing has run post-reset -> "Never") while the schedule
        // basis is reseeded so the next poll is still not instant.
        stateService.ClearScheduledPrefillServiceLastRun();
        Assert.Null(stateService.GetScheduledPrefillServiceLastActualRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N")));
        Assert.NotNull(stateService.GetScheduledPrefillServiceLastRun(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam).ToString("N")));
    }

    [Fact]
    public void SetScheduledPrefillConfig_DeletingARecordPurgesItsTimingAndRecreationStartsClean()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;
        var config = BuildConfig(steamEnabled: true, steamIntervalHours: 48d);
        var recordId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        config.Steam.Schedules.Add(new ScheduledPrefillSchedule
        {
            Id = recordId,
            Name = "Weekly selected",
            Enabled = false,
            IntervalHours = 168d,
            Preset = ScheduledPrefillPreset.All,
            SelectedAppIds = ["10", "20"],
            OperatingSystems = [ScheduledPrefillOperatingSystem.Windows],
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto
            {
                Mode = ScheduledPrefillMaxConcurrencyMode.Auto
            }
        });
        stateService.SetScheduledPrefillConfig(config);

        var key = recordId.ToString("N");
        stateService.SetScheduledPrefillServiceLastRun(key, DateTime.UtcNow.AddDays(-7));
        stateService.SetScheduledPrefillServiceLastActualRun(key, DateTime.UtcNow.AddDays(-7));

        config.Steam.Schedules.RemoveAll(schedule => schedule.Id == recordId);
        stateService.SetScheduledPrefillConfig(config);
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(key));
        Assert.Null(stateService.GetScheduledPrefillServiceLastActualRun(key));

        config.Steam.Schedules.Add(new ScheduledPrefillSchedule
        {
            Id = recordId,
            Name = "Weekly selected",
            Enabled = false,
            IntervalHours = 168d,
            Preset = ScheduledPrefillPreset.All,
            SelectedAppIds = ["10", "20"],
            OperatingSystems = [ScheduledPrefillOperatingSystem.Windows],
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto
            {
                Mode = ScheduledPrefillMaxConcurrencyMode.Auto
            }
        });
        stateService.SetScheduledPrefillConfig(config);
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(key));
        Assert.Null(stateService.GetScheduledPrefillServiceLastActualRun(key));
    }

    [Fact]
    public void GetState_MigratesLegacyPlatformTimingKeysToStableChildIds()
    {
        using var context = new TempStateServiceContext();
        var stateService = context.StateService;
        var current = BuildConfig(steamEnabled: true, steamIntervalHours: 48d);
        var legacy = new ScheduledPrefillConfigDto
        {
            Version = 5,
            MaxServiceRuntime = current.MaxServiceRuntime,
            StallTimeout = current.StallTimeout,
            PersistenceMode = current.PersistenceMode,
            Steam = new ScheduledPrefillServiceConfigDto
            {
                ServiceId = PrefillPlatform.Steam,
                Enabled = true,
                NotificationMode = NotificationMode.All,
                IntervalHours = 48d,
                Preset = ScheduledPrefillPreset.Recent,
                OperatingSystems = [ScheduledPrefillOperatingSystem.Windows],
                MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto
                {
                    Mode = ScheduledPrefillMaxConcurrencyMode.Auto
                }
            },
            Epic = current.Epic,
            Xbox = current.Xbox,
            BattleNet = current.BattleNet,
            Riot = current.Riot
        };
        var priorRun = DateTime.UtcNow.AddDays(-2);
        var persisted = new AppState
        {
            ScheduledPrefill = legacy,
            ScheduledPrefillServiceLastRunUtc = new Dictionary<string, DateTime>
            {
                [PrefillPlatform.Steam.ToString()] = priorRun
            },
            ScheduledPrefillServiceLastActualRunUtc = new Dictionary<string, DateTime>
            {
                [PrefillPlatform.Steam.ToString()] = priorRun
            }
        };
        SetCachedState(stateService, persisted);
        stateService.SaveState(persisted);
        SetCachedState(stateService, null);

        stateService.GetState();
        var childKey = ScheduledPrefillConfigFactory
            .GetDefaultScheduleId(PrefillPlatform.Steam)
            .ToString("N");
        Assert.Equal(priorRun, stateService.GetScheduledPrefillServiceLastRun(childKey));
        Assert.Equal(priorRun, stateService.GetScheduledPrefillServiceLastActualRun(childKey));
        Assert.Null(stateService.GetScheduledPrefillServiceLastRun(PrefillPlatform.Steam.ToString()));
        Assert.Null(stateService.GetScheduledPrefillServiceLastActualRun(PrefillPlatform.Steam.ToString()));
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
            PersistenceMode = template.PersistenceMode,
            Steam = Reconfigure(template.Steam, steamEnabled, steamIntervalHours),
            Epic = Reconfigure(template.Epic, enabled: false, template.Epic.IntervalHours),
            Xbox = Reconfigure(template.Xbox, enabled: false, template.Xbox.IntervalHours),
            BattleNet = Reconfigure(template.BattleNet, battleNetEnabled, battleNetIntervalHours),
            Riot = Reconfigure(template.Riot, enabled: false, template.Riot.IntervalHours)
        };
    }

    private static ScheduledPrefillServiceConfigDto Reconfigure(
        ScheduledPrefillServiceConfigDto template, bool enabled, double intervalHours)
    {
        var schedules = template.Schedules.Count == 0
            ? []
            : template.Schedules.Select(schedule => new ScheduledPrefillSchedule
            {
                Id = schedule.Id,
                Name = schedule.Name,
                Enabled = enabled,
                IntervalHours = intervalHours,
                CustomSchedule = schedule.CustomSchedule,
                Preset = schedule.Preset,
                TopCount = schedule.TopCount,
                SelectedAppIds = [.. schedule.SelectedAppIds],
                OperatingSystems = [.. schedule.OperatingSystems],
                Force = schedule.Force,
                MaxConcurrency = schedule.MaxConcurrency,
                NotificationMode = schedule.NotificationMode,
                NotificationDisplayMode = schedule.NotificationDisplayMode
            }).ToList();

        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = template.ServiceId,
            Schedules = schedules,
            Enabled = enabled,
            NotificationMode = template.NotificationMode,
            NotificationDisplayMode = template.NotificationDisplayMode,
            IntervalHours = intervalHours,
            Preset = template.Preset,
            TopCount = template.TopCount,
            SelectedAppIds = template.SelectedAppIds,
            OperatingSystems = template.OperatingSystems,
            Force = template.Force,
            MaxConcurrency = template.MaxConcurrency,
            PersistenceMode = template.PersistenceMode,
            CustomSchedule = template.CustomSchedule
        };
    }

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

    // ---- DI-boot smoke test for the auth-orchestrator rip-out ----
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
        Assert.Equal(2, harness.Tracker.RunRegisterCount);
        // A run also registers one operation per due platform (the default config enables Battle.net
        // and Riot), so every operation it opened must be closed - the per-platform ones included, or
        // their adopted token sources are never disposed.
        Assert.Equal(harness.Tracker.RegisterCount, harness.Tracker.CompleteCount);
        Assert.True(harness.Tracker.RegisterCount > harness.Tracker.RunRegisterCount);
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
            while (harness.Tracker.RunRegisterCount < 2 && DateTime.UtcNow < deadline)
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
            harness.Tracker.RunRegisterCount >= 2,
            $"Scheduling loop should keep running work after a benign cancel; it ran {harness.Tracker.RunRegisterCount} time(s).");
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("error in scheduled work"));
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("cancelled"));
    }

    // ---- A per-row Run runs the platform it names and nothing else ----
    // The Schedules page's per-service Run button posts to a per-platform route, and the shared
    // TriggerImmediateRun it goes through takes no arguments, so the platform has to survive the trip
    // some other way. These drive the real ExecuteWorkAsync and read back which platforms it opened a
    // tracked operation for.

    [Fact]
    public async Task ARunThatThrowsBeforeItsServicesStart_StillLetsThemRunAgain()
    {
        var harness = CreateRecordingHarness<PrefillConfigStateServiceProxy>();
        using var provider = harness.Provider;

        // Platforms are claimed before the fan-out so the loop and an on-demand run cannot both take
        // one. Anything between the claim and the fan-out can throw, including the run-level Started
        // broadcast, and a claim left behind is permanent: the loop skips that platform on every
        // later tick and the service silently stops running until the process restarts. The tick
        // below is driven twice, and the second one has to still see its services. [54]
        harness.Notifications.FailNextStarted = true;

        var thrown = await Record.ExceptionAsync(() => InvokeExecuteWorkAsync(harness.Service));
        Assert.NotNull(thrown);

        await InvokeExecuteWorkAsync(harness.Service);
        harness.Service.Dispose();

        // Counted rather than merely present: the failed tick registers its own run-level operation
        // before it throws, so asking whether one exists cannot tell the two ticks apart. A second
        // one only exists if the second tick found services to run, which is exactly what a leaked
        // claim prevents.
        Assert.Equal(2, harness.Tracker.RegisteredNames.Count(name => name == "Scheduled Prefill"));
    }

    [Fact]
    public async Task TriggerServiceRun_RunsTheNamedPlatformOnItsOwn_WithoutWaitingForATick()
    {
        var harness = CreateRecordingHarness<EnabledServicesJustRanStateServiceProxy>();
        using var provider = harness.Provider;

        // Both enabled services ran a moment ago, so neither is due and no tick is invoked here at all.
        // Naming Riot must still run Riot, on the strength of the request and on its own task: the loop
        // awaits one tick at a time, so a request that had to wait for a tick would wait behind whatever
        // download that tick was holding. It must also not sweep in Battle.net, which is not due. [49]
        Assert.NotNull(harness.Service.TriggerServiceRun(
            PrefillPlatform.Riot,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Riot)));
        await WaitForRegistrationAsync(harness.Tracker, "Scheduled Prefill - Riot - Default");
        harness.Service.Dispose();

        Assert.DoesNotContain("Scheduled Prefill - BattleNet - Default", harness.Tracker.RegisteredNames);
        Assert.DoesNotContain("Scheduled Prefill - Steam - Default", harness.Tracker.RegisteredNames);
    }

    [Fact]
    public async Task TriggerServiceRun_StartsWhileAnotherPlatformIsAlreadyRunning()
    {
        var harness = CreateRecordingHarness<EnabledServicesJustRanStateServiceProxy>();
        using var provider = harness.Provider;

        // The whole point of the change. Two platforms asked for one after the other must BOTH run;
        // before this, the second waited for the first run to finish, which for a real download meant
        // hours. Neither is due, so each run here is the request's doing and nothing else. [49]
        Assert.NotNull(harness.Service.TriggerServiceRun(
            PrefillPlatform.Riot,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Riot)));
        Assert.NotNull(harness.Service.TriggerServiceRun(
            PrefillPlatform.BattleNet,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet)));

        await WaitForRegistrationAsync(harness.Tracker, "Scheduled Prefill - Riot - Default");
        await WaitForRegistrationAsync(harness.Tracker, "Scheduled Prefill - BattleNet - Default");
        harness.Service.Dispose();
    }

    [Fact]
    public async Task ExecuteWorkAsync_RunsNamedRecordsForOnePlatformSequentially()
    {
        var harness = CreateRecordingHarness<PrefillConfigStateServiceProxy>();
        using var provider = harness.Provider;
        var config = BuildConfig(steamEnabled: false, steamIntervalHours: 24d);
        var firstId = Guid.Parse("88888888-8888-8888-8888-888888888881");
        var secondId = Guid.Parse("88888888-8888-8888-8888-888888888882");
        config.Riot.Schedules.Clear();
        config.Riot.Schedules.AddRange(
        [
            new ScheduledPrefillSchedule
            {
                Id = firstId,
                Name = "Daily recent",
                Enabled = true,
                IntervalHours = -1d,
                Preset = ScheduledPrefillPreset.All,
                MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto
                {
                    Mode = ScheduledPrefillMaxConcurrencyMode.Auto
                }
            },
            new ScheduledPrefillSchedule
            {
                Id = secondId,
                Name = "Weekly selected",
                Enabled = true,
                IntervalHours = -1d,
                Preset = ScheduledPrefillPreset.All,
                SelectedAppIds = ["league"],
                MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto
                {
                    Mode = ScheduledPrefillMaxConcurrencyMode.Auto
                }
            }
        ]);
        ((PrefillConfigStateServiceProxy)harness.StateService).Config = config;

        await InvokeExecuteWorkAsync(harness.Service);
        harness.Service.Dispose();

        Assert.Equal(
        [
            (SignalREvents.ScheduledPrefillStarted, firstId),
            (SignalREvents.ScheduledPrefillCompleted, firstId),
            (SignalREvents.ScheduledPrefillStarted, secondId),
            (SignalREvents.ScheduledPrefillCompleted, secondId)
        ],
            harness.Notifications.Lifecycle);
    }

    // The claim that stops one platform being run twice at once is deliberately NOT asserted here. A
    // run in this harness resolves no daemon and finishes in microseconds, so the claim is released
    // before a second call can observe it, and any test of it would pass on timing rather than on the
    // guard. The user-visible half of that guarantee is covered by
    // RunService_ForAPlatformAlreadyRunning_Refuses, which drives the route against the tracker. [49]

    // ---- A disabled saved setup does not enter the automatic due scan ----

    [Fact]
    public async Task TriggerServiceRun_ForADisabledService_DoesNotRunIt()
    {
        var harness = CreateRecordingHarness<PrefillConfigStateServiceProxy>();
        using var provider = harness.Provider;

        // Steam is disabled in the default config, while the two enabled services take their due
        // turn on the same tick. Explicit Run now is tested separately because it may run a saved setup.
        await InvokeExecuteWorkAsync(harness.Service);
        harness.Service.Dispose();

        Assert.DoesNotContain("Scheduled Prefill - Steam - Default", harness.Tracker.RegisteredNames);
        Assert.Contains("Scheduled Prefill - BattleNet - Default", harness.Tracker.RegisteredNames);
        Assert.Contains("Scheduled Prefill - Riot - Default", harness.Tracker.RegisteredNames);
    }

    [Fact]
    public void RunService_ForDisabledSavedSetup_StartsExactRecord()
    {
        var (controller, _) = CreateRunServiceController();
        var scheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam);

        Assert.IsType<AcceptedResult>(
            controller.RunService(PrefillPlatform.Steam, scheduleId));
    }

    [Fact]
    public void RunService_ForUnknownRecordReturnsNotFound()
    {
        var (controller, _) = CreateRunServiceController();

        Assert.IsType<NotFoundResult>(
            controller.RunService(PrefillPlatform.Steam, Guid.NewGuid()));
    }

    [Fact]
    public void GetSchedule_ReturnsStableRecordIdentityAndNoDueTimeForSavedSetup()
    {
        var (controller, _) = CreateRunServiceController();

        var rows = Assert.IsType<ScheduledPrefillServiceScheduleDto[]>(
            Assert.IsType<OkObjectResult>(controller.GetSchedule().Result).Value);
        var steam = Assert.Single(rows, row => row.ServiceId == PrefillPlatform.Steam);

        Assert.Equal(ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam), steam.ScheduleId);
        Assert.Equal("Default", steam.Name);
        Assert.False(steam.Enabled);
        Assert.Null(steam.NextRunUtc);
    }

    [Fact]
    public async Task SetConfigAsync_RejectsDeletingAnActiveRecord()
    {
        var (controller, active) = CreateRunServiceController();
        var scheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam);
        active.Add(new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill - Default",
            Metadata = new ScheduledPrefillServiceRunState(
                PrefillPlatform.Steam,
                scheduleId,
                "Default")
        });

        var replacement = ScheduledPrefillConfigFactory.CreateDefault();
        replacement.Steam.Schedules.Clear();

        Assert.IsType<ConflictObjectResult>(
            await controller.SetConfigAsync(replacement));
    }

    [Fact]
    public void GetRunStatus_CarriesTheExactRecordAndOperationIdentity()
    {
        var (controller, active) = CreateRunServiceController();
        var scheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Xbox);
        var operationId = Guid.NewGuid();
        active.Add(new OperationInfo
        {
            Id = operationId,
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill - Weekly Linux",
            Metadata = new ScheduledPrefillServiceRunState(
                PrefillPlatform.Xbox,
                scheduleId,
                "Weekly Linux")
        });

        var status = Assert.IsType<ScheduledPrefillRunStatusDto>(
            Assert.IsType<OkObjectResult>(controller.GetRunStatus().Result).Value);
        var service = Assert.Single(status.Services);

        Assert.True(status.IsRunning);
        Assert.Equal(scheduleId, service.ScheduleId);
        Assert.Equal("Weekly Linux", service.Name);
        Assert.Equal(operationId.ToString(), service.OperationId);
    }

    // ---- Running state on the schedule rows is PER SERVICE ----
    // The services run concurrently, so a row that read a schedule-wide flag refused to start while any
    // other service was downloading, which is one download blocking every other row's button. [48]

    [Fact]
    public void GetSchedule_ReportsRunningPerService_NotForTheWholeSchedule()
    {
        var (controller, active) = CreateRunServiceController();
        active.Add(RunLevelOperation());
        active.Add(new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill - BattleNet - Default",
            Metadata = new ScheduledPrefillServiceRunState(
                PrefillPlatform.BattleNet,
                ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet),
                "Default")
        });

        var schedule = Assert.IsType<ScheduledPrefillServiceScheduleDto[]>(
            Assert.IsType<OkObjectResult>(controller.GetSchedule().Result).Value);

        // Battle.net is downloading. Riot is not, and the run-level operation must not make it look
        // like it is: that is what disabled every other row's Run button mid-run.
        Assert.True(schedule.Single(row => row.ServiceId == PrefillPlatform.BattleNet).IsRunning);
        Assert.False(schedule.Single(row => row.ServiceId == PrefillPlatform.Riot).IsRunning);
        Assert.False(schedule.Single(row => row.ServiceId == PrefillPlatform.Steam).IsRunning);
    }

    // ---- The per-platform run route tells a start from a queue ----
    // A run already in flight holds the loop, so a platform outside it only queues. The row's toast
    // must say so rather than claim a start, which is what the response's queued flag carries.

    // These three name Battle.net because it is ENABLED in the default config. A disabled platform is
    // refused before either branch below is reached, so testing them with one would pass for the wrong
    // reason. [47]

    [Fact]
    public void RunService_WhileADifferentPlatformRuns_StillStarts()
    {
        var (controller, active) = CreateRunServiceController();
        active.Add(RunLevelOperation());

        // A run in flight no longer holds this back, because the new run does not go through the
        // scheduling loop that run is occupying. This used to answer "queued". [49]
        Assert.IsType<AcceptedResult>(controller.RunService(
            PrefillPlatform.BattleNet,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet)));
    }

    [Fact]
    public void RunService_WithNothingInFlight_Starts()
    {
        var (controller, _) = CreateRunServiceController();

        Assert.IsType<AcceptedResult>(controller.RunService(
            PrefillPlatform.BattleNet,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet)));
    }

    [Fact]
    public void RunService_ForAPlatformAlreadyRunning_Refuses()
    {
        var (controller, active) = CreateRunServiceController();
        active.Add(RunLevelOperation());
        active.Add(new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill - BattleNet - Default",
            Metadata = new ScheduledPrefillServiceRunState(
                PrefillPlatform.BattleNet,
                ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet),
                "Default")
        });

        Assert.IsType<ConflictObjectResult>(controller.RunService(
            PrefillPlatform.BattleNet,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.BattleNet)));
    }

    private static OperationInfo RunLevelOperation() => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.ScheduledPrefill,
        Name = "Scheduled Prefill",
        Metadata = new ScheduledPrefillOperationMetadata(showNotification: true)
    };

    private static (ScheduledPrefillConfigController Controller, List<OperationInfo> Active) CreateRunServiceController()
    {
        var harness = CreateRecordingHarness<PrefillConfigStateServiceProxy>();
        var tracker = (ActiveOperationsTrackerProxy)DispatchProxy.Create<IUnifiedOperationTracker, ActiveOperationsTrackerProxy>();
        var state = (PrefillConfigStateServiceProxy)DispatchProxy.Create<IStateService, PrefillConfigStateServiceProxy>();
        var controller = new ScheduledPrefillConfigController(
            (IStateService)state,
            (IServiceScheduleRegistry)DispatchProxy.Create<IServiceScheduleRegistry, NullReturningProxy>(),
            (IUnifiedOperationTracker)tracker,
            harness.Service);

        return (controller, tracker.Active);
    }

    // Answers GetActiveOperations from a list the test fills; every other member no-ops.
    // Not sealed: DispatchProxy.Create derives the concrete proxy type from this class.
    private class ActiveOperationsTrackerProxy : DispatchProxy
    {
        public List<OperationInfo> Active { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IUnifiedOperationTracker.GetActiveOperations))
            {
                return Active.AsEnumerable();
            }

            return targetMethod?.ReturnType == typeof(Guid) ? Guid.NewGuid() : null;
        }
    }

    [Fact]
    public async Task TriggerImmediateRun_AfterAPerRowRun_StillRunsEveryEnabledService()
    {
        var harness = CreateRecordingHarness<PrefillConfigStateServiceProxy>();
        using var provider = harness.Provider;

        // A whole-schedule Run Now landing on top of a queued per-row run must not be narrowed down
        // to that one platform, which is what leaving the queued platform in place would do.
        harness.Service.TriggerServiceRun(
            PrefillPlatform.Riot,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Riot));
        harness.Service.TriggerImmediateRun();
        await InvokeExecuteWorkAsync(harness.Service);
        harness.Service.Dispose();

        Assert.DoesNotContain("Scheduled Prefill - Steam - Default", harness.Tracker.RegisteredNames);
        Assert.Contains("Scheduled Prefill - BattleNet - Default", harness.Tracker.RegisteredNames);
        Assert.Contains("Scheduled Prefill - Riot - Default", harness.Tracker.RegisteredNames);
    }

    // ---- The reason a service's card gives for closing ----
    // The reported defect: a service that failed by throwing showed its last PROGRESS line as the
    // reason, so the card read "Steam failed: Prefill in progress" and the real error was never seen.

    [Fact]
    public async Task CompleteServiceRun_AThrownFailure_ShowsTheExceptionRatherThanTheProgressLine()
    {
        var state = new ScheduledPrefillServiceRunState(
            PrefillPlatform.Steam,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam),
            "Default");
        // What the card held the moment the service threw.
        state.Record("running", "Prefill in progress", "signalr.scheduledPrefill.running", 42d);

        var terminal = await CompleteServiceRunForTestAsync(
            state, ScheduledPrefillServiceRunResult.Failed, failureMessage: "Docker daemon is not reachable");

        Assert.Equal("Docker daemon is not reachable", terminal.Error);
        // The recorded key names the progress sentence, so carrying it would translate the card
        // straight back into "Prefill in progress" for anyone not reading English.
        Assert.Null(terminal.StageKey);
        Assert.False(terminal.Success);
    }

    [Fact]
    public async Task CompleteServiceRun_AThrowBeforeAnyProgress_StillNamesTheFailure()
    {
        // Nothing recorded yet, which used to degrade the card to the bare word "Failed".
        var state = new ScheduledPrefillServiceRunState(
            PrefillPlatform.Steam,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam),
            "Default");

        var terminal = await CompleteServiceRunForTestAsync(
            state, ScheduledPrefillServiceRunResult.Failed, failureMessage: "The socket connection was aborted");

        Assert.Equal("The socket connection was aborted", terminal.Error);
    }

    [Fact]
    public async Task CompleteServiceRun_AGatedFailure_KeepsTheReasonItsOwnProgressReported()
    {
        // The stall and max-runtime paths report themselves through a progress event and RETURN
        // Failed without throwing, so their recorded line IS the reason and must survive.
        var state = new ScheduledPrefillServiceRunState(
            PrefillPlatform.Steam,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam),
            "Default");
        state.Record("failed", "Prefill stalled (no progress)", "signalr.scheduledPrefill.failedStalled", 99d);

        var terminal = await CompleteServiceRunForTestAsync(
            state, ScheduledPrefillServiceRunResult.Failed, failureMessage: null);

        Assert.Equal("Prefill stalled (no progress)", terminal.Error);
        Assert.Equal("signalr.scheduledPrefill.failedStalled", terminal.StageKey);
    }

    [Fact]
    public async Task CompleteServiceRun_ASkip_KeepsItsOwnReasonAndClosesAsSkipped()
    {
        var state = new ScheduledPrefillServiceRunState(
            PrefillPlatform.Xbox,
            ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Xbox),
            "Default");
        state.Record("skipped", "No running persistent container for Xbox", "signalr.scheduledPrefill.skippedNoContainer", 99d);

        var terminal = await CompleteServiceRunForTestAsync(
            state, ScheduledPrefillServiceRunResult.Skipped, failureMessage: null);

        Assert.Equal("No running persistent container for Xbox", terminal.Error);
        Assert.Equal("signalr.scheduledPrefill.skippedNoContainer", terminal.StageKey);
        Assert.Equal("skipped", terminal.Status);
    }

    private sealed record ServiceTerminal(bool Success, string? Error, string? StageKey, string? Status);

    private static async Task<ServiceTerminal> CompleteServiceRunForTestAsync(
        ScheduledPrefillServiceRunState state,
        ScheduledPrefillServiceRunResult result,
        string? failureMessage)
    {
        var recorder = (TerminalRecordingProxy)DispatchProxy.Create<ISignalRNotificationService, TerminalRecordingProxy>();
        var tracker = (IUnifiedOperationTracker)DispatchProxy.Create<IUnifiedOperationTracker, RecordingTrackerProxy>();

        var serviceRun = new ScheduledPrefillServiceRun(
            ScheduledPrefillConfigFactory.CreateDefault().GetSchedulesInRunOrder().First(schedule =>
                schedule.ServiceId == state.ServiceId && schedule.ScheduleId == state.ScheduleId),
            Guid.NewGuid(),
            "op-1",
            "run-1",
            state,
            CancellationToken.None);

        var completeServiceRun = typeof(ScheduledPrefillService)
            .GetMethod("CompleteServiceRunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

        await (Task)completeServiceRun.Invoke(
            null,
            new object?[] { serviceRun, tracker, (ISignalRNotificationService)recorder, result, true, failureMessage })!;

        return recorder.Terminal ?? throw new InvalidOperationException("No terminal event was emitted.");
    }

    // Records the fields of the per-service terminal payload the card renders its closing line from.
    // Not sealed: DispatchProxy.Create derives the concrete proxy type from this class.
    private class TerminalRecordingProxy : DispatchProxy
    {
        public ServiceTerminal? Terminal { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args is { Length: >= 2 }
                && args[0] as string == SignalREvents.ScheduledPrefillCompleted
                && args[1] is { } payload)
            {
                var type = payload.GetType();
                Terminal = new ServiceTerminal(
                    type.GetProperty("success")?.GetValue(payload) as bool? ?? false,
                    type.GetProperty("error")?.GetValue(payload) as string,
                    type.GetProperty("stageKey")?.GetValue(payload) as string,
                    type.GetProperty("status")?.GetValue(payload) as string);
            }

            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    /// A per-row run executes on its own task, so the trigger returns before the run has registered
    /// anything. Poll rather than await: the task is deliberately not exposed, and the alternative,
    /// StopAsync, cancels the run it would be waiting for.
    private static async Task WaitForRegistrationAsync(RecordingTrackerProxy tracker, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (tracker.RegisteredNames.Contains(name))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"'{name}' was never registered. Registered: {string.Join(", ", tracker.RegisteredNames)}");
    }

    private static Task InvokeExecuteWorkAsync(ScheduledPrefillService service)
    {
        var executeWork = typeof(ScheduledPrefillService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return (Task)executeWork.Invoke(service, new object[] { CancellationToken.None })!;
    }

    private static RecordingHarness CreateRecordingHarness<TStateService>()
        where TStateService : DispatchProxy
    {
        var trackerProxy = DispatchProxy.Create<IUnifiedOperationTracker, RecordingTrackerProxy>();
        var notificationsProxy = DispatchProxy.Create<ISignalRNotificationService, FailableNotificationsProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, TStateService>();

        var services = new ServiceCollection();
        services.AddSingleton((IUnifiedOperationTracker)trackerProxy);
        services.AddSingleton((ISignalRNotificationService)notificationsProxy);
        var provider = services.BuildServiceProvider();

        // No daemon is registered in this provider, so every due platform stops at the first gate and
        // reports "no daemon". That is deliberate: these tests are about WHICH platforms a run opens
        // an operation for, not about what happens once one is running.
        var service = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            stateService);

        return new RecordingHarness(
            service,
            stateService,
            (RecordingTrackerProxy)trackerProxy,
            (FailableNotificationsProxy)notificationsProxy,
            provider);
    }

    private sealed record RecordingHarness(
        ScheduledPrefillService Service,
        IStateService StateService,
        RecordingTrackerProxy Tracker,
        FailableNotificationsProxy Notifications,
        ServiceProvider Provider);

    // No-ops like the null proxy, except that a test can make the next run-level Started throw. That
    // is the reachable way into the window between claiming the platforms and starting them.
    // Not sealed: DispatchProxy.Create derives the concrete proxy type from this class.
    private class FailableNotificationsProxy : DispatchProxy
    {
        private readonly object _sync = new();

        /// <summary>Makes the next run-level ScheduledPrefillStarted throw, then clears itself.</summary>
        public bool FailNextStarted { get; set; }

        public List<(string EventName, Guid ScheduleId)> Lifecycle { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args is { Length: >= 2 }
                && args[0] is string lifecycleEvent
                && args[1]?.GetType().GetProperty("scheduleId")?.GetValue(args[1]) is Guid scheduleId
                && lifecycleEvent is SignalREvents.ScheduledPrefillStarted or SignalREvents.ScheduledPrefillCompleted)
            {
                lock (_sync)
                {
                    Lifecycle.Add((lifecycleEvent, scheduleId));
                }
            }

            if (FailNextStarted
                && targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args?[0] as string == SignalREvents.ScheduledPrefillStarted)
            {
                FailNextStarted = false;
                throw new InvalidOperationException("hub send failed");
            }

            var returnType = targetMethod?.ReturnType;
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType is not null && returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }

    // Records the name every operation registers under, in order, without cancelling anything.
    // Not sealed: DispatchProxy.Create derives the concrete proxy type from this class.
    private class RecordingTrackerProxy : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly List<string> _registeredNames = [];

        public IReadOnlyList<string> RegisteredNames
        {
            get { lock (_sync) return _registeredNames.ToArray(); }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IUnifiedOperationTracker.RegisterOperation)
                && args?[1] is string name)
            {
                lock (_sync)
                {
                    _registeredNames.Add(name);
                }

                return Guid.NewGuid();
            }

            return targetMethod?.ReturnType == typeof(Guid) ? Guid.NewGuid() : null;
        }
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
        private int _runRegisterCount;
        private int _completeCount;

        public int RegisterCount => Volatile.Read(ref _registerCount);

        /// <summary>
        /// Registrations of the RUN itself, told apart from the one-per-platform operations a run
        /// also opens by the name it registers under (args[1]).
        /// </summary>
        public int RunRegisterCount => Volatile.Read(ref _runRegisterCount);

        public int CompleteCount => Volatile.Read(ref _completeCount);
        public bool? LastCompleteSuccess { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                    Interlocked.Increment(ref _registerCount);
                    if (args?[1] as string == "Scheduled Prefill")
                    {
                        Interlocked.Increment(ref _runRegisterCount);
                    }

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
        public ScheduledPrefillConfigDto Config { get; set; } = ScheduledPrefillConfigFactory.CreateDefault();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return Config;
            }

            var returnType = targetMethod?.ReturnType;

            // void is a VALUE TYPE, so it must be handled before the IsValueType branch below:
            // Activator.CreateInstance(typeof(void)) throws "Cannot dynamically create an instance of
            // System.Void". Any void member on the stub (e.g. SetScheduledPrefillServiceLastRun, which
            // the cancellation path now calls) would otherwise blow up the test rather than no-op.
            // NullReturningProxy already guards this; this proxy did not.
            if (returnType == typeof(void))
            {
                return null;
            }

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

    // The default config's enabled services carry no last-run, so all of them are due on every tick.
    // This stub gives both of them a last-run of right now, leaving them enabled but NOT due, which is
    // what isolates "ran because it was named" from "ran because it was due".
    private class EnabledServicesJustRanStateServiceProxy : PrefillConfigStateServiceProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillServiceLastRun)
                && args?[0] is nameof(PrefillPlatform.BattleNet) or nameof(PrefillPlatform.Riot))
            {
                return DateTime.UtcNow;
            }

            return base.Invoke(targetMethod, args);
        }
    }

    private static IStateService CreateNullStateService()
        => (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
}
