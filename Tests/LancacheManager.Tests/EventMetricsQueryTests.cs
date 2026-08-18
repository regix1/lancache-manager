using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the LINQ join that feeds the per-event Prometheus gauges. A translation failure
/// would be swallowed by the cycle's catch and leave those series empty on a real install.
/// </summary>
public class EventMetricsQueryTests
{
    private static readonly List<string> NoHiddenClients = new();
    private static readonly List<string> NoStatsExcludedClients = new();

    [Fact]
    public void EventTotalsQuery_TranslatesToSql()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=event_metrics_translation_smoke_test")
                .Options);

        var downloads = LancacheMetricsService.ExcludedClientsRemoved(
            context,
            NoHiddenClients,
            NoStatsExcludedClients);
        var sql = LancacheMetricsService.EventTotalsQuery(context, downloads)
            .OrderByDescending(row => row.TotalBytes)
            .Take(LancacheMetricsService.MaxEventCount)
            .ToQueryString();

        Assert.Contains("EventDownloads", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Events", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs on SQLite while production is Npgsql only, so it checks the LINQ shape and the numbers,
    /// not the SQL the production provider generates.
    /// </summary>
    [Fact]
    public void EventTotalsQuery_HonoursClientExclusions()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();

        var start = DateTime.UtcNow.AddHours(-4);
        context.Events.Add(PartyEvent(1, "LAN Party", start, start.AddHours(8)));
        context.Downloads.AddRange(
            SteamDownload(10, "10.0.0.1", 1000),
            SteamDownload(11, "10.0.0.2", 500),
            SteamDownload(12, "10.0.0.3", 250));
        context.EventDownloads.AddRange(
            Tag(1, 10),
            Tag(1, 11),
            Tag(1, 12));
        context.SaveChanges();

        var downloads = LancacheMetricsService.ExcludedClientsRemoved(
            context,
            ["10.0.0.2"],
            ["10.0.0.3"]);
        var totals = LancacheMetricsService.EventTotalsQuery(context, downloads).Single();

        Assert.Equal(1, totals.EventId);
        Assert.Equal("LAN Party", totals.Name);
        Assert.Equal(1000, totals.TotalBytes);
        Assert.Equal(1000, totals.HitBytes);
        Assert.Equal(1, totals.Downloads);
    }

    /// <summary>
    /// The compare chart asks for up to eight events at once. One query has to answer for all of
    /// them, keyed by event, or the endpoint pays a round trip and a pooled context per event.
    /// Runs on SQLite, so it checks the LINQ shape and the grouping, not the production SQL.
    /// </summary>
    [Fact]
    public async Task LoadTaggedDownloadIds_AnswersEveryEventFromOneQuery()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();

        var start = DateTime.UtcNow.AddHours(-4);
        context.Events.Add(PartyEvent(1, "Friday", start, start.AddHours(8)));
        context.Events.Add(PartyEvent(2, "Saturday", start, start.AddHours(8)));
        context.Events.Add(PartyEvent(3, "Sunday", start, start.AddHours(8)));
        context.Downloads.AddRange(
            SteamDownload(10, "10.0.0.1", 1000),
            SteamDownload(11, "10.0.0.2", 500),
            SteamDownload(12, "10.0.0.3", 250));
        context.EventDownloads.AddRange(
            Tag(1, 10),
            Tag(1, 11),
            Tag(2, 12));
        context.SaveChanges();

        var tagged = await DashboardBatchService.LoadTaggedDownloadIdsAsync(
            context,
            [1, 2, 3],
            CancellationToken.None);

        Assert.Equal(new[] { 10L, 11L }, tagged[1].Order());
        Assert.Equal(new[] { 12L }, tagged[2].Order());
        Assert.False(tagged.ContainsKey(3));
    }

    private static Event PartyEvent(long id, string name, DateTime start, DateTime end) => new()
    {
        Id = id,
        Name = name,
        StartTimeUtc = start,
        EndTimeUtc = end,
        ColorIndex = 1,
        CreatedAtUtc = start
    };

    private static EventDownload Tag(long eventId, long downloadId) => new()
    {
        EventId = eventId,
        DownloadId = downloadId,
        TaggedAtUtc = DateTime.UtcNow,
        AutoTagged = true
    };

    private static Download SteamDownload(long id, string clientIp, long hitBytes)
    {
        var start = DateTime.UtcNow.AddHours(-2);
        return new Download
        {
            Id = id,
            Service = "steam",
            ClientIp = clientIp,
            StartTimeUtc = start,
            StartTimeLocal = start,
            EndTimeUtc = start.AddMinutes(10),
            EndTimeLocal = start.AddMinutes(10),
            CacheHitBytes = hitBytes,
            CacheMissBytes = 0,
            IsActive = false,
            GameAppId = 730,
            GameName = "Counter-Strike 2",
            Datasource = "default",
            IsEvicted = false
        };
    }
}
