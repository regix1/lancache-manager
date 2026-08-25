using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// GetCacheSnapshotAsync now stamps every return path with the snapshot scheduler's own
/// NextRunUtc, including both HasData=false early returns. The default time range is what the
/// dashboard actually requests on first load, so a viewer who has never picked a range still
/// needs to see when the next snapshot lands rather than a blank countdown.
/// </summary>
public sealed class DashboardBatchServiceCacheSnapshotTests
{
    /// <summary>
    /// Builds a DashboardBatchService whose only live dependency is a CacheSnapshotService
    /// pointed at an in-memory database, bypassing both constructors via reflection the same
    /// way GamesControllerGameRemovalQueueTests builds a bare GameCacheDetectionService.
    /// GetCacheSnapshotAsync only reads _cacheSnapshotService, so nothing else needs a value.
    /// </summary>
    private static DashboardBatchService BuildService(string dbName, DateTime? nextRunUtc)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();

        var snapshotService =
            (CacheSnapshotService)RuntimeHelpers.GetUninitializedObject(typeof(CacheSnapshotService));
        typeof(CacheSnapshotService)
            .GetField("_scopeFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(snapshotService, provider.GetRequiredService<IServiceScopeFactory>());
        typeof(CacheSnapshotService)
            .GetProperty("NextRunUtc")!
            .GetSetMethod(nonPublic: true)!
            .Invoke(snapshotService, [nextRunUtc]);

        var batchService =
            (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        typeof(DashboardBatchService)
            .GetField("_cacheSnapshotService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(batchService, snapshotService);
        return batchService;
    }

    private static async Task SeedSnapshotAsync(string dbName, DateTime timestampUtc, long usedSize, long totalSize)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        await using var context = new AppDbContext(options);
        context.CacheSnapshots.Add(new CacheSnapshot
        {
            TimestampUtc = timestampUtc,
            UsedCacheSize = usedSize,
            TotalCacheSize = totalSize
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Calls the private GetCacheSnapshotAsync directly; it is not part of the public interface.</summary>
    private static async Task<CacheSnapshotResponse> InvokeAsync(
        DashboardBatchService service, long? startTime, long? endTime)
    {
        var method = typeof(DashboardBatchService).GetMethod(
            "GetCacheSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<object>)method.Invoke(service, [startTime, endTime, CancellationToken.None])!;
        var result = await task;
        return Assert.IsType<CacheSnapshotResponse>(result);
    }

    [Fact]
    public async Task DefaultTimeRange_HasNoData_StillReportsNextSnapshotUtc()
    {
        var dbName = $"cache-snapshot-default-{Guid.NewGuid():N}";
        var nextRun = new DateTime(2026, 8, 25, 3, 0, 0, DateTimeKind.Utc);
        var service = BuildService(dbName, nextRun);

        var response = await InvokeAsync(service, startTime: null, endTime: null);

        Assert.False(response.HasData);
        Assert.Equal(nextRun, response.NextSnapshotUtc);
    }

    [Fact]
    public async Task RangeWithNoSnapshotHistory_HasNoData_StillReportsNextSnapshotUtc()
    {
        var dbName = $"cache-snapshot-empty-{Guid.NewGuid():N}";
        var nextRun = new DateTime(2026, 8, 25, 3, 0, 0, DateTimeKind.Utc);
        var service = BuildService(dbName, nextRun);

        var rangeStart = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var rangeEnd = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var response = await InvokeAsync(service, rangeStart, rangeEnd);

        Assert.False(response.HasData);
        Assert.Equal(nextRun, response.NextSnapshotUtc);
    }

    [Fact]
    public async Task RangeWithSnapshotHistory_ReturnsDataAlongsideNextSnapshotUtc()
    {
        var dbName = $"cache-snapshot-data-{Guid.NewGuid():N}";
        var nextRun = new DateTime(2026, 8, 25, 3, 0, 0, DateTimeKind.Utc);
        var rangeStart = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
        var rangeEnd = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);
        await SeedSnapshotAsync(dbName, rangeStart.AddHours(1), usedSize: 100, totalSize: 1000);
        await SeedSnapshotAsync(dbName, rangeStart.AddHours(2), usedSize: 200, totalSize: 1000);

        var service = BuildService(dbName, nextRun);
        var response = await InvokeAsync(
            service,
            new DateTimeOffset(rangeStart).ToUnixTimeSeconds(),
            new DateTimeOffset(rangeEnd).ToUnixTimeSeconds());

        Assert.True(response.HasData);
        Assert.Equal(2, response.SnapshotCount);
        Assert.Equal(nextRun, response.NextSnapshotUtc);
    }
}
