using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the persisted-state LOAD path degrading a single corrupt top-level section to its own default
/// (with a warning) while preserving every other section, salvaging valid entries of keyed maps and valid
/// members of durable-cursor compounds, failing security values closed, and never rewriting the source file
/// on a recovery load, instead of discarding the whole state.json when one value fails to deserialize.
/// </summary>
public sealed class StateServiceSectionIsolationTests : IDisposable
{
    private readonly string _root;

    public StateServiceSectionIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-state-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // A1: an unknown enum STRING inside the scheduledPrefill block must reset only that block to its
    // default (via the existing ResolveScheduledPrefillConfig repair path) while preserving other sections.
    [Fact]
    public void LoadState_UnknownStringEnumInScheduledPrefill_DegradesOnlyThatSection()
    {
        var stateJson = BuildStateJson(root =>
        {
            var steam = ScheduledPrefillSteam(root);
            steam["Enabled"] = true;                 // benign distinctive value that must NOT survive the reset
            steam["Preset"] = "totallyBogusPreset";  // unknown string rejected by the enum converter
        });

        var (loaded, logger) = LoadWrittenState(stateJson);

        // Other sections preserved (this fails against the pre-fix whole-discard behavior).
        Assert.True(loaded.SetupCompleted);
        Assert.Equal(250, loaded.AdminPersistentLoginValidityDays);
        Assert.Equal("host", loaded.StatusCheckResolverMode);

        // The scheduledPrefill block itself reset to default (the Enabled=true above was discarded).
        Assert.NotNull(loaded.ScheduledPrefill);
        Assert.False(loaded.ScheduledPrefill.Steam.Enabled);

        AssertWarningNamingSection(logger, nameof(AppState.ScheduledPrefill));
    }

    // A2: a NUMERIC value on a strict (allowIntegerValues:false) enum inside scheduledPrefill must reset only
    // that block. Numeric values only fail on the strict converters (PersistenceMode), so this targets one.
    [Fact]
    public void LoadState_NumericEnumInScheduledPrefill_DegradesOnlyThatSection()
    {
        var stateJson = BuildStateJson(root =>
        {
            var block = ScheduledPrefillBlock(root);
            ScheduledPrefillSteam(root)["Enabled"] = true; // benign distinctive value that must NOT survive
            block["PersistenceMode"] = 999;                // integer rejected by the strict PersistenceMode converter
        });

        var (loaded, logger) = LoadWrittenState(stateJson);

        Assert.True(loaded.SetupCompleted);
        Assert.Equal(250, loaded.AdminPersistentLoginValidityDays);

        Assert.NotNull(loaded.ScheduledPrefill);
        Assert.False(loaded.ScheduledPrefill.Steam.Enabled);

        AssertWarningNamingSection(logger, nameof(AppState.ScheduledPrefill));
    }

    // A3: a corrupt value in a DIFFERENT section must degrade only that section; a valid scheduledPrefill
    // block is preserved intact.
    [Fact]
    public void LoadState_CorruptValueInDifferentSection_PreservesScheduledPrefill()
    {
        var stateJson = BuildStateJson(root =>
        {
            ScheduledPrefillSteam(root)["Enabled"] = true; // valid, distinctive: must survive (default is false)
            root["CrawlIntervalHours"] = "not-a-number";   // invalid double: this section must degrade
        });

        var (loaded, logger) = LoadWrittenState(stateJson);

        // scheduledPrefill preserved (this fails against the pre-fix whole-discard behavior).
        Assert.NotNull(loaded.ScheduledPrefill);
        Assert.True(loaded.ScheduledPrefill.Steam.Enabled);

        // The corrupt section fell back to its default.
        Assert.Equal(1.0, loaded.CrawlIntervalHours);

        AssertWarningNamingSection(logger, nameof(AppState.CrawlIntervalHours));
    }

