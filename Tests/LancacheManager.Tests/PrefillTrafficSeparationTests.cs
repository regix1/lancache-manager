using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Configuration;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Prefill rules: rows marked as prefill that a rule covers drop out of served, saved, hit ratio
/// and client numbers but stay in Added to cache; with no prefill rule every number counts them.
/// </summary>
public sealed class PrefillTrafficSeparationTests : IDisposable
{
    private const string Device = "10.0.0.1";
    private const string Container = "172.17.0.2";
    private const string OtherContainer = "172.17.0.3";
    private static readonly DateTime Start = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefill-rules-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ServiceSharesAddUpToOneHundredWhileAClientIsStatsExcluded()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database,
            Row("steam", Device, 1000, 0),
            Row("steam", "10.0.0.9", 1000, 0),
            Row("epicgames", "10.0.0.2", 1000, 0));
        var excluded = new List<string> { "10.0.0.9" };

        var batch = await BatchDashboardAsync(database, [], statsExcluded: excluded);
        var stats = await StatsDashboardAsync(database, State([], statsExcluded: excluded));

        Assert.InRange(batch.ServiceBreakdown.Sum(item => item.Percentage), 99.9, 100.1);
        Assert.InRange(stats.ServiceBreakdown.Sum(item => item.Percentage), 99.9, 100.1);
    }

    [Fact]
    public async Task WithSeparationOffPrefillRowsCountLikeAnyOther()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("steam", Container, 0, 5000, isPrefill: true));

        foreach (var response in new[]
                 {
                     await BatchDashboardAsync(database, []),
                     await StatsDashboardAsync(database, State([]))
                 })
        {
            Assert.False(response.PrefillSeparated);
            Assert.Equal(6200, response.TotalServed);
            Assert.Equal(1000, response.TotalBandwidthSaved);
            Assert.Equal(5200, response.TotalAddedToCache);
            Assert.Equal(6200, response.Period.TotalServed);
            Assert.Equal(5200, response.Period.AddedToCache);
            Assert.Equal(2, response.Period.Downloads);
            Assert.Equal(2, response.UniqueClients);
        }
    }

    [Fact]
    public async Task WithSeparationOnPrefillRowsLeaveServedAndClientsButStayInAddedToCache()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("steam", Container, 0, 5000, isPrefill: true));

        var batch = await BatchDashboardAsync(database, [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]);
        var stats = await StatsDashboardAsync(database, State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]));
        // The batch reports the ratio as a percentage and the stats endpoint as a fraction.
        Assert.Equal(1000 * 100.0 / 1200, batch.CacheHitRatio, 3);
        Assert.Equal(1000.0 / 1200, stats.CacheHitRatio, 3);

        foreach (var response in new[] { batch, stats })
        {
            Assert.True(response.PrefillSeparated);
            Assert.Equal(1200, response.TotalServed);
            Assert.Equal(1000, response.TotalBandwidthSaved);
            Assert.Equal(5200, response.TotalAddedToCache);
            Assert.Equal(1200, response.Period.TotalServed);
            Assert.Equal(5200, response.Period.AddedToCache);
            Assert.Equal(1, response.Period.Downloads);
            Assert.Equal(1, response.UniqueClients);
            Assert.InRange(response.ServiceBreakdown.Sum(item => item.Percentage), 99.9, 100.1);
        }
    }

    [Fact]
    public async Task TheAddedToCacheSparklineKeepsPrefillBytesWhileServedDropsThem()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("steam", Container, 0, 5000, isPrefill: true));

        var on = Assert.IsType<SparklineDataResponse>(await InvokeSectionAsync(database, "GetSparklineDataAsync",
            [null, null, new List<long>(), null, new List<string>(), "show", new List<string>(), new List<ClientExclusionRule> { CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide) }, CancellationToken.None]));
        var off = Assert.IsType<SparklineDataResponse>(await InvokeSectionAsync(database, "GetSparklineDataAsync",
            [null, null, new List<long>(), null, new List<string>(), "show", new List<string>(), new List<ClientExclusionRule>(), CancellationToken.None]));

        Assert.Equal(5200, on.AddedToCache.Data.Sum(), 0);
        Assert.Equal(1200, on.TotalServed.Data.Sum(), 0);
        Assert.Equal(5200, off.AddedToCache.Data.Sum(), 0);
        Assert.Equal(6200, off.TotalServed.Data.Sum(), 0);
    }

    [Fact]
    public async Task WithSeparationOnTheDownloadSectionsLeavePrefillRowsOut()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("epicgames", Container, 0, 5000, isPrefill: true));

        object?[] Totals(bool separate) =>
            [null, null, new List<long>(), null, new List<string>(), "show",
                separate ? new List<ClientExclusionRule> { CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide) } : new List<ClientExclusionRule>(),
                CancellationToken.None];

        var onTotals = await InvokeSectionAsync(database, "GetDownloadTotalsAsync", Totals(true));
        var offTotals = await InvokeSectionAsync(database, "GetDownloadTotalsAsync", Totals(false));
        Assert.Equal(1, Property<int>(onTotals, "Count"));
        Assert.Equal(2, Property<int>(offTotals, "Count"));

        var onClients = Assert.IsType<List<string>>(await InvokeSectionAsync(database, "GetClientOptionsAsync", Totals(true)));
        var offClients = Assert.IsType<List<string>>(await InvokeSectionAsync(database, "GetClientOptionsAsync", Totals(false)));
        Assert.Equal([Device], onClients);
        Assert.Equal(2, offClients.Count);

        var onServices = (System.Collections.IEnumerable)await InvokeSectionAsync(database, "GetServiceOptionsAsync", Totals(true));
        Assert.DoesNotContain(
            onServices.Cast<object>(),
            option => Property<string>(option, "Service") == "epicgames");
    }

    [Fact]
    public async Task MarkingMarksOnlyRowsFromTheRunsAddressServiceAndWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.Add(Session("steam-session", PrefillPlatform.Steam));
            seed.PrefillSessions.Add(Session("xbox-session", PrefillPlatform.Xbox));
            seed.PrefillRuns.Add(Run("steam-session", Start, Start.AddHours(1)));
            seed.PrefillRuns.Add(Run("xbox-session", Start, Start.AddHours(1)));
            // Started before the setting was turned on, so it marks nothing.
            seed.PrefillRuns.Add(Run("steam-session", Start.AddDays(-2), Start.AddDays(-2).AddHours(1)));
            seed.Downloads.AddRange(
                Row("steam", Container, 0, 100, start: Start.AddMinutes(10)),
                Row("xboxlive", Container, 0, 100, start: Start.AddMinutes(10)),
                Row("steam", Device, 0, 100, start: Start.AddMinutes(10)),
                Row("epicgames", Container, 0, 100, start: Start.AddMinutes(10)),
                Row("steam", Container, 0, 100, start: Start.AddHours(3)),
                Row("steam", Container, 0, 100, start: Start.AddDays(-2).AddMinutes(10)));
            await seed.SaveChangesAsync();
        }

        var sessions = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);
        await sessions.MarkPrefillRowsAsync(Start.AddDays(-1), "default", DateTime.UtcNow, CancellationToken.None);

        await using var read = new AppDbContext(database.Options);
        var marked = await read.Downloads
            .Where(d => d.IsPrefill)
            .Select(d => d.Service)
            .OrderBy(service => service)
            .ToListAsync();
        Assert.Equal(["steam", "xboxlive"], marked);
    }

    [Fact]
    public async Task AMarkingPassReadsOnlyRunsStillOpenOrEndedSinceThePreviousIngestPass()
    {
        await using var database = await TestDatabase.CreateAsync();
        var window = Start.AddDays(-1);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.Add(Session("steam-session", PrefillPlatform.Steam));
            seed.PrefillRuns.Add(Run("steam-session", Start, Start.AddHours(1)));
            seed.PrefillRuns.Add(Run("steam-session", Start, null, OtherContainer));
            await seed.SaveChangesAsync();
        }
        var sessions = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);

        await sessions.MarkPrefillRowsAsync(window, "default", Start.AddHours(2), CancellationToken.None);
        var ended = Row("steam", Container, 0, 100, start: Start.AddMinutes(10));
        var open = Row("steam", OtherContainer, 0, 100, start: Start.AddMinutes(10));
        await SeedAsync(database, ended, open);
        await sessions.MarkPrefillRowsAsync(window, "default", Start.AddHours(3), CancellationToken.None);

        Assert.True(await IsMarkedAsync(database, open.Id));
        Assert.False(await IsMarkedAsync(database, ended.Id));

        sessions.ResetMarkingPass();
        await sessions.MarkPrefillRowsAsync(window, "default", Start.AddHours(4), CancellationToken.None);
        Assert.True(await IsMarkedAsync(database, ended.Id));

        // Back to a bounded pass, then a new window start reads the whole window again.
        await UnmarkAsync(database, ended.Id);
        await sessions.MarkPrefillRowsAsync(window, "default", Start.AddHours(5), CancellationToken.None);
        Assert.False(await IsMarkedAsync(database, ended.Id));
        await sessions.MarkPrefillRowsAsync(window.AddMinutes(-1), "default", Start.AddHours(6), CancellationToken.None);
        Assert.True(await IsMarkedAsync(database, ended.Id));
    }

    [Fact]
    public async Task AMarkingPassThatThrowsRecordsNothingSoTheNextPassReadsEveryRun()
    {
        await using var database = await TestDatabase.CreateAsync();
        var window = Start.AddDays(-1);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.Add(Session("steam-session", PrefillPlatform.Steam));
            seed.PrefillRuns.Add(Run("steam-session", Start, Start.AddHours(1)));
            await seed.SaveChangesAsync();
        }
        var ended = Row("steam", Container, 0, 100, start: Start.AddMinutes(10));
        await SeedAsync(database, ended);
        var contexts = new SwitchableDbContextFactory();
        var sessions = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sessions.MarkPrefillRowsAsync(window, "default", Start.AddHours(2), CancellationToken.None));
        contexts.Recover(database.Factory);
        await sessions.MarkPrefillRowsAsync(window, "default", Start.AddHours(3), CancellationToken.None);

        Assert.True(await IsMarkedAsync(database, ended.Id));
    }

    [Fact]
    public async Task ARunThatEndedWhileThePreviousIngestPassRanIsMarkedByTheNextPassOfItsDatasource()
    {
        await using var database = await TestDatabase.CreateAsync();
        var window = Start.AddDays(-1);
        var firstIngest = Start.AddHours(2);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.Add(Session("steam-session", PrefillPlatform.Steam));
            seed.PrefillRuns.Add(Run("steam-session", Start, firstIngest.AddSeconds(1)));
            await seed.SaveChangesAsync();
        }
        var sessions = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);

        await sessions.MarkPrefillRowsAsync(window, "default", firstIngest, CancellationToken.None);
        var row = Row("steam", Container, 0, 100, start: Start.AddMinutes(10));
        await SeedAsync(database, row);
        // Another datasource's later pass must not move this datasource's cutoff.
        await sessions.MarkPrefillRowsAsync(window, "other", firstIngest.AddHours(2), CancellationToken.None);
        await UnmarkAsync(database, row.Id);
        await sessions.MarkPrefillRowsAsync(window, "default", firstIngest.AddHours(3), CancellationToken.None);

        Assert.True(await IsMarkedAsync(database, row.Id));
    }

    [Fact]
    public async Task APassRunningAcrossTwoSavesNeverMarksTheEndedWindowAfterTheClear()
    {
        await using var database = await TestDatabase.CreateAsync();
        var state = StateTestMethods.CreateStateService(_root);
        var pause = new PauseSecondPrefillMark();
        var sessions = new PrefillSessionService(
            new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(pause).Options),
            NullLogger<PrefillSessionService>.Instance);
        var (controller, _) = ExclusionsController(new AppDbContext(database.Options), state, sessions);
        var withRule = new UpdateStatsExclusionsRequest { Rules = [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)] };
        await controller.UpdateExcludedClientsAsync(withRule);
        var window = state.GetPrefillMarkingSinceUtc()!.Value;
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.Add(Session("steam-session", PrefillPlatform.Steam));
            seed.PrefillRuns.Add(Run("steam-session", window.AddSeconds(1), window.AddSeconds(2)));
            seed.PrefillRuns.Add(Run("steam-session", window.AddSeconds(1), window.AddSeconds(2), OtherContainer));
            await seed.SaveChangesAsync();
        }
        var first = Row("steam", Container, 0, 100, start: window.AddSeconds(1));
        var second = Row("steam", OtherContainer, 0, 100, start: window.AddSeconds(1));
        await SeedAsync(database, first, second);

        pause.Armed = true;
        var pass = sessions.MarkPrefillRowsAsync(window, "default", DateTime.UtcNow, CancellationToken.None);
        await pause.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var saves = Task.Run(async () =>
        {
            await controller.UpdateExcludedClientsAsync(new UpdateStatsExclusionsRequest { Rules = [] });
            await controller.UpdateExcludedClientsAsync(withRule);
        });
        // The saves either finish here or wait for the pass; both orders must end the same way.
        await Task.WhenAny(saves, Task.Delay(TimeSpan.FromSeconds(2)));
        pause.Release.SetResult();
        await pass.WaitAsync(TimeSpan.FromSeconds(10));
        await saves.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await IsMarkedAsync(database, first.Id));
        Assert.False(await IsMarkedAsync(database, second.Id));
    }

    [Fact]
    public async Task APassThatReadItsWindowBeforeTheClearMarksNoRunOfTheEndedWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var state = StateTestMethods.CreateStateService(_root);
        var sessions = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);
        var (controller, _) = ExclusionsController(new AppDbContext(database.Options), state, sessions);
        var withRule = new UpdateStatsExclusionsRequest { Rules = [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)] };
        await controller.UpdateExcludedClientsAsync(withRule);
        var endedWindow = state.GetPrefillMarkingSinceUtc()!.Value;
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.Add(Session("steam-session", PrefillPlatform.Steam));
            seed.PrefillRuns.Add(Run("steam-session", endedWindow, endedWindow.AddMilliseconds(1)));
            await seed.SaveChangesAsync();
        }
        var row = Row("steam", Container, 0, 100, start: endedWindow);
        await SeedAsync(database, row);
        await controller.UpdateExcludedClientsAsync(new UpdateStatsExclusionsRequest { Rules = [] });
        await controller.UpdateExcludedClientsAsync(withRule);

        // The ingest pass read the window start before the saves above.
        await sessions.MarkPrefillRowsAsync(endedWindow, "default", DateTime.UtcNow, CancellationToken.None);

        Assert.False(await IsMarkedAsync(database, row.Id));
    }

    /// <summary>Holds the second prefill mark of a pass until <see cref="Release"/> is set.</summary>
    private sealed class PauseSecondPrefillMark : DbCommandInterceptor
    {
        private int _marks;

        public volatile bool Armed;
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData commandEvent,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed
                && command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _marks) == 2)
            {
                Paused.SetResult();
                await Release.Task;
            }

            return result;
        }
    }

    private static async Task<bool> IsMarkedAsync(TestDatabase database, long id)
    {
        await using var read = new AppDbContext(database.Options);
        return await read.Downloads.Where(d => d.Id == id).Select(d => d.IsPrefill).SingleAsync();
    }

    private static async Task UnmarkAsync(TestDatabase database, long id)
    {
        await using var write = new AppDbContext(database.Options);
        await write.Downloads
            .Where(d => d.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(d => d.IsPrefill, false));
    }

    [Fact]
    public void TheLiveSnapshotHidesAPrefillAddressOnlyOnItsOwnService()
    {
        var snapshot = new DownloadSpeedSnapshot
        {
            StreamId = "prefill-visibility",
            GameSpeeds =
            [
                new GameSpeedInfo { ClientIp = Container, Service = "steam", BytesPerSecond = 100 },
                new GameSpeedInfo { ClientIp = Container, Service = "epicgames", BytesPerSecond = 40 },
                new GameSpeedInfo { ClientIp = Container, Service = "xboxlive", BytesPerSecond = 20 },
                new GameSpeedInfo { ClientIp = Device, Service = "steam", BytesPerSecond = 10 }
            ]
        };
        List<PrefillClientAddress> live = [new("steam", Container), new("xbox", Container)];

        var during = RustSpeedTrackerService.BuildClientVisibleSnapshot(snapshot, [], "show", live);
        var afterTeardown = RustSpeedTrackerService.BuildClientVisibleSnapshot(snapshot, [], "show", []);

        Assert.Equal(
            [("epicgames", Container), ("steam", Device)],
            during.GameSpeeds.Select(g => (g.Service, g.ClientIp)).OrderBy(pair => pair.Service).ToList());
        Assert.Equal(50, during.TotalBytesPerSecond);
        Assert.Equal(4, afterTeardown.GameSpeeds.Count);
    }

    [Fact]
    public async Task SavedPrefillRulesComeBackWithAllFourFieldsAndTheSaveBroadcastsAfterTheRefresh()
    {
        await using var database = await TestDatabase.CreateAsync();
        var state = StateTestMethods.CreateStateService(_root);
        List<ClientExclusionRule> rules =
        [
            CacheScanGateHarness.PrefillRule("10.0.0.5", ClientExclusionModes.Hide, PrefillPlatform.Steam),
            CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Exclude)
        ];
        var (controller, recorder) = ExclusionsController(
            new AppDbContext(database.Options),
            state,
            new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance));

        state.SetExcludedClientRules(rules);
        var stored = state.GetExcludedClientRules();
        var result = await controller.UpdateExcludedClientsAsync(new UpdateStatsExclusionsRequest { Rules = rules });

        var answer = Assert.IsType<StatsExclusionsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(rules.Select(rule => rule.RuleKey), stored.Select(rule => rule.RuleKey));
        Assert.Equal(rules.Select(rule => rule.RuleKey), answer.Rules.Select(rule => rule.RuleKey));
        Assert.Equal(
            [SignalREvents.DownloadsRefresh, SignalREvents.PrefillTrafficSettingChanged],
            recorder.Events.Select(item => item.EventName));
    }

    [Fact]
    public async Task ARuleForEveryAddressMustBeAPrefillRuleAndTheTrafficValueMustBeKnown()
    {
        var state = StateTestMethods.CreateStateService(_root);
        state.SetExcludedClientRules([CacheScanGateHarness.PrefillRule("10.0.0.5", ClientExclusionModes.Hide, PrefillPlatform.Steam)]);
        var before = state.GetExcludedClientRules().Select(rule => rule.RuleKey).ToList();
        // The 400 answers before any database work, so no context is needed.
        var (controller, recorder) = ExclusionsController(null!, state, prefillSessions: null!);

        foreach (var (rule, reported) in new[]
                 {
                     (new ClientExclusionRule { Ip = "", Mode = ClientExclusionModes.Hide, Traffic = ClientExclusionTraffic.All }, ""),
                     (new ClientExclusionRule { Ip = "10.0.0.6", Mode = ClientExclusionModes.Hide, Traffic = "everything" }, "10.0.0.6")
                 })
        {
            var result = await controller.UpdateExcludedClientsAsync(new UpdateStatsExclusionsRequest { Rules = [rule] });

            var body = Assert.IsType<BadRequestObjectResult>(result.Result).Value!;
            Assert.Equal("errors.stats.invalidExclusions", Property<string>(body, "stageKey"));
            Assert.Equal([reported], Property<List<string>>(body, "invalidIps"));
        }

        Assert.Equal(before, state.GetExcludedClientRules().Select(rule => rule.RuleKey));
        Assert.Empty(recorder.Events);
    }

    [Fact]
    public async Task APrefillRuleForOneServiceHidesOnlyThatServicesPrefillDownloads()
    {
        await using var database = await TestDatabase.CreateAsync();
        var steamPrefill = Row("steam", Container, 0, 5000, isPrefill: true);
        var epicPrefill = Row("epicgames", Container, 0, 3000, isPrefill: true);
        var steamNormal = Row("steam", Device, 1000, 200);
        await SeedAsync(database, steamPrefill, epicPrefill, steamNormal);
        List<ClientExclusionRule> rules = [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide, PrefillPlatform.Steam)];
        var state = State(rules);

        await Assert.ThrowsAsync<NotFoundException>(() => DownloadByIdAsync(database, state, steamPrefill.Id));
        Assert.IsType<OkObjectResult>((await DownloadByIdAsync(database, state, epicPrefill.Id)).Result);
        Assert.IsType<OkObjectResult>((await DownloadByIdAsync(database, state, steamNormal.Id)).Result);

        foreach (var response in new[] { await BatchDashboardAsync(database, rules), await StatsDashboardAsync(database, state) })
        {
            Assert.Equal(4200, response.TotalServed);
            // Added to cache keeps every prefill row under every rule.
            Assert.Equal(8200, response.TotalAddedToCache);
            Assert.Equal(8200, response.Period.AddedToCache);
        }

        var clients = await StatsClientsAsync(database, includeExcluded: false, rules);
        Assert.Equal(3000, Assert.Single(clients, client => client.ClientIp == Container).TotalBytes);
        Assert.Equal(1200, Assert.Single(clients, client => client.ClientIp == Device).TotalBytes);
    }

    [Fact]
    public async Task APrefillRuleForOneAddressHidesOnlyThatClientsPrefillDownloads()
    {
        await using var database = await TestDatabase.CreateAsync();
        var hidden = Row("steam", Container, 0, 5000, isPrefill: true);
        var kept = Row("steam", OtherContainer, 0, 4000, isPrefill: true);
        await SeedAsync(database, hidden, kept, Row("steam", Device, 1000, 200));
        var state = State([CacheScanGateHarness.PrefillRule(Container, ClientExclusionModes.Hide)]);

        await Assert.ThrowsAsync<NotFoundException>(() => DownloadByIdAsync(database, state, hidden.Id));
        Assert.IsType<OkObjectResult>((await DownloadByIdAsync(database, state, kept.Id)).Result);
        Assert.Equal(5200, (await StatsDashboardAsync(database, state)).TotalServed);
    }

    [Fact]
    public async Task APrefillRuleForSteamAndXboxOnOneAddressKeepsThatAddresssOtherRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        var steamPrefill = Row("steam", Container, 0, 5000, isPrefill: true);
        var xboxPrefill = Row("xboxlive", Container, 0, 4000, isPrefill: true);
        var epicPrefill = Row("epicgames", Container, 0, 3000, isPrefill: true);
        var steamOrdinary = Row("steam", Container, 1000, 200);
        await SeedAsync(database, steamPrefill, xboxPrefill, epicPrefill, steamOrdinary);
        await using var context = new AppDbContext(database.Options);

        var kept = await context.Downloads
            .ApplyClientFilters([], [], [CacheScanGateHarness.PrefillRule(Container, ClientExclusionModes.Hide, PrefillPlatform.Steam, PrefillPlatform.Xbox)])
            .Select(d => d.Id)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(new[] { epicPrefill.Id, steamOrdinary.Id }.Order(), kept);
    }

    [Fact]
    public async Task TheClientFiltersDropEachPartAndTheAddedToCacheArmKeepsPrefillRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string hiddenIp = "10.0.0.7";
        const string statsOnlyIp = "10.0.0.8";
        var hidden = Row("steam", hiddenIp, 1000, 200);
        var statsOnly = Row("steam", statsOnlyIp, 1000, 200);
        var prefill = Row("steam", Container, 0, 5000, isPrefill: true);
        var ordinary = Row("steam", Device, 1000, 200);
        await SeedAsync(database, hidden, statsOnly, prefill, ordinary);
        await using var context = new AppDbContext(database.Options);

        var served = await context.Downloads
            .ApplyClientFilters([hiddenIp], [statsOnlyIp], [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)])
            .Select(d => d.Id)
            .ToListAsync();
        var addedToCache = await context.Downloads
            .ApplyClientFilters([], [statsOnlyIp], [])
            .Select(d => d.Id)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal([ordinary.Id], served);
        Assert.Equal(new[] { hidden.Id, prefill.Id, ordinary.Id }.Order(), addedToCache);
    }

    [Fact]
    public void AStoredRuleWithNoTrafficFieldLoadsAsAWholeClientRule()
    {
        WriteState(new AppState { ExcludedClientRules = [new ClientExclusionRule { Ip = "10.0.0.5", Mode = ClientExclusionModes.Hide }] });
        var path = Path.Combine(_root, nameof(IPathResolver.GetStateDirectory), "state.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Traffic\":\"all\",", "", StringComparison.Ordinal));
        Assert.DoesNotContain("\"Traffic\"", File.ReadAllText(path), StringComparison.Ordinal);

        var state = StateTestMethods.CreateStateService(_root);

        var rule = Assert.Single(state.GetExcludedClientRules());
        Assert.Equal(("10.0.0.5", ClientExclusionTraffic.All), (rule.Ip, rule.Traffic));
        Assert.Equal(["10.0.0.5"], state.GetHiddenClientIps());
        Assert.Empty(state.GetPrefillRules());
    }

    [Fact]
    public async Task AHiddenPrefillRuleLeavesPrefillBytesOutOfTheSpeedHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var recent = DateTime.UtcNow.AddMinutes(-10);
        await SeedAsync(database, Row("steam", Device, 1000, 200, start: recent), Row("steam", Container, 0, 5000, isPrefill: true, start: recent));
        var controller = new SpeedsController(null!, database.Factory, State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]), null!);

        var result = await controller.GetSpeedHistoryAsync(60);

        var history = Assert.IsType<SpeedHistorySnapshot>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1200, history.TotalBytes);
        Assert.Equal(1, history.TotalSessions);
    }

    [Fact]
    public async Task AHiddenPrefillRuleLeavesPrefillRowsOutOfTheListWithEventTags()
    {
        await using var database = await TestDatabase.CreateAsync();
        var device = Row("steam", Device, 1000, 200);
        await SeedAsync(database, device, Row("steam", Container, 0, 5000, isPrefill: true));
        var controller = DownloadsOver(database, State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]));

        var result = await controller.GetWithEventsAsync(startTime: new DateTimeOffset(Start.AddHours(-1)).ToUnixTimeSeconds());

        var rows = Assert.IsType<List<DownloadWithEventTags>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([device.Id], rows.Select(row => row.Download.Id));
    }

    [Fact]
    public async Task AHiddenPrefillRuleLeavesPrefillRowsOutOfTheRetroList()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("steam", Container, 0, 5000, isPrefill: true));
        var controller = DownloadsOver(database, State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]));

        var result = await controller.GetRetroDownloadsAsync(new RetroDownloadQuery());

        var retro = Assert.IsType<RetroDownloadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, retro.TotalDownloads);
        Assert.Equal(Device, Assert.Single(retro.Items).ClientIp);
    }

    [Fact]
    public async Task AHiddenPrefillRuleLeavesPrefillRowsOutOfAGroupsMemberList()
    {
        await using var database = await TestDatabase.CreateAsync();
        var device = Row("steam", Device, 1000, 200);
        var prefill = Row("steam", Container, 0, 5000, isPrefill: true);
        await SeedAsync(database, device, prefill);
        var controller = DownloadsOver(database, State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]));

        var result = await controller.GetByIdsAsync(new BatchDownloadEventsRequest { DownloadIds = [device.Id, prefill.Id] });

        var rows = Assert.IsType<List<Download>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal([device.Id], rows.Select(row => row.Id));
    }

    [Fact]
    public async Task AHiddenPrefillRuleLeavesPrefillBytesOutOfEventCompareServedButNotMissed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var device = Row("steam", Device, 1000, 200);
        var prefill = Row("steam", Container, 0, 5000, isPrefill: true);
        await SeedAsync(database, device, prefill);
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.Events.Add(new Event { Id = 7, Name = "LAN Party", StartTimeUtc = Start, EndTimeUtc = Start.AddHours(1), CreatedAtUtc = Start, ColorIndex = 1 });
            seed.EventDownloads.AddRange(
                new EventDownload { EventId = 7, DownloadId = device.Id, TaggedAtUtc = Start },
                new EventDownload { EventId = 7, DownloadId = prefill.Id, TaggedAtUtc = Start });
            await seed.SaveChangesAsync();
        }
        var service = BatchOver(database);
        CacheScanGateHarness.SetField(service, "_stateRepository", State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]));

        var compare = await service.GetEventCompareAsync([7], CancellationToken.None);

        var series = Assert.Single(compare.Series);
        Assert.Equal(1200, series.Served.Sum() ?? 0, 0);
        Assert.Equal(5200, series.Missed.Sum() ?? 0, 0);
    }

    [Fact]
    public async Task AHiddenPrefillRuleLeavesPrefillBytesOutOfTheServiceStats()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("steam", Container, 0, 5000, isPrefill: true));
        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        CacheScanGateHarness.SetField(controller, "_context", new AppDbContext(database.Options));
        CacheScanGateHarness.SetField(controller, "_stateRepository", State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]));

        var result = await controller.GetServicesAsync(ct: CancellationToken.None);

        var steam = Assert.Single(Assert.IsType<List<ServiceStats>>(Assert.IsType<OkObjectResult>(result.Result).Value));
        Assert.Equal(1200, steam.TotalBytes);
    }

    [Fact]
    public async Task AnExcludePrefillRuleKeepsTheDownloadsInListsAndTakesThemOutOfStats()
    {
        await using var database = await TestDatabase.CreateAsync();
        var prefill = Row("steam", Container, 0, 5000, isPrefill: true);
        await SeedAsync(database, Row("steam", Device, 1000, 200), prefill);
        var state = State([CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Exclude)]);

        Assert.IsType<OkObjectResult>((await DownloadByIdAsync(database, state, prefill.Id)).Result);
        var stats = await StatsDashboardAsync(database, state);
        Assert.Equal(1200, stats.TotalServed);
        Assert.Equal(5200, stats.TotalAddedToCache);
    }

    [Fact]
    public void ATwoRulePrefillFilterTranslatesToSql()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=prefill_rule_translation_smoke_test")
                .Options);

        // ToQueryString throws when any part of the filter would need client evaluation.
        var sql = context.Downloads
            .ApplyClientFilters(
            [],
            [],
            [
                CacheScanGateHarness.PrefillRule("10.0.0.5", ClientExclusionModes.Hide, PrefillPlatform.Steam, PrefillPlatform.Xbox),
                CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Exclude)
            ])
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllThreeClientFilterPartsTranslateToSql()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=client_filters_translation_smoke_test")
                .Options);

        // ToQueryString throws when any part of the filter would need client evaluation.
        var sql = context.Downloads
            .ApplyClientFilters(
                ["10.0.0.7"],
                ["10.0.0.8"],
                [CacheScanGateHarness.PrefillRule("10.0.0.5", ClientExclusionModes.Hide, PrefillPlatform.Steam)])
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUpgradedInstallWithTheSwitchOnReadsOneHiddenPrefillRuleAndKeepsItsStart()
    {
        var since = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        WriteState(new AppState { SeparatePrefillTraffic = true, SeparatePrefillTrafficSinceUtc = since });

        var state = StateTestMethods.CreateStateService(_root);
        var migrated = Assert.Single(state.GetExcludedClientRules());
        Assert.Equal(CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide).RuleKey, migrated.RuleKey);
        Assert.Equal(since, state.GetPrefillMarkingSinceUtc());

        // An unrelated save while the switch is still on stores the migrated rule; the next load
        // must not read a second copy of it.
        state.SetEvictedDataMode("hide");
        Assert.Single(StateTestMethods.CreateStateService(_root).GetExcludedClientRules());

        state.SetExcludedClientRules(state.GetExcludedClientRules());
        Assert.False(state.GetState().SeparatePrefillTraffic);
        Assert.Equal(since, state.GetPrefillMarkingSinceUtc());
        var reloaded = StateTestMethods.CreateStateService(_root);
        Assert.Single(reloaded.GetHiddenPrefillRules());
        Assert.Equal(since, reloaded.GetPrefillMarkingSinceUtc());
    }

    [Fact]
    public void DeletingTheMigratedRuleTurnsPrefillHidingOffForGood()
    {
        WriteState(new AppState { SeparatePrefillTraffic = true, SeparatePrefillTrafficSinceUtc = Start });
        var state = StateTestMethods.CreateStateService(_root);

        state.SetExcludedClientRules([new ClientExclusionRule { Ip = "10.0.0.5", Mode = ClientExclusionModes.Hide }]);

        foreach (var read in new IStateService[] { state, StateTestMethods.CreateStateService(_root) })
        {
            var kept = Assert.Single(read.GetExcludedClientRules());
            Assert.Equal(("10.0.0.5", ClientExclusionTraffic.All), (kept.Ip, kept.Traffic));
            Assert.Empty(read.GetHiddenPrefillRules());
            Assert.Null(read.GetPrefillMarkingSinceUtc());
        }
    }

    [Fact]
    public async Task WithSeparationOnTheStatsClientListLeavesThePrefillMachineOutButThePickersKeepIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("steam", Container, 0, 5000, isPrefill: true));

        List<ClientExclusionRule> rules = [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)];
        var stats = await StatsClientsAsync(database, includeExcluded: false, rules);
        var pickers = await StatsClientsAsync(database, includeExcluded: true, rules);

        Assert.Equal([Device], stats.Select(client => client.ClientIp));
        Assert.Equal([Device, Container], pickers.Select(client => client.ClientIp).Order());
    }

    [Fact]
    public async Task AFirstPrefillRuleStartsANewWindowAndClearsMarksFromAnEarlierOne()
    {
        await using var database = await TestDatabase.CreateAsync();
        var earlier = Row("steam", Container, 0, 5000, isPrefill: true);
        await SeedAsync(database, Row("steam", Device, 1000, 200), earlier);
        var state = StateTestMethods.CreateStateService(_root);
        var (controller, _) = ExclusionsController(
            new AppDbContext(database.Options),
            state,
            new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance));
        var withRule = new UpdateStatsExclusionsRequest { Rules = [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)] };

        await controller.UpdateExcludedClientsAsync(withRule);
        var first = state.GetPrefillMarkingSinceUtc();

        await using (var read = new AppDbContext(database.Options))
        {
            Assert.False(await read.Downloads.Where(d => d.Id == earlier.Id).Select(d => d.IsPrefill).SingleAsync());
        }
        Assert.IsType<OkObjectResult>((await DownloadByIdAsync(database, state, earlier.Id)).Result);
        Assert.NotNull(first);

        await controller.UpdateExcludedClientsAsync(new UpdateStatsExclusionsRequest { Rules = [] });
        Assert.Null(state.GetPrefillMarkingSinceUtc());

        await controller.UpdateExcludedClientsAsync(withRule);
        Assert.True(state.GetPrefillMarkingSinceUtc() > first);
    }

    /// <summary>
    /// One metrics refresh over the database per setting, then one collection of the global gauges,
    /// as an exporter scrape reads them. The storage read fails inside its own catch and leaves
    /// those gauges at zero, which no assert reads.
    /// </summary>
    [Fact]
    public async Task AMetricsScrapeFollowsThePrefillSetting()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database, Row("steam", Device, 1000, 200), Row("steam", Container, 0, 5000, isPrefill: true));
        string[] names = ["lancache_downloads_total", "lancache_bytes_served_total", "lancache_cache_miss_bytes_total"];

        foreach (var (separate, downloads, served) in new[] { (true, 1L, 1200L), (false, 2L, 6200L) })
        {
            var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => (object)new List<string>(),
                nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
                nameof(IStateService.GetPrefillRules) => separate
                    ? new List<ClientExclusionRule> { CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide) }
                    : new List<ClientExclusionRule>(),
                nameof(IStateService.GetTopGameCount) => 10,
                _ => throw new NotSupportedException(method.Name)
            });
            using var services = new ServiceCollection()
                .AddSingleton(state)
                .AddSingleton((CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService)))
                .AddScoped(_ => new AppDbContext(database.Options))
                .BuildServiceProvider();
            using var metrics = new LancacheMetricsService(
                services,
                NullLogger<LancacheMetricsService>.Instance,
                new ConfigurationBuilder().Build(),
                Stopwatch.GetTimestamp,
                () => new DownloadSpeedSnapshot());
            using var scope = services.CreateScope();
            await (Task)typeof(LancacheMetricsService)
                .GetMethod(
                    "ExecuteWorkAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    [typeof(IServiceProvider), typeof(CancellationToken)])!
                .Invoke(metrics, [scope.ServiceProvider, CancellationToken.None])!;

            var meter = (Meter)typeof(LancacheMetricsService)
                .GetField("_meter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(metrics)!;
            var read = new Dictionary<string, long>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && names.Contains(instrument.Name))
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => read[instrument.Name] = value);
            listener.Start();
            listener.RecordObservableInstruments();

            Assert.Equal(downloads, read["lancache_downloads_total"]);
            Assert.Equal(served, read["lancache_bytes_served_total"]);
            Assert.Equal(5200, read["lancache_cache_miss_bytes_total"]);
        }
    }

    /// <summary>
    /// Ingest runs in a child process, so the order is read from the source: the marking runs before
    /// the refresh the dashboard reloads on, and its own catch closes before the refresh and the
    /// checkpoint save, so a failed marking costs neither.
    /// </summary>
    [Fact]
    public void IngestMarksPrefillRowsBeforeTheRefreshAndAFailedMarkingStillRefreshes()
    {
        var source = File.ReadAllText(Path.Combine(
            EndpointAuthorizationHost.FindRepositoryRoot(),
            "Api", "LancacheManager", "Infrastructure", "Services", "Rust", "RustLogProcessorService.cs"));

        var mark = source.IndexOf(".MarkPrefillRowsAsync(since, datasourceName, riotSuccess ? ingestStartedUtc : null, CancellationToken.None);", StringComparison.Ordinal);
        var markCatch = source.IndexOf("Prefill row marking failed after an accepted log-processing checkpoint", StringComparison.Ordinal);
        var refresh = source.IndexOf("await NotifyCommittedDownloadsAsync(finalProgress);", StringComparison.Ordinal);
        var save = source.IndexOf("await SaveProcessingSourceAsync(", refresh, StringComparison.Ordinal);

        Assert.True(mark >= 0, "ingest must mark prefill rows");
        Assert.True(markCatch > mark, "a failed marking must be caught on its own");
        Assert.True(refresh > markCatch, "the refresh must follow the marking and its catch");
        Assert.True(save > refresh, "the checkpoint save must follow the refresh");
    }

    private static Download Row(
        string service,
        string clientIp,
        long hitBytes,
        long missBytes,
        bool isPrefill = false,
        DateTime? start = null) => new()
        {
            Service = service,
            ClientIp = clientIp,
            StartTimeUtc = start ?? Start,
            EndTimeUtc = (start ?? Start).AddMinutes(5),
            CacheHitBytes = hitBytes,
            CacheMissBytes = missBytes,
            IsPrefill = isPrefill
        };

    private static PrefillSession Session(string sessionId, PrefillPlatform platform) => new()
    {
        SessionId = sessionId,
        Platform = platform,
        CreatedAtUtc = Start.AddDays(-3),
        LastLoginAtUtc = Start.AddDays(-3),
        ExpiresAtUtc = Start.AddDays(3)
    };

    private static PrefillRun Run(string sessionId, DateTime startedAtUtc, DateTime? completedAtUtc, string address = Container) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = sessionId,
        DaemonInstanceId = Guid.NewGuid().ToString(),
        OptionsJson = "{}",
        SnapshotJson = "{}",
        ClientAddressesJson = $"[\"{address}\"]",
        StartedAtUtc = startedAtUtc,
        CompletedAtUtc = completedAtUtc
    };

    internal static async Task SeedAsync(TestDatabase database, params Download[] rows)
    {
        await using var seed = new AppDbContext(database.Options);
        seed.Downloads.AddRange(rows);
        await seed.SaveChangesAsync();
    }

    private static DashboardBatchService BatchOver(TestDatabase database)
    {
        var service = (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        CacheScanGateHarness.SetField(service, "_dbContextFactory", database.Factory);
        CacheScanGateHarness.SetField(service, "_readActivity", new Func<DownloadSpeedSnapshot>(() => new DownloadSpeedSnapshot { StreamId = "prefill-tests" }));
        return service;
    }

    private static Task<object> InvokeSectionAsync(TestDatabase database, string name, object?[] arguments)
        => (Task<object>)typeof(DashboardBatchService)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(BatchOver(database), arguments)!;

    private static async Task<DashboardStatsResponse> BatchDashboardAsync(
        TestDatabase database,
        List<ClientExclusionRule> prefillRules,
        List<string>? statsExcluded = null)
        => Assert.IsType<DashboardStatsResponse>(await InvokeSectionAsync(database, "GetDashboardStatsAsync",
            [null, null, new List<long>(), null, new List<string>(), "show", statsExcluded ?? [], prefillRules, CancellationToken.None]));

    private static async Task<DashboardStatsResponse> StatsDashboardAsync(TestDatabase database, IStateService state)
    {
        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        CacheScanGateHarness.SetField(controller, "_context", new AppDbContext(database.Options));
        CacheScanGateHarness.SetField(controller, "_stateRepository", state);
        CacheScanGateHarness.SetField(controller, "_readActivity", new Func<DownloadSpeedSnapshot>(() => new DownloadSpeedSnapshot { StreamId = "prefill-tests" }));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.DashboardStatsAsync(startTime: null, endTime: null, eventId: null, ct: CancellationToken.None);
        return Assert.IsType<DashboardStatsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<List<ClientStatsWithGroup>> StatsClientsAsync(
        TestDatabase database,
        bool includeExcluded,
        List<ClientExclusionRule> prefillRules)
    {
        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        CacheScanGateHarness.SetField(controller, "_context", new AppDbContext(database.Options));
        CacheScanGateHarness.SetField(controller, "_stateRepository", State(prefillRules));
        CacheScanGateHarness.SetField(controller, "_apiOptions", Options.Create(new ApiOptions()));
        CacheScanGateHarness.SetField(controller, "_clientGroupsRepository", CacheScanGateHarness.CreateProxy<IClientGroupsService>(
            (_, _) => Task.FromResult(new Dictionary<string, ClientGroupAssignment>())));
        CacheScanGateHarness.SetField(controller, "_clientHostnameService", CacheScanGateHarness.CreateProxy<IClientHostnameService>(
            (_, _) => Task.FromResult(new ClientHostnameLookupOutcome(new Dictionary<string, string>(), ClientHostnamesReason.None))));

        var result = await controller.GetClientsAsync(includeExcluded: includeExcluded, ct: CancellationToken.None);
        return Assert.IsType<List<ClientStatsWithGroup>>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    /// <summary>
    /// Answers the members the stats reads use; any other member fails the test, so a new state
    /// read the code under test starts making shows up here instead of returning a silent default.
    /// </summary>
    private static IStateService State(List<ClientExclusionRule> prefillRules, List<string>? statsExcluded = null)
        => CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => (object)new List<string>(),
            nameof(IStateService.GetStatsExcludedOnlyClientIps) => statsExcluded ?? [],
            nameof(IStateService.GetPrefillRules) => prefillRules,
            nameof(IStateService.GetHiddenPrefillRules) => prefillRules
                .Where(rule => rule.Mode == ClientExclusionModes.Hide)
                .ToList(),
            // The evicted display mode is the one string the stats action reads.
            _ when method.ReturnType == typeof(string) => "show",
            _ => throw new NotSupportedException(method.Name)
        });

    private static Task<ActionResult<DownloadWithEventsResponse>> DownloadByIdAsync(TestDatabase database, IStateService state, long id)
    {
        var downloads = (DownloadsController)RuntimeHelpers.GetUninitializedObject(typeof(DownloadsController));
        CacheScanGateHarness.SetField(downloads, "_context", new AppDbContext(database.Options));
        CacheScanGateHarness.SetField(downloads, "_stateRepository", state);
        return downloads.GetByIdAsync(id);
    }

    private static DownloadsController DownloadsOver(TestDatabase database, IStateService state)
    {
        var context = new AppDbContext(database.Options);
        return new DownloadsController(context, state, new EventsService(context, NullLogger<EventsService>.Instance, state), NullLogger<DownloadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static (StatsController Controller, RecordingNotifications Recorder) ExclusionsController(
        AppDbContext context,
        IStateService state,
        PrefillSessionService prefillSessions)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        CacheScanGateHarness.SetField(controller, "_context", context);
        CacheScanGateHarness.SetField(controller, "_stateRepository", state);
        CacheScanGateHarness.SetField(controller, "_prefillSessions", prefillSessions);
        CacheScanGateHarness.SetField(controller, "_notifications", notifications);
        return (controller, (RecordingNotifications)(object)notifications);
    }

    private void WriteState(AppState state)
    {
        var directory = Path.Combine(_root, nameof(IPathResolver.GetStateDirectory));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "state.json"), System.Text.Json.JsonSerializer.Serialize(state));
    }

    private static T Property<T>(object target, string name)
        => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
}
