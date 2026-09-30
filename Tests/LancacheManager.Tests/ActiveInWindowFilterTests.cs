using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class ActiveInWindowFilterTests
{
    [Fact]
    public async Task ApplyTimeRangeKeepsDownloadsActiveInsideTheWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var day = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
        var start = day.AddHours(12);
        var end = day.AddHours(13);
        context.Downloads.AddRange(
            Row(1, "long", day.AddHours(10), day.AddHours(13)),
            Row(2, "before", day.AddHours(9), day.AddHours(9.5)),
            Row(3, "inside", day.AddHours(12.1), day.AddHours(12.3)),
            Row(4, "unset", day.AddHours(12.5), day.AddHours(11.5)));
        await context.SaveChangesAsync();

        var selected = await context.Downloads
            .ApplyTimeRange(UnixSeconds(start), UnixSeconds(end))
            .Select(d => d.ClientIp)
            .OrderBy(ip => ip)
            .ToListAsync();
        var oldPredicate = await context.Downloads
            .Where(d => d.StartTimeUtc >= start && d.StartTimeUtc <= end)
            .Select(d => d.ClientIp)
            .ToListAsync();
        var overlapOnly = await context.Downloads
            .Where(d => d.EndTimeUtc >= start && d.StartTimeUtc <= end)
            .Select(d => d.ClientIp)
            .ToListAsync();

        Assert.Equal(new[] { "inside", "long", "unset" }, selected);
        Assert.DoesNotContain("long", oldPredicate);
        Assert.Contains("long", overlapOnly);
        Assert.DoesNotContain("unset", overlapOnly);
        Assert.DoesNotContain("before", selected);
    }

    [Fact]
    public async Task AutoTagIncludesDownloadsActiveDuringTheEventAndAfterItsCreation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var evt = new Event
        {
            Id = 1,
            Name = "LAN Party",
            StartTimeUtc = now.AddMinutes(-30),
            EndTimeUtc = now.AddHours(2),
            CreatedAtUtc = now.AddMinutes(-30),
            ColorIndex = 1
        };
        context.Events.Add(evt);
        context.Downloads.AddRange(
            Row(10, "spanning", now.AddMinutes(-40), now.AddMinutes(-20)),
            Row(11, "inside", now.AddMinutes(-10), now.AddMinutes(-5)),
            Row(12, "before", now.AddHours(-3), now.AddHours(-2)));
        await context.SaveChangesAsync();
        var oldPredicate = await context.Downloads
            .Where(d => d.StartTimeUtc >= evt.StartTimeUtc && d.StartTimeUtc <= evt.EndTimeUtc)
            .Where(d => d.StartTimeUtc >= evt.CreatedAtUtc)
            .Select(d => d.Id)
            .ToListAsync();
        var service = new EventsService(context, NullLogger<EventsService>.Instance);

        var count = await service.AutoTagActiveEventsAsync();
        var tagged = await context.EventDownloads
            .Where(ed => ed.EventId == evt.Id)
            .Select(ed => ed.DownloadId)
            .OrderBy(id => id)
            .ToListAsync();

        Assert.Equal(new[] { 11L }, oldPredicate);
        Assert.Equal(2, count);
        Assert.Equal(new[] { 10L, 11L }, tagged);
    }

    [Fact]
    public void DailyGrowthQueryTranslatesTheConditionalFirstDayBucket()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=daily_growth_translation_smoke_test")
                .Options);
        var sevenDaysAgo = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

        var sql = LancacheMetricsService.DailyGrowthQuery(context.Downloads, sevenDaysAgo)
            .ToQueryString();

        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("date_trunc", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DailyGrowthCountsAnOverlappingDownloadOnTheFirstDay()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var sevenDaysAgo = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        context.Downloads.Add(Row(
            20,
            "long",
            sevenDaysAgo.AddDays(-2),
            sevenDaysAgo.AddDays(4),
            missBytes: 700));
        await context.SaveChangesAsync();

        var growth = Assert.Single(await LancacheMetricsService
            .DailyGrowthQuery(context.Downloads, sevenDaysAgo)
            .ToListAsync());

        Assert.Equal(sevenDaysAgo.Date, growth.Date);
        Assert.Equal(700, growth.GrowthBytes);
    }

    [Fact]
    public async Task ClientLastActivityUsesTheEndOrFallsBackToTheStart()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var day = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
        context.Downloads.AddRange(
            Row(30, "long", day.AddHours(10), day.AddHours(13)),
            Row(31, "unset", day.AddHours(12), day.AddHours(11)));
        await context.SaveChangesAsync();

        var rows = await ClientStatsAggregationHelper.QueryIpAggregatesAsync(
            context.Downloads,
            CancellationToken.None);

        Assert.Equal(day.AddHours(13), Assert.Single(rows, row => row.ClientIp == "long").LastActivityUtc);
        Assert.Equal(day.AddHours(12), Assert.Single(rows, row => row.ClientIp == "unset").LastActivityUtc);
    }

    [Fact]
    public void DirectRangeAndLastActivitySitesUseTheActiveSpan()
    {
        var root = EndpointAuthorizationHost.FindRepositoryRoot();
        var downloads = File.ReadAllText(Path.Combine(
            root, "Api", "LancacheManager", "Controllers", "Downloads", "DownloadsController.cs"));
        var stats = File.ReadAllText(Path.Combine(
            root, "Api", "LancacheManager", "Controllers", "Dashboard", "StatsController.cs"));
        var clients = File.ReadAllText(Path.Combine(
            root, "Api", "LancacheManager", "Controllers", "Clients", "ClientHostnamesController.cs"));

        Assert.DoesNotContain(
            "d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate",
            downloads,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "query = query.Where(d => d.StartTimeUtc >= cutoffTime.Value);",
            stats,
            StringComparison.Ordinal);
        Assert.Contains(
            "LastActivityUtc = g.Max(d => d.EndTimeUtc > d.StartTimeUtc ? d.EndTimeUtc : d.StartTimeUtc)",
            clients,
            StringComparison.Ordinal);
    }

    private static long UnixSeconds(DateTime value) =>
        new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds();

    private static Download Row(
        long id,
        string clientIp,
        DateTime start,
        DateTime end,
        long missBytes = 100) => new()
    {
        Id = id,
        Service = "steam",
        ClientIp = clientIp,
        Datasource = "default",
        StartTimeUtc = start,
        EndTimeUtc = end,
        CacheMissBytes = missBytes,
        IsActive = false
    };
}
