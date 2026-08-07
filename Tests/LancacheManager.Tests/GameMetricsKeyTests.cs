using LancacheManager.Controllers;
using LancacheManager.Core;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the two game identity keys against each other.
/// <see cref="GamesOnDiskCalculator.GetGameKey"/> keys the <see cref="GameCacheInfo"/> shape, where a
/// named (Blizzard/Riot/Xbox) game carries <c>GameAppId</c> 0.
/// <see cref="GamesOnDiskCalculator.GetDownloadGameKey"/> keys the <see cref="Download"/> shape, where
/// the same game carries NULL. Per-game download figures and per-game on-disk figures are labelled
/// from these two helpers, so any divergence splits one game into two unrelated label sets and
/// nothing reports an error.
///
/// Also pins the identity-exact top-N selection: a game whose rows carry several different recorded
/// <c>GameName</c> values must be totalled as one game BEFORE the ranking cut, otherwise it is read
/// as a handful of small games and dropped.
/// </summary>
public class GameMetricsKeyTests
{
    // -----------------------------------------------------------------------------------------
    // The join contract: a download row and its detection row produce the same key. [10]
    // -----------------------------------------------------------------------------------------

    [Theory]
    // Steam: the app id is the identity on both sides.
    [InlineData(730L, 730L, null, "steam", "Counter-Strike 2", "steam:730")]
    // Epic: the catalog id wins over every other column on both sides.
    [InlineData(null, 0L, "0a2d9f6403244d12969e11da6713137b", "epicgames", "Fortnite", "epic:0a2d9f6403244d12969e11da6713137b")]
    // Named services: NULL on a download row, 0 on a detection row, one game.
    [InlineData(null, 0L, null, "blizzard", "Diablo IV", "named:blizzard\u0001Diablo IV")]
    [InlineData(null, 0L, null, "riot", "VALORANT", "named:riot\u0001VALORANT")]
    [InlineData(null, 0L, null, "xbox", "Halo Infinite", "named:xbox\u0001Halo Infinite")]
    public void DownloadKeyMatchesDetectionKeyForTheSameGame(
        long? downloadAppId,
        long detectionAppId,
        string? epicAppId,
        string service,
        string gameName,
        string expectedKey)
    {
        var downloadKey = GamesOnDiskCalculator.GetDownloadGameKey(downloadAppId, epicAppId, service, gameName);
        var detectionKey = GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = detectionAppId,
            EpicAppId = epicAppId,
            Service = service,
            GameName = gameName
        });

        Assert.Equal(expectedKey, downloadKey);
        Assert.Equal(expectedKey, detectionKey);
        Assert.True(string.Equals(downloadKey, detectionKey, StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceTokenIsLowercasedOnBothSides()
    {
        var downloadKey = GamesOnDiskCalculator.GetDownloadGameKey(null, null, "Blizzard", "Diablo IV");
        var detectionKey = GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = 0,
            Service = "BLIZZARD",
            GameName = "Diablo IV"
        });

        Assert.Equal("named:blizzard\u0001Diablo IV", downloadKey);
        Assert.Equal(downloadKey, detectionKey);
    }

    // -----------------------------------------------------------------------------------------
    // The named branch: a NULL or 0 app id with a real name must not land in the Steam bucket.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("blizzard", "Diablo IV")]
    [InlineData("riot", "VALORANT")]
    [InlineData("xbox", "Halo Infinite")]
    public void NamedDownloadWithNullAppIdStaysOutOfTheSteamBucket(string service, string gameName)
    {
        var key = GamesOnDiskCalculator.GetDownloadGameKey(null, null, service, gameName);

        Assert.Equal($"named:{service}\u0001{gameName}", key);
        Assert.DoesNotContain("steam:", key, StringComparison.Ordinal);

        // Two named games in one service must not collapse into a single bucket, which is what
        // keying them all on the app id alone would do. [11]
        var sibling = GamesOnDiskCalculator.GetDownloadGameKey(null, null, service, gameName + " II");
        Assert.NotEqual(key, sibling);
    }

    [Fact]
    public void NamedDownloadWithZeroAppIdTakesTheNamedBranch()
    {
        var key = GamesOnDiskCalculator.GetDownloadGameKey(0L, null, "blizzard", "Diablo IV");
        var detectionKey = GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = 0,
            Service = "blizzard",
            GameName = "Diablo IV"
        });

        // A download row can carry 0 rather than NULL for a named game. Testing only for NULL
        // would emit steam:0 here while the detection row emits named:blizzard, and the two
        // figures for this game would never line up again. [12]
        Assert.Equal("named:blizzard\u0001Diablo IV", key);
        Assert.NotEqual("steam:0", key);
        Assert.Equal(detectionKey, key);
    }

    [Fact]
    public void NullGameNameKeepsTheNamedBranch()
    {
        var key = GamesOnDiskCalculator.GetDownloadGameKey(null, null, "blizzard", null);
        var detectionKey = GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = 0,
            Service = "blizzard",
            GameName = null!
        });

        // A NULL name is not an empty name: `null != ""` is true, so the named branch still runs
        // and the name segment comes out empty. An IsNullOrEmpty test here would return steam:0
        // and disagree with the detection side. [12]
        Assert.Equal("named:blizzard\u0001", key);
        Assert.NotEqual("steam:0", key);
        Assert.Equal(detectionKey, key);
    }

    [Fact]
    public void EmptyGameNameFallsThroughToTheSteamBucket()
    {
        var key = GamesOnDiskCalculator.GetDownloadGameKey(null, null, "blizzard", string.Empty);
        var detectionKey = GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = 0,
            Service = "blizzard",
            GameName = string.Empty
        });

        Assert.Equal("steam:0", key);
        Assert.Equal(detectionKey, key);
    }

    [Fact]
    public void NullServiceFallsThroughToTheSteamBucket()
    {
        var key = GamesOnDiskCalculator.GetDownloadGameKey(null, null, null, "Some Game");
        var detectionKey = GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = 0,
            Service = null,
            GameName = "Some Game"
        });

        Assert.Equal("steam:0", key);
        Assert.Equal(detectionKey, key);
    }

    [Fact]
    public void EmptyEpicAppIdIsNotAnEpicIdentity()
    {
        var key = GamesOnDiskCalculator.GetDownloadGameKey(null, string.Empty, "epicgames", "Fortnite");
        var detectionKey = GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = 0,
            EpicAppId = string.Empty,
            Service = "epicgames",
            GameName = "Fortnite"
        });

        Assert.Equal("named:epicgames\u0001Fortnite", key);
        Assert.DoesNotContain("epic:", key, StringComparison.Ordinal);
        Assert.Equal(detectionKey, key);
    }

    // -----------------------------------------------------------------------------------------
    // Identity-exact top-N: total each game before ranking it. [13]
    // -----------------------------------------------------------------------------------------

    private sealed record GameBytesRow(string Service, long? GameAppId, string? EpicAppId, string? GameName, long TotalBytes);

    private sealed record GameTotal(string Key, long TotalBytes, string? DisplayName);

    private static readonly string[] SplitGameNames =
    [
        "Omega Alpha", "Omega Bravo", "Omega Charlie", "Omega Delta", "Omega Echo", "Omega Foxtrot"
    ];

    /// <summary>
    /// One Steam game recorded under six different names at 100 bytes each (600 in total), sitting
    /// underneath four single-row games. Every individual row of the split game is smaller than
    /// every one of the four, so a selection that ranks rows rather than games never sees it.
    /// </summary>
    private static List<GameBytesRow> SplitNameRows()
    {
        var rows = new List<GameBytesRow>
        {
            new("steam", 10, null, "Alpha", 1000),
            new("steam", 20, null, "Beta", 500),
            new("steam", 30, null, "Gamma", 490),
            new("steam", 40, null, "Delta", 480)
        };

        rows.AddRange(SplitGameNames.Select(name => new GameBytesRow("steam", 99, null, name, 100)));
        return rows;
    }

    private static List<GameTotal> TopGamesByIdentity(IEnumerable<GameBytesRow> rows, int cap)
        => rows
            .GroupBy(row => GamesOnDiskCalculator.GetDownloadGameKey(row.GameAppId, row.EpicAppId, row.Service, row.GameName), StringComparer.Ordinal)
            .Select(group => new GameTotal(group.Key, group.Sum(row => row.TotalBytes), group.Max(row => row.GameName)))
            .OrderByDescending(total => total.TotalBytes)
            .Take(cap)
            .ToList();

    private static List<GameTotal> TopGamesByOverFetch(IEnumerable<GameBytesRow> rows, int cap, int multiplier)
        => rows
            .OrderByDescending(row => row.TotalBytes)
            .Take(cap * multiplier)
            .GroupBy(row => GamesOnDiskCalculator.GetDownloadGameKey(row.GameAppId, row.EpicAppId, row.Service, row.GameName), StringComparer.Ordinal)
            .Select(group => new GameTotal(group.Key, group.Sum(row => row.TotalBytes), group.Max(row => row.GameName)))
            .OrderByDescending(total => total.TotalBytes)
            .Take(cap)
            .ToList();

    [Fact]
    public void RowsOfOneGameWithDifferentNamesShareOneKey()
    {
        var splitRows = SplitNameRows().Where(row => row.GameAppId == 99).ToList();

        var keys = splitRows
            .Select(row => GamesOnDiskCalculator.GetDownloadGameKey(row.GameAppId, row.EpicAppId, row.Service, row.GameName))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(6, splitRows.Count);
        Assert.Single(keys);
        Assert.Equal("steam:99", keys[0]);
    }

    [Fact]
    public void SplitNameGameOutranksASingleRowGameInTheTopTwo()
    {
        var top = TopGamesByIdentity(SplitNameRows(), 2);

        Assert.Equal(2, top.Count);
        Assert.Equal("steam:10", top[0].Key);
        Assert.Equal(1000L, top[0].TotalBytes);

        // 600 bytes spread over six rows beats a single 500-byte row once the rows are totalled.
        Assert.Equal("steam:99", top[1].Key);
        Assert.Equal(600L, top[1].TotalBytes);
        Assert.DoesNotContain(top, total => total.Key == "steam:20");

        // One representative display name survives the fold instead of six competing ones.
        Assert.NotNull(top[1].DisplayName);
        Assert.Contains(top[1].DisplayName!, SplitGameNames);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RankingRowsBeforeTotallingThemDropsTheSplitGame(int multiplier)
    {
        var top = TopGamesByOverFetch(SplitNameRows(), 2, multiplier);

        // Any fixed over-fetch reads the split game as several small rows and cuts them away
        // before the fold, so it reports the wrong second place and raises nothing. The number of
        // rows a game splits into has no upper bound, so no multiplier makes this sound. [13]
        Assert.DoesNotContain(top, total => total.Key == "steam:99");
        Assert.Equal("steam:20", top[1].Key);
    }

    // -----------------------------------------------------------------------------------------
    // The Steam-only scope of the unmapped predicate.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("steam", null, true)]
    [InlineData("steam", "", true)]
    [InlineData("STEAM", "steam", true)]
    [InlineData("steam", "Counter-Strike 2", false)]
    // Nameless by design. Counting these as a mapping failure reports a healthy install as broken.
    [InlineData("wsus", null, false)]
    [InlineData("blizzard", "", false)]
    [InlineData("epicgames", null, false)]
    public void UnmappedIsScopedToSteam(string service, string? gameName, bool expected)
    {
        Assert.Equal(expected, DownloadsController.IsUnmappedSteam(service, gameName));
    }
}
