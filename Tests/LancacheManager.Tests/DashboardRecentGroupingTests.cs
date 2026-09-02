using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The dashboard's recent section groups by game before it caps, so a game that produced hundreds
/// of downloads can no longer push every other game out of the panel. These run on the EF InMemory
/// provider, which evaluates LINQ the database could not, so they say nothing about whether the
/// aggregate translates - <see cref="DashboardRecentQueryTranslationTests"/> answers that.
/// </summary>
public sealed class DashboardRecentGroupingTests(ITestOutputHelper output)
{
    private static readonly DateTime Anchor = new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task OneLoudGameDoesNotCrowdTheOtherGamesOut()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 499; i++)
            {
                seed.Downloads.Add(NamedGame("steam", "10.0.0.1", Anchor.AddMinutes(-i), 731, 730, "Call of Duty"));
            }
            for (var other = 0; other < 12; other++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.2", Anchor.AddDays(-1).AddMinutes(-other),
                    900 + other, 800 + other, $"Older Game {other}"));
            }
            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));

        Assert.Equal(13, groups.Count);
        Assert.Equal("Call of Duty", groups[0].Name);
        Assert.Equal(12, groups.Count(g => g.Name.StartsWith("Older Game", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The count on the row is the game's real total. The cap now falls on games rather than rows,
    /// and one depot group counts every one of its members whatever the cap is.
    /// </summary>
    [Fact]
    public async Task TheCountIsTheGamesRealTotalNotTheSliceItFitsIn()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 620; i++)
            {
                seed.Downloads.Add(NamedGame("steam", "10.0.0.1", Anchor.AddMinutes(-i), 731, 730, "Call of Duty"));
            }
            await seed.SaveChangesAsync();
        }

        var group = Assert.Single(GroupsOf(await RecentSectionAsync(options)));

        Assert.Equal(620, group.Count);
        Assert.Equal(620, group.DownloadIds.Count);
    }

    /// <summary>
    /// Both shapes that eat a fixed scan window at once: rows with no depot, which each form a
    /// group of their own, and one game spread over seventy clients, which forms a group per
    /// client. The flood folds into a single service bucket, so it takes the hundredth slot and
    /// the ninety-nine games fill the rest.
    /// </summary>
    [Fact]
    public async Task TheOldestCarriedGameSurvivesANoDepotFloodAndAGameSpreadOverManyClients()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 600; i++)
            {
                seed.Downloads.Add(Row("epicgames", $"10.1.0.{i % 200}", Anchor.AddSeconds(-i)));
            }
            for (var client = 0; client < 70; client++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", $"10.2.0.{client}", Anchor.AddHours(-1), 5001, 5000, "Game Day Shooter"));
            }
            for (var game = 0; game < 98; game++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.9", Anchor.AddHours(-2).AddMinutes(-game),
                    6000 + game, 7000 + game, $"Quiet Game {game}"));
            }
            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));

        Assert.Equal(100, groups.Count);
        Assert.Contains(groups, g => g.Name == "Quiet Game 97");
        var spread = Assert.Single(groups, g => g.Name == "Game Day Shooter");
        Assert.Equal(70, spread.ClientIps.Count);
        Assert.Equal(70, spread.Count);
        Assert.Contains(groups, g => g.Id == "service-epicgames");
    }

    [Fact]
    public async Task ASteamPlaceholderNameLandsInTheServiceBucket()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NamedGame("steam", "10.0.0.1", Anchor, 731, 730, "Steam App 730"));
            await seed.SaveChangesAsync();
        }

        var group = Assert.Single(GroupsOf(await RecentSectionAsync(options)));

        Assert.Equal("service-steam", group.Id);
        Assert.False(group.HasRealGameName);
    }

    /// <summary>
    /// A service bucket carries the folded service in its name, not a rendered title. The browser
    /// writes that label from its locale files, so a title composed here would reach every reader
    /// in English.
    /// </summary>
    [Theory]
    [InlineData("epicgames", "Epic Games", "service-epicgames", "epicgames")]
    [InlineData("xboxlive", "Xbox Live", "service-xbox", "xbox")]
    [InlineData("riot", "Riot Games", "service-riot", "riot")]
    public async Task ARowNamedAfterItsOwnServiceLandsInTheServiceBucket(
        string service, string storedName, string expectedGroupId, string expectedName)
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NamedGame(service, "10.0.0.1", Anchor, null, null, storedName));
            await seed.SaveChangesAsync();
        }

        var group = Assert.Single(GroupsOf(await RecentSectionAsync(options)));

        Assert.Equal(expectedGroupId, group.Id);
        Assert.Equal(expectedName, group.Name);
        Assert.False(group.HasRealGameName);
    }

    /// <summary>
    /// A real game keeps the resolved title in its name, because a game title is not translated.
    /// The panel prints it as it arrives.
    /// </summary>
    [Fact]
    public async Task AGameGroupCarriesTheResolvedTitleAsItsName()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NamedGame("steam", "10.0.0.1", Anchor, 731, 730, "Call of Duty"));
            await seed.SaveChangesAsync();
        }

        var group = Assert.Single(GroupsOf(await RecentSectionAsync(options)));

        Assert.Equal("game-appid-730", group.Id);
        Assert.Equal("Call of Duty", group.Name);
        Assert.True(group.HasRealGameName);
        Assert.Equal("content", group.Type);
        Assert.Equal(3072, group.TotalBytes);
    }

    /// <summary>
    /// The raw array carries the active rows and a short tail of freshly finished ones. Without the
    /// tail a download that finished between two polls simply vanishes, and the live preview beside
    /// it has nothing to retire against until the browser's own sticky timeout expires.
    /// </summary>
    [Fact]
    public async Task TheRawRowsCarryTheActiveOnesAndAThirtySecondFinishedTail()
    {
        var options = NewDatabase();
        var now = DateTime.UtcNow;
        await using (var seed = new AppDbContext(options))
        {
            var running = NamedGame("steam", "10.0.0.1", now.AddMinutes(-30), 731, 730, "Call of Duty");
            running.IsActive = true;
            running.EndTimeUtc = now;
            seed.Downloads.Add(running);

            var justFinished = NamedGame("steam", "10.0.0.2", now.AddMinutes(-2), 741, 740, "Recently Done");
            justFinished.EndTimeUtc = now.AddSeconds(-10);
            seed.Downloads.Add(justFinished);

            var longFinished = NamedGame("steam", "10.0.0.3", now.AddMinutes(-5), 751, 750, "Long Done");
            longFinished.EndTimeUtc = now.AddSeconds(-60);
            seed.Downloads.Add(longFinished);

            await seed.SaveChangesAsync();
        }

        var rows = RowsOf(await RecentSectionAsync(options));

        Assert.Contains(rows, r => r.GameName == "Call of Duty");
        Assert.Contains(rows, r => r.GameName == "Recently Done");
        Assert.DoesNotContain(rows, r => r.GameName == "Long Done");
    }

    /// <summary>
    /// ShowClean hides eviction rather than hiding the rows, so the group's flags have to be masked
    /// the same way the raw rows already were. Without this the mode starts showing the eviction it
    /// exists to hide, on the groups instead of the rows.
    /// </summary>
    [Fact]
    public async Task ShowCleanMasksEvictionOnTheGroupsAndTheRows()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            var evicted = NamedGame("steam", "10.0.0.1", Anchor, 731, 730, "Call of Duty");
            evicted.IsEvicted = true;
            evicted.IsActive = true;
            seed.Downloads.Add(evicted);
            await seed.SaveChangesAsync();
        }

        var shown = GroupsOf(await RecentSectionAsync(options));
        Assert.True(Assert.Single(shown).IsEvicted);

        var section = await RecentSectionAsync(options, EvictedDataMode.ShowClean.ToWireString());
        var cleaned = Assert.Single(GroupsOf(section));

        Assert.False(cleaned.IsEvicted);
        Assert.False(cleaned.IsPartiallyEvicted);
        Assert.All(RowsOf(section), r => Assert.False(r.IsEvicted));
    }

    /// <summary>
    /// An active game older than every carried game still reaches the panel, because the server
    /// folds the active rows a second time and appends what the cap left out. The browser builds no
    /// groups of its own, so if this did not happen a long prefill would disappear from the panel.
    /// </summary>
    [Fact]
    public async Task AnActiveGameOlderThanTheCarriedHundredIsAppended()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var game = 0; game < 100; game++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.9", Anchor.AddMinutes(-game),
                    6000 + game, 7000 + game, $"Newer Game {game}"));
            }

            var prefill = NamedGame("steam", "10.0.0.1", Anchor.AddDays(-3), 999, 998, "Long Prefill");
            prefill.IsActive = true;
            seed.Downloads.Add(prefill);
            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));

        Assert.Equal(101, groups.Count);
        var appended = Assert.Single(groups, g => g.Name == "Long Prefill");
        Assert.Equal(1, appended.Count);
        Assert.Equal("Long Prefill", groups[^1].Name);
    }

    /// <summary>
    /// The five hundred row cap existed because the section is the one part of the batch response
    /// that is charged its real serialized length against the cache size limit. Grouping has to
    /// leave it smaller on the traffic shape that motivated the change, not merely correct.
    /// </summary>
    [Fact]
    public async Task TheSectionSerializesSmallerThanTheFiveHundredRowSliceItReplaced()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 499; i++)
            {
                seed.Downloads.Add(NamedGame("steam", "10.0.0.1", Anchor.AddMinutes(-i), 731, 730, "Call of Duty"));
            }
            seed.Downloads.Add(NamedGame("steam", "10.0.0.2", Anchor.AddMinutes(-500), 741, 740, "One Other Game"));
            await seed.SaveChangesAsync();
        }

        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var grouped = JsonSerializer.SerializeToUtf8Bytes(await RecentSectionAsync(options), wire).Length;

        await using var read = new AppDbContext(options);
        var slice = await read.Downloads.AsNoTracking()
            .OrderByDescending(d => d.StartTimeUtc)
            .Take(500)
            .ToListAsync();
        var flat = JsonSerializer.SerializeToUtf8Bytes(slice, wire).Length;

        output.WriteLine($"grouped section {grouped} bytes, five hundred row slice {flat} bytes");
        Assert.True(grouped < flat, $"grouped section {grouped} bytes, five hundred row slice {flat} bytes");
    }

    private static DbContextOptions<AppDbContext> NewDatabase() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static Download Row(string service, string clientIp, DateTime start) => new()
    {
        Service = service,
        ClientIp = clientIp,
        StartTimeUtc = start,
        EndTimeUtc = start.AddMinutes(1),
        CacheHitBytes = 2048,
        CacheMissBytes = 1024
    };

    private static Download NamedGame(
        string service, string clientIp, DateTime start,
        long? depotId, long? gameAppId, string gameName)
    {
        var row = Row(service, clientIp, start);
        row.DepotId = depotId;
        row.GameAppId = gameAppId;
        row.GameName = gameName;
        return row;
    }

    private static async Task<object> RecentSectionAsync(
        DbContextOptions<AppDbContext> options,
        string evictedMode = "show")
    {
        var service = (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        typeof(DashboardBatchService)
            .GetField("_dbContextFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new RecentDbContextFactory(options));

        var method = typeof(DashboardBatchService)
            .GetMethod("GetRecentDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return await (Task<object>)method.Invoke(
            service,
            [null, null, new List<long>(), null, new List<string>(), evictedMode, null, null, CancellationToken.None])!;
    }

    private static List<DashboardBatchService.DashboardGameGroup> GroupsOf(object section) =>
        (List<DashboardBatchService.DashboardGameGroup>)section.GetType()
            .GetProperty("groups")!.GetValue(section)!;

    private static List<DashboardBatchService.DashboardDownloadRow> RowsOf(object section) =>
        (List<DashboardBatchService.DashboardDownloadRow>)section.GetType()
            .GetProperty("rows")!.GetValue(section)!;

    private sealed class RecentDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }
}

/// <summary>
/// Compiles the recent section's aggregate through the Npgsql provider without opening a
/// connection. The seeded tests above run on the EF InMemory provider, which evaluates LINQ the
/// database cannot translate, so every one of them can be green while the endpoint returns a 500.
/// </summary>
public sealed class DashboardRecentQueryTranslationTests
{
    [Fact]
    public void RecentGroupedAggregateQuery_TranslatesToSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        using var context = new AppDbContext(options);

        var buildGroupedQuery = typeof(DashboardBatchService)
            .GetMethod("BuildRecentGroupedQuery", BindingFlags.NonPublic | BindingFlags.Static)!;
        var query = (IQueryable<DashboardBatchService.DashboardGroupRow>)buildGroupedQuery.Invoke(
            null, [context.Downloads.AsNoTracking()])!;

        var sql = query.ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }
}
