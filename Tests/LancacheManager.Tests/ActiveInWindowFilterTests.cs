using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        var service = new EventsService(context, NullLogger<EventsService>.Instance, SeparatedPrefillState());

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
    public async Task EventDownloadsLeaveOutPrefillDownloadsWhenPrefillTrafficIsSeparated()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var now = DateTime.UtcNow;
        context.Events.Add(new Event
        {
            Id = 2,
            Name = "LAN Party",
            StartTimeUtc = now.AddHours(-1),
            EndTimeUtc = now.AddHours(1),
            CreatedAtUtc = now.AddHours(-1),
            ColorIndex = 1
        });
        var prefill = Row(40, "prefill", now.AddMinutes(-20), now.AddMinutes(-10));
        prefill.IsPrefill = true;
        context.Downloads.AddRange(prefill, Row(41, "player", now.AddMinutes(-20), now.AddMinutes(-10)));
        context.EventDownloads.AddRange(
            new EventDownload { EventId = 2, DownloadId = 40, TaggedAtUtc = now },
            new EventDownload { EventId = 2, DownloadId = 41, TaggedAtUtc = now });
        await context.SaveChangesAsync();
        var service = new EventsService(context, NullLogger<EventsService>.Instance, SeparatedPrefillState());

        var inWindow = await service.GetEventDownloadsAsync(2, taggedOnly: false);
        var tagged = await service.GetEventDownloadsAsync(2, taggedOnly: true);

        Assert.Equal(new[] { 41L }, inWindow.Select(d => d.Id));
        Assert.Equal(new[] { 41L }, tagged.Select(d => d.Id));
    }

    [Fact]
    public async Task EventDownloadsLeaveOutAHiddenClientsDownloads()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var now = DateTime.UtcNow;
        context.Events.Add(new Event
        {
            Id = 3,
            Name = "LAN Party",
            StartTimeUtc = now.AddHours(-1),
            EndTimeUtc = now.AddHours(1),
            CreatedAtUtc = now.AddHours(-1),
            ColorIndex = 1
        });
        context.Downloads.AddRange(
            Row(50, "hidden", now.AddMinutes(-20), now.AddMinutes(-10)),
            Row(51, "visible", now.AddMinutes(-20), now.AddMinutes(-10)));
        context.EventDownloads.AddRange(
            new EventDownload { EventId = 3, DownloadId = 50, TaggedAtUtc = now },
            new EventDownload { EventId = 3, DownloadId = 51, TaggedAtUtc = now });
        await context.SaveChangesAsync();
        var service = new EventsService(context, NullLogger<EventsService>.Instance, SeparatedPrefillState("hidden"));

        var inWindow = await service.GetEventDownloadsAsync(3, taggedOnly: false);
        var tagged = await service.GetEventDownloadsAsync(3, taggedOnly: true);

        Assert.Equal(new[] { 51L }, inWindow.Select(d => d.Id));
        Assert.Equal(new[] { 51L }, tagged.Select(d => d.Id));
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

    [Fact]
    public async Task AnAddressSeenOnlyInHiddenPrefillDownloadsGetsNoHostnameLookup()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var prefill = Row(61, "10.0.0.2", now.AddMinutes(-5), now.AddMinutes(-1));
        prefill.IsPrefill = true;
        context.Downloads.AddRange(Row(60, "10.0.0.1", now.AddMinutes(-20), now.AddMinutes(-10)), prefill);
        await context.SaveChangesAsync();
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetPrefillRules) =>
                new List<ClientExclusionRule> { CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide) },
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
            // The evicted mode reads as null, which hides nothing.
            _ => null
        });
        var asked = new List<string>();
        var hostnames = CacheScanGateHarness.CreateProxy<IClientHostnameService>((method, args) => method.Name switch
        {
            nameof(IClientHostnameService.IsEnabled) => true,
            nameof(IClientHostnameService.GetSettings) => new ClientHostnameSettings(),
            nameof(IClientHostnameService.ResolveAsync) => RecordAsked((IReadOnlyCollection<string>)args![0]!),
            _ => throw new NotSupportedException(method.Name)
        });
        Task<ClientHostnameLookupOutcome> RecordAsked(IReadOnlyCollection<string> clientIps)
        {
            asked.AddRange(clientIps);
            return Task.FromResult(new ClientHostnameLookupOutcome(new Dictionary<string, string>(), ClientHostnamesReason.None));
        }
        var controller = new ClientHostnamesController(
            context,
            hostnames,
            CacheScanGateHarness.CreateProxy<ISignalRNotificationService>((method, _) => throw new NotSupportedException(method.Name)),
            state);
        var http = new DefaultHttpContext();
        http.Items["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin };
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        await controller.GetHostnamesAsync(CancellationToken.None);

        Assert.Equal(new[] { "10.0.0.1" }, asked);
    }

    // A Hide prefill rule for any address and every service, plus Hide rules for the given addresses.
    private static IStateService SeparatedPrefillState(params string[] hiddenClientIps) =>
        CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenPrefillRules) =>
                new List<ClientExclusionRule> { CacheScanGateHarness.PrefillRule("", ClientExclusionModes.Hide) },
            nameof(IStateService.GetHiddenClientIps) => hiddenClientIps.ToList(),
            _ => throw new NotSupportedException(method.Name)
        });

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
