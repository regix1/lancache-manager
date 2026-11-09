using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// With a prefill rule, the metrics endpoint drops prefill rows from the served figures but keeps
/// them in the miss-bytes total and the cache growth, since a prefill still fills the cache.
/// Adding or removing a Hide prefill rule also has to change the speed snapshot's revision so the
/// browser accepts the corrected body.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class MetricsPrefillSeparationTests
{
    private const string Device = "10.0.0.1";
    private const string Container = "172.17.0.2";

    [Fact]
    public async Task WithSeparationOnPrefillBytesStayInMissTotalAndGrowthButLeaveServedFigures()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateTime.UtcNow.Date;
        await PrefillTrafficSeparationTests.SeedAsync(
            database,
            Row(Device, 1000, 200, today.AddDays(-3).AddHours(12), isPrefill: false),
            Row(Device, 500, 100, today.AddDays(-1).AddHours(12), isPrefill: false),
            Row(Container, 0, 5000, today.AddDays(-1).AddHours(12), isPrefill: true));

        using var separated = await RunCycleAsync(database, separate: true);
        using var together = await RunCycleAsync(database, separate: false);

        // Served figures leave the prefill row out: 1000 + 200 + 500 + 100.
        Assert.Equal(1800L, Field<long>(separated, "_totalBytesServed"));
        Assert.Equal(1500L, Field<long>(separated, "_cacheHitBytesTotal"));
        Assert.Equal(2L, Field<long>(separated, "_totalDownloads"));
        // Added to cache keeps it: 200 + 100 + 5000.
        Assert.Equal(5300L, Field<long>(separated, "_cacheMissBytesTotal"));
        // Growth by day is 200 and 5100 over two days; without the prefill row it is 200 and 100.
        Assert.Equal(2650L, Field<long>(separated, "_cacheGrowthDailyBytes"));

        Assert.Equal(6800L, Field<long>(together, "_totalBytesServed"));
        Assert.Equal(3L, Field<long>(together, "_totalDownloads"));
        Assert.Equal(5300L, Field<long>(together, "_cacheMissBytesTotal"));
        Assert.Equal(2650L, Field<long>(together, "_cacheGrowthDailyBytes"));
    }

    [Fact]
    public async Task WithSeparationOnAndOnlyPrefillRowsTheMissTotalStillCountsThem()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateTime.UtcNow.Date;
        await PrefillTrafficSeparationTests.SeedAsync(database, Row(Container, 0, 5000, today.AddDays(-1).AddHours(12), isPrefill: true));

        using var separated = await RunCycleAsync(database, separate: true);

        Assert.Equal(0L, Field<long>(separated, "_totalBytesServed"));
        Assert.Equal(5000L, Field<long>(separated, "_cacheMissBytesTotal"));
    }

    [Fact]
    public void TogglingPrefillSeparationChangesTheSpeedSnapshotRevision()
    {
        var separate = false;
        var raw = new DownloadSpeedSnapshot
        {
            StreamId = "prefill-toggle",
            Revision = 9,
            IsAvailable = true,
            WindowSeconds = 4,
            GameSpeeds = [new GameSpeedInfo { ClientIp = Device, Service = "steam", BytesPerSecond = 100 }]
        };
        var tracker = CacheScanGateHarness.TrackerWith(raw, []);
        CacheScanGateHarness.SetField(tracker, "_services", new ServiceCollection().BuildServiceProvider());
        CacheScanGateHarness.SetField(tracker, "_stateService", CacheScanGateHarness.CreateProxy<IStateService>(
            (method, _) => method.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => new List<string>(),
                nameof(IStateService.GetEvictedDataMode) => "show",
                nameof(IStateService.GetHiddenPrefillRules) => PrefillRules(separate),
                _ => throw new NotSupportedException(method.Name)
            }));

        var first = tracker.GetCurrentSnapshot().Revision;
        var unchanged = tracker.GetCurrentSnapshot().Revision;
        separate = true;
        var turnedOn = tracker.GetCurrentSnapshot().Revision;
        separate = false;
        var turnedOff = tracker.GetCurrentSnapshot().Revision;

        Assert.Equal(first, unchanged);
        Assert.True(turnedOn > unchanged);
        Assert.True(turnedOff > turnedOn);
    }

    private static Download Row(string clientIp, long hitBytes, long missBytes, DateTime start, bool isPrefill) => new()
    {
        Service = "steam",
        ClientIp = clientIp,
        StartTimeUtc = start,
        EndTimeUtc = start.AddMinutes(5),
        CacheHitBytes = hitBytes,
        CacheMissBytes = missBytes,
        IsPrefill = isPrefill
    };

    [Fact]
    public async Task WhenTheVisibleSetEmptiesEveryServedFigureDropsToZero()
    {
        await using var database = await TestDatabase.CreateAsync();
        var today = DateTime.UtcNow.Date;
        await PrefillTrafficSeparationTests.SeedAsync(database, Row(Container, 300, 5000, today.AddDays(-1).AddHours(12), isPrefill: true));

        // The first refresh counts the prefill row; turning separation on empties the served set.
        using var service = await RunCycleAsync(database, separate: false);
        Assert.Equal(5300L, Field<long>(service, "_totalBytesServed"));
        await RefreshAsync(service, database, separate: true);

        Assert.Equal(0L, Field<long>(service, "_totalDownloads"));
        Assert.Equal(0L, Field<long>(service, "_totalBytesServed"));
        Assert.Equal(0L, Field<long>(service, "_cacheHitBytesTotal"));
        Assert.Equal(0L, Field<long>(service, "_largestDownloadBytes"));
        Assert.Equal(0L, Field<long>(service, "_averageDownloadSizeBytes"));
        Assert.Equal(0d, BitConverter.Int64BitsToDouble(Field<long>(service, "_cacheHitRatioBits")));
    }

    private static async Task<LancacheMetricsService> RunCycleAsync(TestDatabase database, bool separate)
    {
        var service = new LancacheMetricsService(
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<LancacheMetricsService>.Instance,
            new ConfigurationBuilder().Build(),
            Stopwatch.GetTimestamp,
            () => new DownloadSpeedSnapshot { StreamId = "metrics-prefill-tests" });
        await RefreshAsync(service, database, separate);
        return service;
    }

    /// <summary>
    /// Runs one metrics refresh against the database. The cache service is never built: its
    /// storage read sits behind the refresh's own catch, and the figures under test come from the
    /// download queries.
    /// </summary>
    private static async Task RefreshAsync(LancacheMetricsService service, TestDatabase database, bool separate)
    {
        using var context = new AppDbContext(database.Options);
        var cacheService = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        var scoped = new ServiceCollection()
            .AddSingleton(context)
            .AddSingleton(cacheService)
            .AddSingleton(CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => new List<string>(),
                nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
                nameof(IStateService.GetPrefillRules) => PrefillRules(separate),
                nameof(IStateService.GetTopGameCount) => 10,
                _ => throw new NotSupportedException(method.Name)
            }))
            .BuildServiceProvider();

        var update = typeof(LancacheMetricsService).GetMethod("UpdateMetricsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)update.Invoke(service, new object[] { scoped, CancellationToken.None })!;
    }

    // Separation on is a Hide prefill rule for any address and every service.
    private static List<ClientExclusionRule> PrefillRules(bool separate) => separate
        ? [CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide)]
        : [];

    private static T Field<T>(object target, string name)
        => (T)typeof(LancacheMetricsService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
}