    // A4: a clean state file loads every section unchanged with no recovery path and no warning, byte-for-byte
    // the same behavior as before the section-isolation change (the happy path is untouched).
    [Fact]
    public void LoadState_CleanStateFile_LoadsAllSectionsWithoutRecovery()
    {
        var stateJson = BuildStateJson(root =>
        {
            ScheduledPrefillSteam(root)["Enabled"] = true;
            root["CrawlIntervalHours"] = 3.5;
        });

        var (loaded, logger) = LoadWrittenState(stateJson);

        Assert.True(loaded.SetupCompleted);
        Assert.Equal(250, loaded.AdminPersistentLoginValidityDays);
        Assert.Equal("host", loaded.StatusCheckResolverMode);
        Assert.Equal(3.5, loaded.CrawlIntervalHours);
        Assert.NotNull(loaded.ScheduledPrefill);
        Assert.True(loaded.ScheduledPrefill.Steam.Enabled);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    // LA-1: a recovery load must NOT rewrite state.json. The original (hand-repairable) file must survive the
    // load byte-for-byte, even though the one-time Steam-auth migration save and anchor-seed save normally run.
    [Fact]
    public void RecoveryLoad_DoesNotRewriteStateFile()
    {
        var stateJson = BuildStateJson(root => ScheduledPrefillSteam(root)["Preset"] = "totallyBogusPreset");
        WriteStateFile(stateJson);
        var bytesBefore = File.ReadAllBytes(StateFilePath);

        var (service, _) = CreateStateService();
        var loaded = service.GetState();

        // Recovery genuinely ran (a preserved section proves it wasn't a no-op)...
        Assert.True(loaded.SetupCompleted);
        // ...and the on-disk file was left exactly as written for manual repair.
        Assert.Equal(bytesBefore, File.ReadAllBytes(StateFilePath));
    }

    // LA-2 / LA-8: exhaustive cross-section invariant. Persist a distinctive AppState in which EVERY writable
    // persisted section holds a non-default value, corrupt exactly one section on disk, reload, and assert
    // every OTHER section round-trips identically to the clean baseline (no unconditional exclusions) AND the
    // file bytes are unchanged by the recovery load. Covers the compound/dictionary sections individually.
    [Theory]
    [InlineData("ServiceIntervals", "ServiceIntervals")]
    [InlineData("DatasourceCacheSizeOverrides", "DatasourceCacheSizeOverrides")]
    [InlineData("ServiceRunOnStartup", "ServiceRunOnStartup")]
    [InlineData("ServiceNotificationMode", "ServiceNotificationMode")]
    [InlineData("ServiceCustomSchedule", "ServiceCustomSchedule")]
    [InlineData("ServiceNotificationDisplayMode", "ServiceNotificationDisplayMode")]
    [InlineData("ScheduledPrefillServiceLastRunUtc", "ScheduledPrefillServiceLastRunUtc")]
    [InlineData("ScheduledPrefillServiceLastActualRunUtc", "ScheduledPrefillServiceLastActualRunUtc")]
    [InlineData("LogProcessing", "LogProcessing")]
    [InlineData("DepotProcessing", "DepotProcessing")]
    [InlineData("ExcludedClientRules", "ExcludedClientRules,ExcludedClientIps")]
    [InlineData("CrawlIntervalHours", "CrawlIntervalHours")]
    [InlineData("StatusCheckResolverMode", "StatusCheckResolverMode")]
    [InlineData("RequireAuthForMetrics", "RequireAuthForMetrics")]
    public void RecoveryLoad_CorruptingOneSection_PreservesEveryOtherSection(string sectionKey, string affectedProps)
    {
        // 1. Persist a fully-distinctive AppState through the real service -> a faithful, complete clean file.
        var (writer, _) = CreateStateService();
        writer.SaveState(BuildDistinctiveState());

        // 2. Baseline: load the clean file with a fresh service.
        var cleanReload = CreateStateService().Service.GetState();

        // 3. Corrupt exactly one top-level section on disk so its deserialize fails.
        CorruptSectionOnDisk(sectionKey);
        var bytesAfterCorruption = File.ReadAllBytes(StateFilePath);

        // 4. Reload; the corrupt section degrades, every other section must equal the clean baseline.
        var corruptReload = CreateStateService().Service.GetState();

        AssertAppStateEqualExcept(cleanReload, corruptReload, affectedProps.Split(','));

        // 5. The recovery load left the (corrupt) file untouched.
        Assert.Equal(bytesAfterCorruption, File.ReadAllBytes(StateFilePath));
    }

    // LA-8 guard: prove the fixture actually makes every compared section observable, so a broad drop can't
    // pass by comparing equal at the initializer. Reflection-driven, so a newly added persisted section that
    // is left at its default fails this test until it is given a distinctive value.
    [Fact]
    public void DistinctiveState_AssignsNonDefaultValueToEveryComparedSection()
    {
        var distinctive = BuildDistinctiveState();
        var fallback = new AppState();

        foreach (var property in ComparedAppStateProperties())
        {
            var distinctiveJson = JsonSerializer.Serialize(property.GetValue(distinctive));
            var defaultJson = JsonSerializer.Serialize(property.GetValue(fallback));
            Assert.True(
                distinctiveJson != defaultJson,
                $"Persisted section '{property.Name}' is not distinctive in BuildDistinctiveState (a drop of it would go undetected).");
        }
    }

    // LA-3: one invalid entry in a keyed map drops only that entry (with a warning); the valid entries survive
    // rather than the whole map resetting (a reset basis map silently re-anchors due runs to "now").
    [Fact]
    public void RecoveryLoad_CorruptDictionaryEntry_DropsOnlyThatEntry()
    {
        var stateJson = BuildStateJson(root => root["ServiceIntervals"] = new JsonObject
        {
            ["steam"] = 4.0,             // valid entry that must survive
            ["epic"] = "not-a-number"    // invalid entry that must be dropped
        });

        var (loaded, logger) = LoadWrittenState(stateJson);

        Assert.NotNull(loaded.ServiceIntervals);
        Assert.Equal(4.0, loaded.ServiceIntervals["steam"]);
        Assert.False(loaded.ServiceIntervals.ContainsKey("epic"));
        Assert.True(loaded.SetupCompleted); // an unrelated section stays preserved

        AssertWarningNamingSection(logger, nameof(AppState.ServiceIntervals));
    }

    [Fact]
    public void DatasourceCacheSizeOverride_RoundTripsAndClearsFromState()
    {
        var (writer, _) = CreateStateService();
        writer.SetDatasourceCacheSizeOverride("alpha", 2_147_483_648L);

        var reloadedService = CreateStateService().Service;
        Assert.Equal(2_147_483_648L, reloadedService.GetDatasourceCacheSizeOverrides()["alpha"]);

        reloadedService.SetDatasourceCacheSizeOverride("ALPHA", null);

        Assert.Empty(CreateStateService().Service.GetDatasourceCacheSizeOverrides());
    }

    // LA-3: same salvage for a schedule last-run map, keeping the valid anchors and dropping only the bad one.
    [Fact]
    public void RecoveryLoad_CorruptLastRunAnchorEntry_KeepsValidAnchors()
    {
        var stateJson = BuildStateJson(root => root["ScheduledPrefillServiceLastRunUtc"] = new JsonObject
        {
            ["Steam"] = JsonValue.Create(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            ["Epic"] = "not-a-date"
        });

        var (loaded, logger) = LoadWrittenState(stateJson);

        Assert.True(loaded.ScheduledPrefillServiceLastRunUtc.ContainsKey("Steam"));
        Assert.False(loaded.ScheduledPrefillServiceLastRunUtc.ContainsKey("Epic"));

        AssertWarningNamingSection(logger, nameof(AppState.ScheduledPrefillServiceLastRunUtc));
    }

    // LA-7: one invalid member of a durable-cursor compound (LogProcessing) resets only that member; the other
    // members (all the datasource log positions) survive rather than the whole checkpoint being discarded.
    [Fact]
    public void RecoveryLoad_CorruptLogProcessingMember_KeepsOtherMembers()
    {
        var stateJson = BuildStateJson(root => root["LogProcessing"] = new JsonObject
        {
            ["Position"] = "not-a-long",                                    // invalid member -> reset to default
            ["DatasourcePositions"] = new JsonObject { ["alpha"] = 11 }     // valid member -> must survive
        });

        var (loaded, logger) = LoadWrittenState(stateJson);

        Assert.Equal(0L, loaded.LogProcessing.Position);                     // corrupt member reset
        Assert.Equal(11L, loaded.LogProcessing.DatasourcePositions["alpha"]); // other member preserved
        Assert.True(loaded.SetupCompleted);                                  // unrelated section preserved

        AssertWarningNamingSection(logger, nameof(AppState.LogProcessing));
    }

    // LA-7: an explicitly-present but invalid security value (metrics auth) fails CLOSED (require auth = true),
    // never falling through to null -> the fail-open configuration default.
    [Fact]
    public void RecoveryLoad_InvalidRequireAuthForMetrics_FailsClosed()
    {
        var stateJson = BuildStateJson(root => root["RequireAuthForMetrics"] = "not-a-bool");

        var (loaded, logger) = LoadWrittenState(stateJson);

        Assert.True(loaded.RequireAuthForMetrics == true);
        Assert.True(loaded.SetupCompleted); // unrelated section preserved

        AssertWarningNamingSection(logger, nameof(AppState.RequireAuthForMetrics));
    }

    // LA-4: a whole-file-unusable state.json falls back to a default state (today's behavior) without emitting
    // the misleading "recovering per-section" warning, for every unrecoverable-root shape.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("\"just a string\"")]
    [InlineData("{ not valid json")]
    public void LoadState_WholeFileUnusable_FallsBackToDefaultWithoutPerSectionWarning(string fileContent)
    {
        WriteRawStateFile(fileContent);

        var (service, logger) = CreateStateService();
        var loaded = service.GetState();

        Assert.NotNull(loaded);
        Assert.False(loaded.SetupCompleted); // a default state
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("recovering per-section", StringComparison.Ordinal));
    }

    // LA-6: GuestModeLocked must survive a persist round-trip (it was silently dropped from both mappers).
    [Fact]
    public void GuestModeLocked_SurvivesPersistRoundTrip()
    {
        var (writer, _) = CreateStateService();
        writer.SetGuestModeLocked(true);

        var reloaded = CreateStateService().Service.GetState();

        Assert.True(reloaded.GuestModeLocked);
    }

    // LA-5: the guest UTC clock default must survive a persist round-trip. It reached AppState without
    // reaching the persisted mirror or either mapper, so an admin's choice was accepted, confirmed by the
    // UI, and gone again after the next restart.
    [Fact]
    public void DefaultGuestUseUtcTimezone_SurvivesPersistRoundTrip()
    {
        var (writer, _) = CreateStateService();
        writer.SaveState(new AppState { DefaultGuestUseUtcTimezone = true });

        // The value was actually serialized (ToPersisted mapped it), not merely re-set by a default on reload.
        var persisted = JsonNode.Parse(File.ReadAllText(StateFilePath))!.AsObject();
        var node = persisted["DefaultGuestUseUtcTimezone"];
        Assert.NotNull(node);
        Assert.True(node!.GetValue<bool>());

        var reloaded = CreateStateService().Service.GetState();
        Assert.True(reloaded.DefaultGuestUseUtcTimezone);
    }

    // ---- eviction-notification migration marker persistence + seeding rule ----

    // S-1: the migration marker must be written by ToPersisted and read back by FromPersisted. Without the
    // mapper members it is silently dropped on every save, so the one-time migration reruns on each restart.
    [Fact]
    public void MigrationMarker_SurvivesPersistRoundTrip()
    {
        var (writer, _) = CreateStateService();
        writer.SaveState(new AppState { EvictionNotificationsMigrated = true, EvictionScanNotifications = false });

        // The marker was actually serialized (ToPersisted mapped it), not merely re-set by a rerun on reload.
        var persisted = JsonNode.Parse(File.ReadAllText(StateFilePath))!.AsObject();
        var markerNode = persisted["EvictionNotificationsMigrated"];
        Assert.NotNull(markerNode);
        Assert.True(markerNode!.GetValue<bool>());

        var reloaded = CreateStateService().Service.GetState();
        Assert.True(reloaded.EvictionNotificationsMigrated);
    }

    // S-2: a restart must not overwrite a mode the user selected after the migration already ran. With the
    // marker persisted, the migration is skipped on reload and the user's Silent choice survives; without it,
    // the migration reruns and (legacy flag = true) re-seeds All, clobbering the selection.
    [Fact]
    public void Restart_WithMarkerAndUserSelectedSilent_KeepsSilent()
    {
        var (writer, _) = CreateStateService();
        writer.SaveState(new AppState
        {
            EvictionScanNotifications = true,
            EvictionNotificationsMigrated = true,
            ServiceNotificationMode = new() { ["cacheReconciliation"] = NotificationMode.Silent }
        });

        var reloaded = CreateStateService().Service.GetState();

        Assert.Equal(NotificationMode.Silent, reloaded.ServiceNotificationMode["cacheReconciliation"]);
    }

    // S-3: Reset to Defaults removes the per-service key but leaves the marker set. A reload must not resurrect
    // the seeded mode from the legacy flag; the key stays absent so the schedule's own default applies.
    [Fact]
    public void ResetToDefaults_ThenReload_DoesNotResurrectSeededMode()
    {
        var stateJson = BuildStateJson(root =>
        {
            root["EvictionScanNotifications"] = true;   // legacy flag still on
            root["EvictionNotificationsMigrated"] = true; // migration already ran; reset cleared the per-service key
            // ServiceNotificationMode intentionally absent (the reset removed the cacheReconciliation entry).
        });

        var (loaded, _) = LoadWrittenState(stateJson);

        Assert.False(loaded.ServiceNotificationMode.ContainsKey("cacheReconciliation"));
    }

    // S-4a: a never-migrated install whose legacy flag was explicitly ON seeds All and sets the marker.
    [Fact]
    public void Migration_LegacyFlagTrue_SeedsAllAndSetsMarker()
    {
        var stateJson = BuildStateJson(root => root["EvictionScanNotifications"] = true);

        var (loaded, _) = LoadWrittenState(stateJson);

        Assert.Equal(NotificationMode.All, loaded.ServiceNotificationMode["cacheReconciliation"]);
        Assert.True(loaded.EvictionNotificationsMigrated);
    }

    // S-4b: a never-migrated install whose legacy flag was OFF (the legacy default, indistinguishable from
    // "never touched") seeds NOTHING, so the schedule's own default applies, and still sets the marker.
    [Fact]
    public void Migration_LegacyFlagFalse_SeedsNothingAndSetsMarker()
    {
        var stateJson = BuildStateJson(root => root["EvictionScanNotifications"] = false);

        var (loaded, _) = LoadWrittenState(stateJson);

        Assert.False(loaded.ServiceNotificationMode.ContainsKey("cacheReconciliation"));
        Assert.True(loaded.EvictionNotificationsMigrated);
    }

    // ---- JSON fixture helpers ----

    private static JsonObject BuildStateJson(Action<JsonObject> customize)
    {
        // Top-level keys use PascalCase to match StateService's on-disk serialization (default naming policy).
        var root = new JsonObject
        {
            ["SetupCompleted"] = true,
            ["AdminPersistentLoginValidityDays"] = 250,
            ["StatusCheckResolverMode"] = "host",
            ["ScheduledPrefill"] = SerializeDefaultScheduledPrefill()
        };

        customize(root);
        return root;
    }

    private static JsonObject SerializeDefaultScheduledPrefill()
    {
        // Serialize a real current-version default config with the same (default) options StateService uses,
        // so the block is a faithful, valid on-disk representation before a test corrupts it.
        var json = JsonSerializer.Serialize(ScheduledPrefillConfigFactory.CreateDefault());
        return JsonNode.Parse(json)!.AsObject();
    }

    private static JsonObject ScheduledPrefillBlock(JsonObject root) => root["ScheduledPrefill"]!.AsObject();

    private static JsonObject ScheduledPrefillSteam(JsonObject root) => ScheduledPrefillBlock(root)["Steam"]!.AsObject();

    // A fully-distinctive AppState: every persisted section (except the two noted below) holds a non-default
    // value, so a dropped section is observable after a round-trip. All five last-run anchors are seeded so
    // SeedInitialFirstRunAnchors is a no-op and the comparison stays deterministic. ScheduledPrefill is left
    // at its validated default (corruption/preservation is covered directly by A1-A3) and SteamAuth is left
    // null (legacy field, migration-nulled on load); both are skipped by the completeness guard.
    private static AppState BuildDistinctiveState() => new()
    {
        LogProcessing = new LogProcessingState { Position = 5555, DatasourcePositions = new() { ["alpha"] = 11L } },
        DepotProcessing = new DepotProcessingState
        {
            IsActive = true,
            Status = DepotScanPhase.Processing,
            TotalApps = 42,
            ProcessedApps = 7,
            DepotMappingsFound = 3,
            LastChangeNumber = 99u
        },
        SetupCompleted = true,
        LastPicsCrawl = new DateTime(2031, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        LastFullPicsCrawl = new DateTime(2032, 4, 5, 6, 7, 8, DateTimeKind.Utc),
        StatusCheckResult = new StatusCheckResult(), // non-null is already distinctive from the default null

        StatusCheckResolverMode = "host",
        EpicMappingLastCollection = new DateTime(2028, 2, 3, 4, 5, 6, DateTimeKind.Utc),
        CrawlIntervalHours = 7.5,
        TopGameCount = 123,
        CrawlIncrementalMode = false,
        LastUpdated = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        HasDataLoaded = true,
        HasProcessedLogs = true,
        GuestSessionDurationHours = 9,
        GuestModeLocked = true,
        DefaultGuestTheme = "light-distinct",
        RefreshRate = RefreshRate.Ultra,
        DefaultGuestRefreshRate = RefreshRate.Relaxed,
        GuestRefreshRateLocked = false,
        SharedAdminSessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        DefaultGuestUseLocalTimezone = true,
        DefaultGuestUseUtcTimezone = true,
        DefaultGuestUse24HourFormat = false,
        DefaultGuestSharpCorners = true,
        DefaultGuestDisableTooltips = true,
        DefaultGuestShowDatasourceLabels = false,
        AllowedTimeFormats = new() { "local-12h" },
        // Set for the same reason as EvictionNotificationsMigrated below: this fixture is compared
        // section by section after a reload, and a marker left at false would let the one-time UTC
        // migration change the state between the two loads.
        UtcTimeFormatMigrated = true,
        GuestPrefillEnabledByDefault = true,
        GuestPrefillDurationHours = 1,
        AdminPersistentLoginValidityDays = 222,
        DefaultPrefillOperatingSystems = new() { "windows" },
        DefaultPrefillMaxConcurrency = "distinct-conc",
        DefaultGuestMaxThreadCount = 6,
        EpicGuestPrefillEnabledByDefault = true,
        EpicGuestPrefillDurationHours = 1,
        EpicDefaultGuestMaxThreadCount = 5,
        EpicDefaultPrefillMaxConcurrency = "distinct-epic",
        BattleNetGuestPrefillEnabledByDefault = true,
        BattleNetGuestPrefillDurationHours = 1,
        RiotGuestPrefillEnabledByDefault = true,
        RiotGuestPrefillDurationHours = 1,
        XboxGuestPrefillEnabledByDefault = true,
        XboxGuestPrefillDurationHours = 1,
        XboxDefaultGuestMaxThreadCount = 4,
        RequiresFullScan = true,
        LastViabilityCheck = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc),
        LastViabilityCheckChangeNumber = 12345u,
        ViabilityChangeGap = 678u,
        RequireAuthForMetrics = true,
        ExcludedClientIps = new() { "9.9.9.9" },
        ExcludedClientRules = new() { new ClientExclusionRule { Ip = "10.0.0.5", Mode = ClientExclusionModes.Hide } },
        EvictedDataMode = EvictedDataMode.Hide,
        EvictionScanNotifications = true,
        PruneOrphanedDownloads = true,
        ClientHostnameLookup = true,
        ClientHostnameResolver = "172.16.1.222",
        ClientHostnameGuestAccess = true,
        ClientHostnameRouterLookup = false,
        ClientHostnameDockerLookup = false,
        CurrentSetupStep = SetupStep.DepotInit,
        DataSourceChoice = DataSourceChoice.Steam,
        CompletedPlatforms = "{\"steam\":\"github\"}",
        ServiceIntervals = new() { ["steam"] = 4.0, ["epic"] = 8.0 },
        DatasourceCacheSizeOverrides = new() { ["alpha"] = 2_147_483_648L },
        ServiceRunOnStartup = new() { ["steam"] = true },
        ServiceNotificationMode = new() { ["cacheReconciliation"] = NotificationMode.Silent },
        ServiceCustomSchedule = new()
        {
            ["gameDetection"] = new CustomSchedule
            {
                Expression = "0 3 * * 1",
                TimeZoneId = "Europe/Berlin",
                WindowStart = new TimeOnly(22, 0),
                WindowEnd = new TimeOnly(6, 0),
            },
        },
        ServiceNotificationDisplayMode = new() { ["cacheReconciliation"] = NotificationDisplayMode.Condensed },
        EvictionNotificationsMigrated = true,
        ScheduledPrefillServiceLastRunUtc = new()
        {
            ["Steam"] = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ["Epic"] = new DateTime(2030, 1, 3, 3, 4, 5, DateTimeKind.Utc),
            ["Xbox"] = new DateTime(2030, 1, 4, 3, 4, 5, DateTimeKind.Utc),
            ["BattleNet"] = new DateTime(2030, 1, 5, 3, 4, 5, DateTimeKind.Utc),
            ["Riot"] = new DateTime(2030, 1, 6, 3, 4, 5, DateTimeKind.Utc)
        },
        ScheduledPrefillServiceLastActualRunUtc = new() { ["Epic"] = new DateTime(2029, 6, 7, 8, 9, 10, DateTimeKind.Utc) }
    };

    // The persisted AppState sections compared by the exhaustive test: public read/write properties that are
    // serialized (not [JsonIgnore]), excluding ScheduledPrefill (validated DTO, covered by A1-A3) and the
    // legacy migration-nulled SteamAuth.
    private static IEnumerable<PropertyInfo> ComparedAppStateProperties()
    {
        foreach (var property in typeof(AppState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            {
                continue;
            }

            if (property.Name is nameof(AppState.ScheduledPrefill) or nameof(AppState.SteamAuth))
            {
                continue;
            }

            yield return property;
        }
    }

    // An operation history row whose type the converter cannot parse must drop only that row: the
    // remaining rows still load, in file order, and the dropped row is named in a warning.
    [Fact]
    public void GetOperationStates_UnparseableRow_KeepsTheOtherRows()
    {
        var firstId = Guid.NewGuid();
        var unparseableId = Guid.NewGuid();
        var lastId = Guid.NewGuid();

        var historyPath = Path.Combine(_root, nameof(IPathResolver.GetOperationsDirectory), "operation_history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        File.WriteAllText(historyPath, new JsonArray(
            OperationHistoryRow(firstId, "cacheClearing"),
            OperationHistoryRow(unparseableId, "totallyBogusOperationType"),
            OperationHistoryRow(lastId, "logProcessing")).ToJsonString());

        var (service, logger) = CreateStateService();

        var states = service.GetOperationStates();

        Assert.Equal(new[] { firstId, lastId }, states.Select(state => state.Id).ToArray());
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(unparseableId.ToString(), StringComparison.Ordinal));
    }

