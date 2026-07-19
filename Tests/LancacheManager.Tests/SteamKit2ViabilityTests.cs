using LancacheManager.Core.Services.SteamKit2;

namespace LancacheManager.Tests;

// Regression coverage for the fresh-install depot viability decision. With no depot mappings in the
// database or the JSON snapshot and no saved PICS change number, an incremental crawl has nothing to
// diff against, so viability must report a required full scan. That lets the scheduler take its
// existing graceful skip branch instead of starting a crawl that throws on boot.
public class SteamKit2ViabilityTests
{
    [Fact]
    public void HasUsableBaseline_IsFalse_WhenNoMappingsAndNoChangeNumber()
    {
        var baseline = new SteamKit2Service.DepotBaseline(0, 0, 0);

        Assert.False(baseline.HasUsableBaseline);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 42)]
    [InlineData(5, 5, 100)]
    public void HasUsableBaseline_IsTrue_WhenAnyBaselineSignalPresent(int dbCount, int jsonCount, int changeNumber)
    {
        var baseline = new SteamKit2Service.DepotBaseline(dbCount, jsonCount, (uint)changeNumber);

        Assert.True(baseline.HasUsableBaseline);
    }

    [Fact]
    public void FreshInstall_ProducesNonViableSkip_WithoutErrorOrChangeNumber()
    {
        var baseline = new SteamKit2Service.DepotBaseline(0, 0, 0);

        // A cached "viable" answer must not be reusable while the baseline is absent, so the fresh
        // install falls through to the needs-initial-data result rather than reusing a stale cache.
        Assert.False(SteamKit2Service.ShouldReuseCachedViability(cachedRequiresFullScan: false, baseline.HasUsableBaseline));

        var result = SteamKit2Service.BuildNeedsInitialDataResult();

        // These field values are exactly what routes the scheduler to its graceful skip branch: a
        // required full scan with no Error set (a non-empty Error would instead be read as a Steam
        // connection failure).
        Assert.False(result.IsViable);
        Assert.True(result.WillTriggerFullScan);
        Assert.Null(result.Error);
        Assert.Equal(0u, result.LastChangeNumber);
        Assert.Equal(0u, result.ChangeGap);
    }

    [Theory]
    [InlineData(true, false, true)]   // cached requires full scan: safe to reuse even with no baseline
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]   // cached viable and baseline present: reuse
    [InlineData(false, false, false)] // cached viable but baseline gone: must not reuse
    public void ShouldReuseCachedViability_GuardsAgainstStaleViableReuse(
        bool cachedRequiresFullScan,
        bool hasUsableBaseline,
        bool expected)
    {
        Assert.Equal(expected, SteamKit2Service.ShouldReuseCachedViability(cachedRequiresFullScan, hasUsableBaseline));
    }
}
