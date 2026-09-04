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
    /// <summary>
    /// Relative to now, not a fixed date. The live view reads a window measured back from the
    /// current time, so rows pinned to a calendar date drift out of it as the clock moves and the
    /// suite would start failing on a day nobody changed anything.
    /// </summary>
    private static readonly DateTime Anchor = DateTime.UtcNow.AddHours(-1);

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
    /// The count on the row is the game's real total. The cap falls on games rather than rows, and
    /// one group counts every one of its members whatever the cap is. The member ids are a
    /// different matter: they are capped at five hundred, which is what the batch event route
    /// answers in one call, so a group larger than that carries its newest five hundred.
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
        Assert.Equal(500, group.DownloadIds.Count);
    }

    /// <summary>
    /// The outcome the change exists for. A game with rows today and rows weeks ago used to have
    /// the older ones outside the window the scan stopped at, so its count, its bytes and its
    /// client list all read low. Every other game here is newer than the older day, so the old
    /// scan folded its hundred games and stopped before it ever reached those rows.
    /// </summary>
    [Fact]
    public async Task AGameWithRowsOnTwoDistantDaysCountsBothOfThem()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NamedGame("steam", "10.0.0.1", Anchor, 731, 730, "Call of Duty"));
            for (var game = 0; game < 150; game++)
            {
                for (var i = 0; i < 20; i++)
                {
                    seed.Downloads.Add(NamedGame(
                        "steam", "10.0.0.9", Anchor.AddHours(-1).AddMinutes(-i),
                        6000 + game, 7000 + game, $"Newer Game {game}"));
                }
            }
            for (var i = 0; i < 40; i++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.2", Anchor.AddDays(-21).AddMinutes(-i), 731, 730, "Call of Duty"));
            }
            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));
        var counted = Assert.Single(groups, g => g.Name == "Call of Duty");

        Assert.Equal(41, counted.Count);
        Assert.Equal(41 * 2048, counted.CacheHitBytes);
        Assert.Equal(41 * 1024, counted.CacheMissBytes);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, counted.ClientIps.Order());
    }

    /// <summary>
    /// The live view picks no range and the header calls it all of history, so a count on it is
    /// every download the game ever made, however old. This was a thirty-day window once, which
    /// answered a narrower range than the reader had chosen while the panel went on labelling it
    /// with the wider one. A reader who names a start time gets the same figures, which is what the
    /// second half checks.
    /// </summary>
    [Fact]
    public async Task TheLiveViewCountsRowsOlderThanAMonthAndSoDoesAnExplicitRange()
    {
        var options = NewDatabase();
        var now = DateTime.UtcNow;
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 3; i++)
            {
                seed.Downloads.Add(NamedGame("steam", "10.0.0.1", now.AddHours(-1 - i), 731, 730, "Call of Duty"));
            }
            for (var i = 0; i < 2; i++)
            {
                seed.Downloads.Add(NamedGame("steam", "10.0.0.1", now.AddDays(-45 - i), 731, 730, "Call of Duty"));
            }
            await seed.SaveChangesAsync();
        }

        var live = Assert.Single(GroupsOf(await RecentSectionAsync(options)));
        Assert.Equal(5, live.Count);
        Assert.Equal(5 * 2048, live.CacheHitBytes);
        Assert.Equal(5, live.DownloadIds.Count);

        var asked = Assert.Single(GroupsOf(await RecentSectionAsync(
            options, startTime: new DateTimeOffset(now.AddDays(-60), TimeSpan.Zero).ToUnixTimeSeconds())));
        Assert.Equal(5, asked.Count);
        Assert.Equal(5, asked.DownloadIds.Count);
    }

    /// <summary>
    /// A carried active game carries the range total, not a tally of the rows that happen to be
    /// running. A prefill that has run several times over several days used to read as a count of
    /// one while it ran, then jump to its real number the moment it finished and climbed into the
    /// newest hundred, which is the same wrong number this whole change removes.
    /// </summary>
    [Fact]
    public async Task ACarriedActiveGameCountsItsWholeRangeNotOnlyItsActiveRows()
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

            for (var run = 1; run < 8; run++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.1", Anchor.AddDays(-3 - run), 999, 998, "Long Prefill"));
            }
            var running = NamedGame("steam", "10.0.0.1", Anchor.AddDays(-3), 999, 998, "Long Prefill");
            running.IsActive = true;
            seed.Downloads.Add(running);

            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));
        var appended = Assert.Single(groups, g => g.Name == "Long Prefill");

        Assert.Equal(101, groups.Count);
        Assert.Equal(8, appended.Count);
        Assert.Equal(8 * 2048, appended.CacheHitBytes);
        Assert.Equal(8, appended.DownloadIds.Count);
        // Leads the panel because it is running. It used to sit last, being older than the hundred.
        Assert.Equal("Long Prefill", groups[0].Name);
    }

    /// <summary>
    /// Every group gets its own five hundred rather than a share of one budget. A quiet game whose
    /// rows are all older than a loud neighbour's must still carry its own ids, because a group
    /// with no ids draws no event badges and nothing on screen or in the log says why. The loud
    /// game here has more rows than any whole-read bound would have left room for.
    /// </summary>
    [Fact]
    public async Task AQuietGroupKeepsItsMemberIdsBesideALoudOne()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 1200; i++)
            {
                seed.Downloads.Add(NamedGame("steam", "10.0.0.1", Anchor.AddMinutes(-i), 731, 730, "Loud Game"));
            }
            for (var i = 0; i < 3; i++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.2", Anchor.AddDays(-20).AddMinutes(-i), 741, 740, "Quiet Game"));
            }
            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));
        var quiet = Assert.Single(groups, g => g.Name == "Quiet Game");
        var loud = Assert.Single(groups, g => g.Name == "Loud Game");

        Assert.Equal(3, quiet.Count);
        Assert.Equal(3, quiet.DownloadIds.Count);
        Assert.Equal(1200, loud.Count);
        Assert.Equal(500, loud.DownloadIds.Count);
    }

    /// <summary>
    /// The id list and the count are filled by two different queries, so nothing but this holds
    /// them together. A badge is drawn per id, and an id the count does not cover would draw a
    /// badge for a member the row says it does not have.
    /// </summary>
    [Fact]
    public async Task NoGroupCarriesMoreMemberIdsThanItsCount()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 30; i++)
            {
                seed.Downloads.Add(Row("wsus", "10.0.0.1", Anchor.AddMinutes(-i)));
            }
            for (var i = 0; i < 12; i++)
            {
                seed.Downloads.Add(NamedGame("steam", "10.0.0.2", Anchor.AddMinutes(-i), 731, 730, "Call of Duty"));
            }

            var prefill = NamedGame("steam", "10.0.0.3", Anchor.AddDays(-4), 999, 998, "Long Prefill");
            prefill.IsActive = true;
            seed.Downloads.Add(prefill);
            for (var i = 0; i < 7; i++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.3", Anchor.AddDays(-4).AddMinutes(-i - 1), 999, 998, "Long Prefill"));
            }
            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));

        Assert.All(groups, g => Assert.True(
            g.DownloadIds.Count <= g.Count,
            $"{g.Id} carries {g.DownloadIds.Count} ids against a count of {g.Count}"));
    }

    /// <summary>
    /// Rows with no depot used to form one aggregate group each, which is what drove the scan to
    /// page dozens of times over a range full of Windows Update traffic. They fold on their
    /// service now, so one client's whole run is one group and one set of member ids.
    /// </summary>
    [Fact]
    public async Task NoDepotTrafficFormsOneGroupPerClientRatherThanOnePerRow()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            for (var i = 0; i < 400; i++)
            {
                seed.Downloads.Add(Row("wsus", i % 2 == 0 ? "10.0.0.1" : "10.0.0.2", Anchor.AddSeconds(-i)));
            }
            await seed.SaveChangesAsync();
        }

        var group = Assert.Single(GroupsOf(await RecentSectionAsync(options)));

        Assert.Equal("service-wsus", group.Id);
        Assert.Equal(400, group.Count);
        Assert.Equal(2, group.ClientIps.Count);
        Assert.Equal(400, group.DownloadIds.Count);
    }

    /// <summary>
    /// Each shape the ingestion could not name keeps its own group: a Steam row on an unmapped
    /// depot by that depot, an Epic row by its app id, an Xbox row by its product id. None of them
    /// scatter and none of them collapse into one another.
    /// </summary>
    [Fact]
    public async Task UnnamedRowsGroupOnTheColumnThatIdentifiesThem()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            var unmappedOne = Row("steam", "10.0.0.1", Anchor);
            unmappedOne.DepotId = 100001;
            seed.Downloads.Add(unmappedOne);
            var unmappedTwo = Row("steam", "10.0.0.1", Anchor.AddMinutes(-1));
            unmappedTwo.DepotId = 100002;
            seed.Downloads.Add(unmappedTwo);

            var epicOne = Row("epicgames", "10.0.0.1", Anchor.AddMinutes(-2));
            epicOne.EpicAppId = "fortnite";
            seed.Downloads.Add(epicOne);
            var epicTwo = Row("epicgames", "10.0.0.1", Anchor.AddMinutes(-3));
            epicTwo.EpicAppId = "rocketleague";
            seed.Downloads.Add(epicTwo);

            var xbox = Row("xboxlive", "10.0.0.1", Anchor.AddMinutes(-4));
            xbox.XboxProductId = "9NBLGGH4R315";
            seed.Downloads.Add(xbox);

            await seed.SaveChangesAsync();
        }

        var groups = GroupsOf(await RecentSectionAsync(options));

        // No mapping tables are seeded, so every row falls back to its service and the two service
        // buckets are what the panel draws. What matters is that the five rows arrive whole.
        Assert.Equal(5, groups.Sum(g => g.Count));
        Assert.Equal(5, groups.Sum(g => g.DownloadIds.Count));
        Assert.Equal(2, groups.Count(g => g.Id == "service-steam" || g.Id == "service-epicgames"));
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
    /// carries in the group the aggregate already built for it. The browser builds no groups of its
    /// own, so if this did not happen a long prefill would disappear from the panel. It then leads
    /// the panel rather than trailing it: running games hold the top so a reader can watch one
    /// without it drifting, and this one is older than all hundred finished games.
    /// </summary>
    [Fact]
    public async Task AnActiveGameOlderThanTheCarriedHundredStillReachesThePanel()
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
        var carried = Assert.Single(groups, g => g.Name == "Long Prefill");
        Assert.Equal(1, carried.Count);
        Assert.Equal("Long Prefill", groups[0].Name);
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

    /// <summary>
    /// A Steam game whose depot was unmapped when its older downloads were ingested has those rows
    /// stored under the depot alone and its newer ones under the app id. That is the normal path
    /// rather than an edge case, because the mapping backfill runs later than the ingestion. The
    /// two halves are one game and the count has to say so.
    /// </summary>
    [Fact]
    public async Task AGameStoredUnderBothADepotAndAnAppIdCountsAsOne()
    {
        var options = NewDatabase();
        await using (var seed = new AppDbContext(options))
        {
            seed.SteamDepotMappings.Add(new SteamDepotMapping
            {
                DepotId = 741,
                AppId = 730,
                AppName = "Call of Duty",
                IsOwner = true
            });

            for (var i = 0; i < 3; i++)
            {
                seed.Downloads.Add(NamedGame(
                    "steam", "10.0.0.1", Anchor.AddMinutes(-i), 731, 730, "Call of Duty"));
            }

            for (var i = 0; i < 4; i++)
            {
                var older = Row("steam", "10.0.0.1", Anchor.AddDays(-90).AddMinutes(-i));
                older.DepotId = 741;
                seed.Downloads.Add(older);
            }

            await seed.SaveChangesAsync();
        }

        var group = Assert.Single(GroupsOf(await RecentSectionAsync(options)));

        Assert.Equal("Call of Duty", group.Name);
        Assert.Equal(7, group.Count);
        Assert.Equal(7 * 2048, group.CacheHitBytes);
        Assert.Equal(7, group.DownloadIds.Count);
    }

    /// <summary>
    /// The identities above are only reached because the listed game contributes its app id and the
    /// depots that app owns to the filter the totals pass runs under. The seeded test cannot show
    /// that on its own: its rows all fall inside the newest slice, so both halves would be named
    /// there anyway. This checks the contribution itself, which is what carries a game whose older
    /// half sits outside that slice.
    /// </summary>
    [Fact]
    public void TheListedGamesContributeTheirAppIdAndEveryDepotItOwns()
    {
        var listed = new DashboardBatchService.DashboardGameGroup
        {
            Id = "game-appid-730",
            Name = "Call of Duty",
            Service = "steam",
            HasRealGameName = true,
            GameAppId = 730
        };
        var seen = new DashboardBatchService.GroupMemberKey(
            null, null, 741, null, null, "steam", "10.0.0.1");
        var identitiesByKey = new Dictionary<string, HashSet<DashboardBatchService.GroupMemberKey>>(StringComparer.Ordinal)
        {
            ["game-appid-730"] = [seen]
        };
        List<SteamDepotMapping> owned =
        [
            new() { DepotId = 741, AppId = 730, IsOwner = true },
            new() { DepotId = 742, AppId = 730, IsOwner = true },
            new() { DepotId = 999, AppId = 4000, IsOwner = true }
        ];

        var expand = typeof(DashboardBatchService)
            .GetMethod("ExpandListedIdentities", BindingFlags.NonPublic | BindingFlags.Static)!;
        var expanded = (List<DashboardBatchService.GroupMemberKey>)expand.Invoke(
            null, [new List<DashboardBatchService.DashboardGameGroup> { listed }, identitiesByKey, owned])!;

        Assert.Contains(expanded, k => k.GameAppId == 730 && k.DepotId == null);
        Assert.Contains(expanded, k => k.GameAppId == null && k.DepotId == 741);
        Assert.Contains(expanded, k => k.GameAppId == null && k.DepotId == 742);
        // A depot owned by another app is not this game's, so it stays out of the filter.
        Assert.DoesNotContain(expanded, k => k.DepotId == 999);
        // The columns it does not set come from an identity the game already has, so the key adds
        // one value to the filter rather than widening the service and client lists as well.
        Assert.All(expanded, k =>
        {
            Assert.Equal("steam", k.Service);
            Assert.Equal("10.0.0.1", k.ClientIp);
        });
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
        string evictedMode = "show",
        long? startTime = null)
    {
        var service = (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        typeof(DashboardBatchService)
            .GetField("_dbContextFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new RecentDbContextFactory(options));

        var method = typeof(DashboardBatchService)
            .GetMethod("GetRecentDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return await (Task<object>)method.Invoke(
            service,
            [startTime, null, new List<long>(), null, new List<string>(), evictedMode, null, null, CancellationToken.None])!;
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

        // The identity columns are the change this test exists for. A key the provider cannot
        // group on throws at the endpoint while every seeded test above stays green.
        var groupBy = sql[sql.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase)..];
        foreach (var column in new[]
                 {
                     "GameAppId", "GameName", "DepotId", "EpicAppId", "XboxProductId",
                     "Service", "ClientIp"
                 })
        {
            Assert.Contains(column, groupBy, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The member ids ride a second query whose filter has one arm per identity column and whose
    /// per-group limit rests on a window function. LINQ has no window operator, so the limit only
    /// stays per group while the provider keeps emitting ROW_NUMBER partitioned by the identity: a
    /// rewrite that lost it would silently go back to a shared budget, where one loud game takes
    /// the newest rows and a quiet one beside it draws no badges at all. That is what the
    /// PARTITION BY assertion below is guarding, and no seeded test can see it.
    /// </summary>
    [Fact]
    public void GroupMemberQuery_TranslatesToSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        using var context = new AppDbContext(options);

        var buildMemberQuery = typeof(DashboardBatchService)
            .GetMethod("BuildGroupMemberQuery", BindingFlags.NonPublic | BindingFlags.Static)!;
        DashboardBatchService.GroupMemberKey[] identities =
        [
            new(730, "Call of Duty", 731, null, null, "steam", "10.0.0.1"),
            new(null, null, null, "fortnite", null, "epicgames", "10.0.0.2"),
            new(null, null, null, null, "9NBLGGH4R315", "xboxlive", "10.0.0.3"),
            new(null, null, null, null, null, "wsus", "10.0.0.4")
        ];
        var query = (IQueryable<DashboardBatchService.IdentityMembers>)buildMemberQuery.Invoke(
            null, [context.Downloads.AsNoTracking(), identities, 500])!;

        var sql = query.ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ROW_NUMBER() OVER(PARTITION BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY d0.\"StartTimeUtc\" DESC)", sql, StringComparison.Ordinal);

        // The window partitions on the same seven columns the aggregate groups on. A partition
        // narrower than the group key would hand one group several windows of five hundred.
        var partition = sql[sql.IndexOf("PARTITION BY", StringComparison.Ordinal)..];
        partition = partition[..partition.IndexOf("ORDER BY", StringComparison.Ordinal)];
        foreach (var column in new[]
                 {
                     "GameAppId", "GameName", "DepotId", "EpicAppId", "XboxProductId",
                     "Service", "ClientIp"
                 })
        {
            Assert.Contains(column, partition, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The section decides which games it lists from a bounded slice of the range's newest rows,
    /// which is a LIMIT feeding a GROUP BY. The in-memory provider evaluates that in process
    /// whatever it looks like, so only this can say whether the database will take it.
    /// </summary>
    [Fact]
    public void RecentPickQuery_TranslatesToSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        using var context = new AppDbContext(options);

        var buildPickQuery = typeof(DashboardBatchService)
            .GetMethod("BuildRecentPickQuery", BindingFlags.NonPublic | BindingFlags.Static)!;
        var query = (IQueryable<DashboardBatchService.DashboardGroupRow>)buildPickQuery.Invoke(
            null, [context.Downloads.AsNoTracking()])!;

        var sql = query.ToQueryString();

        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);

        // The slice has to be taken before the grouping. A LIMIT applied after it would cap the
        // games rather than the rows read, which is the whole-table scan this replaced.
        Assert.True(
            sql.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase)
                < sql.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase),
            sql);
    }

    /// <summary>
    /// The totals pass narrows to the listed games before it groups. A filter the provider cannot
    /// translate would leave the endpoint returning a 500 with every seeded test still green.
    /// </summary>
    [Fact]
    public void ListedGameTotalsQuery_TranslatesToSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        using var context = new AppDbContext(options);

        DashboardBatchService.GroupMemberKey[] identities =
        [
            new(730, "Call of Duty", 731, null, null, "steam", "10.0.0.1"),
            new(null, null, null, "fortnite", null, "epicgames", "10.0.0.2"),
            new(null, null, null, null, "9NBLGGH4R315", "xboxlive", "10.0.0.3"),
            new(null, null, null, null, null, "wsus", "10.0.0.4")
        ];

        var applyIdentityFilter = typeof(DashboardBatchService)
            .GetMethod("ApplyIdentityFilter", BindingFlags.NonPublic | BindingFlags.Static)!;
        var narrowed = (IQueryable<Download>)applyIdentityFilter.Invoke(
            null, [context.Downloads.AsNoTracking(), identities])!;

        var buildGroupedQuery = typeof(DashboardBatchService)
            .GetMethod("BuildRecentGroupedQuery", BindingFlags.NonPublic | BindingFlags.Static)!;
        var query = (IQueryable<DashboardBatchService.DashboardGroupRow>)buildGroupedQuery.Invoke(
            null, [narrowed])!;

        var sql = query.ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);

        // The depots the listed games' apps own are read to widen that filter, so a game stored
        // under both a depot and an app id is counted whole. The read is compiled here beside the
        // query it feeds.
        List<long> listedAppIds = [730, 440];
        var owned = context.SteamDepotMappings.AsNoTracking()
            .Where(m => m.IsOwner && listedAppIds.Contains(m.AppId));

        var ownedSql = owned.ToQueryString();

        Assert.Contains("\"IsOwner\"", ownedSql, StringComparison.Ordinal);
        Assert.Contains("\"AppId\"", ownedSql, StringComparison.Ordinal);
    }
}
