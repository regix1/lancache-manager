using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// The services list is aggregated from Downloads, so a row tagged <c>localhost</c> or
/// <c>ip-address</c> reached it as a service name with nothing behind it. What keeps those two out
/// is the service identifier and never the byte total: a real service that has transferred nothing
/// yet is still a row worth showing.
/// </summary>
public sealed class PlaceholderServiceFilterTests
{
    private static AppDbContext Seed(string dbName, params Download[] downloads)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        var context = new AppDbContext(options);
        context.Downloads.AddRange(downloads);
        context.SaveChanges();
        return context;
    }

    private static Download Row(string service, long hitBytes) => new()
    {
        Service = service,
        ClientIp = "10.0.0.1",
        StartTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndTimeUtc = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
        CacheHitBytes = hitBytes
    };

    [Fact]
    public async Task ServiceStatsQueryDropsPlaceholderServices()
    {
        await using var context = Seed(
            nameof(ServiceStatsQueryDropsPlaceholderServices),
            Row("steam", 1024),
            Row("localhost", 0),
            Row("ip-address", 0));

        var services = await DashboardBatchService.ServiceStatsQuery(context.Downloads).ToListAsync();

        Assert.Equal("steam", Assert.Single(services).Service);
    }

    [Fact]
    public async Task ServiceStatsQueryKeepsAServiceThatTransferredNothing()
    {
        await using var context = Seed(
            nameof(ServiceStatsQueryKeepsAServiceThatTransferredNothing),
            Row("wsus", 0));

        var services = await DashboardBatchService.ServiceStatsQuery(context.Downloads).ToListAsync();

        Assert.Equal("wsus", Assert.Single(services).Service);
    }

    [Fact]
    public async Task PlaceholderServicesAreMatchedRegardlessOfCase()
    {
        await using var context = Seed(
            nameof(PlaceholderServicesAreMatchedRegardlessOfCase),
            Row("Localhost", 0),
            Row("IP-Address", 0));

        var services = await DashboardBatchService.ServiceStatsQuery(context.Downloads).ToListAsync();

        Assert.Empty(services);
    }
}
