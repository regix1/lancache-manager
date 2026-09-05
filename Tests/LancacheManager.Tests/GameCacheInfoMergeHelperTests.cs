using LancacheManager.Core;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// One identity can be detected under more than one datasource. Each datasource is scanned by its
/// own process against its own cache root, so the two reports describe disjoint files and the
/// merge has to ADD the counts and the bytes. Summing counts while overwriting the size reported
/// the second datasource's bytes as the whole total.
/// </summary>
public class GameCacheInfoMergeHelperTests
{
    [Fact]
    public void MergeService_TwoDatasources_AddsCountsAndBytesAndKeepsBothDatasources()
    {
        var existing = new ServiceCacheInfo
        {
            ServiceName = "steam",
            CacheFilesFound = 3,
            TotalSizeBytes = 300UL,
            Datasources = new List<string> { "a" }
        };

        var incoming = new ServiceCacheInfo
        {
            ServiceName = "steam",
            CacheFilesFound = 3,
            TotalSizeBytes = 300UL,
            Datasources = new List<string> { "b" }
        };

        GameCacheInfoMergeHelper.MergeService(existing, incoming);

        Assert.Equal(6, existing.CacheFilesFound);
        Assert.Equal(600UL, existing.TotalSizeBytes);
        Assert.Equal(new List<string> { "a", "b" }, existing.Datasources);
    }

    [Fact]
    public void MergeGame_TwoDatasources_AddsCountsAndBytesAndKeepsBothDatasources()
    {
        var existing = new GameCacheInfo
        {
            GameAppId = 730,
            GameName = "Counter-Strike 2",
            Service = "steam",
            CacheFilesFound = 3,
            TotalSizeBytes = 300UL,
            Datasources = new List<string> { "a" }
        };

        var incoming = new GameCacheInfo
        {
            GameAppId = 730,
            GameName = "Counter-Strike 2",
            Service = "steam",
            CacheFilesFound = 3,
            TotalSizeBytes = 300UL,
            Datasources = new List<string> { "b" }
        };

        GameCacheInfoMergeHelper.MergeGame(existing, incoming);

        Assert.Equal(6, existing.CacheFilesFound);
        Assert.Equal(600UL, existing.TotalSizeBytes);
        Assert.Equal(new List<string> { "a", "b" }, existing.Datasources);
    }
}
