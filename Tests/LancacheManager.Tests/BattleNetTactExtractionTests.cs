using LancacheManager.Core.Services.BattleNet;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the two-step Blizzard CDN-path extraction: tpr segments resolve as before, and
/// URLs without a tpr prefix expose their first path segment for catalog-gated fallback
/// (btlr publishes cortez/Cerberus-B-Live instead of tpr/btlr). The gate itself lives at
/// the resolve call site; these tests lock the pure parsing halves.
/// </summary>
public sealed class BattleNetTactExtractionTests
{
    [Theory]
    [InlineData("/tpr/wow/data/ab/cd/abcdef", "wow")]
    [InlineData("/tpr/BTLR/config/aa/bb/cc", "btlr")]
    public void ExtractTactSegment_ParsesTprSegments(string url, string expected)
    {
        Assert.Equal(expected, BattleNetMappingService.ExtractTactSegment(url));
    }

    [Theory]
    [InlineData("/cortez/Cerberus-B-Live/data/81/1d/811de194873df83c")]
    [InlineData("/filestreamingservice/files/abc")]
    public void ExtractTactSegment_ReturnsNullWithoutTpr(string url)
    {
        Assert.Null(BattleNetMappingService.ExtractTactSegment(url));
    }

    [Theory]
    [InlineData("/cortez/Cerberus-B-Live/data/81/1d/811de194873df83c", "cortez")]
    [InlineData("/filestreamingservice/files/abc", "filestreamingservice")]
    public void ExtractCdnPathRootSegment_ReturnsLowercasedFirstSegment(string url, string expected)
    {
        Assert.Equal(expected, BattleNetMappingService.ExtractCdnPathRootSegment(url));
    }

    [Fact]
    public void ExtractCdnPathRootSegment_UrlWithoutSegments_ReturnsNull()
    {
        Assert.Null(BattleNetMappingService.ExtractCdnPathRootSegment(""));
        Assert.Null(BattleNetMappingService.ExtractCdnPathRootSegment("/"));
    }
}