    // ---- file / service helpers ----

    // PascalCase keys and camelCase enum values, matching what SaveOperationStates writes. Running is
    // non-terminal, so the retention prune that follows a successful load leaves every row in place.
    private static JsonObject OperationHistoryRow(Guid id, string type) => new()
    {
        ["Id"] = id.ToString(),
        ["Type"] = type,
        ["Status"] = "running",
        ["Message"] = "row " + id,
        ["CreatedAt"] = DateTime.UtcNow,
        ["UpdatedAt"] = DateTime.UtcNow
    };

    private string StateFilePath => Path.Combine(_root, nameof(IPathResolver.GetStateDirectory), "state.json");

    private void WriteStateFile(JsonObject stateJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        File.WriteAllText(StateFilePath, stateJson.ToJsonString());
    }

    private void WriteRawStateFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        File.WriteAllText(StateFilePath, content);
    }

    private void CorruptSectionOnDisk(string sectionKey)
    {
        var root = JsonNode.Parse(File.ReadAllText(StateFilePath))!.AsObject();

        switch (sectionKey)
        {
            // Keyed maps: an entry of the wrong type fails the whole-section deserialize (then salvage runs).
            case "ServiceIntervals":
            case "DatasourceCacheSizeOverrides":
            case "ServiceRunOnStartup":
            case "ServiceNotificationMode":
            case "ServiceCustomSchedule":
            case "ServiceNotificationDisplayMode":
            case "ScheduledPrefillServiceLastRunUtc":
            case "ScheduledPrefillServiceLastActualRunUtc":
                root[sectionKey]!.AsObject()["__corrupt__"] = "not-a-valid-value";
                break;
            // Durable-cursor compounds: a member of the wrong type fails the deserialize (then member salvage runs).
            case "LogProcessing":
                root["LogProcessing"]!.AsObject()["Position"] = "not-a-long";
                break;
            case "DepotProcessing":
                // TotalApps is an int; a string fails to deserialize. (Status uses a fault-tolerant converter
                // that maps an unknown value to Unknown rather than throwing, so it would not trigger recovery.)
                root["DepotProcessing"]!.AsObject()["TotalApps"] = "not-an-int";
                break;
            // A list: a non-array value fails its deserialize.
            case "ExcludedClientRules":
                root["ExcludedClientRules"] = "not-an-array";
                break;
            // A number into a string field, a string into a number field, and a string into a bool? field.
            case "StatusCheckResolverMode":
                root["StatusCheckResolverMode"] = 123;
                break;
            case "CrawlIntervalHours":
                root["CrawlIntervalHours"] = "not-a-number";
                break;
            case "RequireAuthForMetrics":
                root["RequireAuthForMetrics"] = "not-a-bool";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sectionKey), sectionKey, "Unhandled corruption target.");
        }

        File.WriteAllText(StateFilePath, root.ToJsonString());
    }

    private (AppState State, RecordingLogger<StateService> Logger) LoadWrittenState(JsonObject stateJson)
    {
        WriteStateFile(stateJson);
        var (service, logger) = CreateStateService();
        return (service.GetState(), logger);
    }

    private (StateService Service, RecordingLogger<StateService> Logger) CreateStateService()
    {
        var configuration = new ConfigurationBuilder().Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        var dataProtection = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(_root, "dp-keys")));
        var apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            pathResolver);
        var encryption = new SecureStateEncryptionService(
            dataProtection,
            apiKeyService,
            NullLogger<SecureStateEncryptionService>.Instance);
        var steamAuthStorage = new SteamAuthStorageService(
            NullLogger<SteamAuthStorageService>.Instance,
            pathResolver,
            encryption);

        var logger = new RecordingLogger<StateService>();
        var service = new StateService(logger, pathResolver, encryption, steamAuthStorage);
        return (service, logger);
    }

    // ---- assertions ----

    private static void AssertWarningNamingSection(RecordingLogger<StateService> logger, string sectionName)
    {
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(sectionName, StringComparison.Ordinal));
    }

    // Compares two loaded AppStates section-by-section (whole-object JSON diff), ignoring ONLY the sections
    // affected by the corruption under test. No unconditional exclusions: the fixture seeds all last-run
    // anchors so seeding is a no-op, and the clean baseline reload re-saves the file so both loads observe the
    // same LastUpdated stamp, keeping every remaining section deterministic.
    private static void AssertAppStateEqualExcept(AppState expected, AppState actual, params string[] exceptProperties)
    {
        var ignored = new HashSet<string>(exceptProperties, StringComparer.Ordinal);

        var expectedNode = JsonSerializer.SerializeToNode(expected)!.AsObject();
        var actualNode = JsonSerializer.SerializeToNode(actual)!.AsObject();
        foreach (var property in ignored)
        {
            expectedNode.Remove(property);
            actualNode.Remove(property);
        }

        Assert.Equal(expectedNode.ToJsonString(), actualNode.ToJsonString());
    }

    // ---- fakes (hand-rolled; no mocking framework, matching the suite idiom) ----

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

}
