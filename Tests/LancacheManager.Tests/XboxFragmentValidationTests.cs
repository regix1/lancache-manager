using LancacheManager.Services.Xbox;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the Xbox naming fix: the fragment-shape validator must accept BOTH Xbox content-URL
/// shapes, and the consumer candidate-service filter must include the <c>xboxlive</c> tag the prefill
/// daemon's assets1.xboxlive.com traffic lands under (in addition to the legacy <c>wsus</c> tag).
///
/// These fixtures are kept BYTE-FOR-BYTE identical to the Rust mirror
/// (<c>rust-processor/src/log_processor.rs</c> <c>mod xbox_fragment_guard_tests</c>) so
/// <see cref="XboxMappingService.IsValidFragment"/> and <c>cache_utils::is_valid_xbox_fragment</c>
/// stay behaviorally in sync. The rule is: filestreamingservice marker (DO-client path) OR
/// &gt;=2 well-formed 8-4-4-4-12 GUIDs (assets1 package path).
/// </summary>
public class XboxFragmentValidationTests
{
    // The real prefill fragment the daemon emits for Black Ops 4 (assets1.xboxlive.com), carrying
    // three GUIDs. SAME string as the Rust BO4_FRAGMENT fixture.
    private const string Bo4Fragment =
        "/4/e4393384-8ff0-4d92-aac1-bad1fb53178a/cdaa6a83-240e-4888-b462-5a0d2c5aa90e/1.0.23.1.04470f65-eb47-428d-89de-d70e05f73369/bo4-ww-en-fr_1.0.23.1_x64__ht1qfjb0gaftw";

    // The Delivery-Optimization client object path (marker + one GUID). SAME GUID as the Rust fixture.
    private const string FilestreamingFragment =
        "/filestreamingservice/files/12345678-90ab-cdef-1234-567890abcdef";

    [Fact]
    public void IsValidFragment_AcceptsRealAssets1PrefillFragment()
    {
        // The naming bug: this real assets1 fragment was previously REJECTED (no filestreamingservice
        // marker), leaving XboxCdnPatterns empty. It has >=2 GUIDs, so it must now validate.
        Assert.True(XboxMappingService.IsValidFragment(Bo4Fragment));
    }

    [Fact]
    public void IsValidFragment_AcceptsFilestreamingServicePath()
    {
        // The legacy DO-client path must still validate (marker + 1 GUID).
        Assert.True(XboxMappingService.IsValidFragment(FilestreamingFragment));
        Assert.True(XboxMappingService.IsValidFragment(
            "http://assets1.xboxlive.com" + FilestreamingFragment + "?P1=1"));
        Assert.True(XboxMappingService.IsValidFragment(
            "/filestreamingservice/files/ABCDEF12-3456-7890-ABCD-EF1234567890"));
        // Uppercase MARKER (not just hex) with exactly one GUID. _filestreamingFragmentRegex is
        // RegexOptions.IgnoreCase, so this validates via the marker branch alone. SAME fixture as the
        // Rust accepts_uppercase_filestreamingservice_marker test, locking the C#<->Rust equivalence.
        Assert.True(XboxMappingService.IsValidFragment(
            "/FILESTREAMINGSERVICE/FILES/12345678-90AB-CDEF-1234-567890ABCDEF"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/4/foo/bar")]
    [InlineData("/4/foo/bar/baz")]
    [InlineData("/c/msdownload/update/abc")]
    // Exactly ONE GUID and no filestreamingservice marker is not enough to be specific.
    [InlineData("/4/e4393384-8ff0-4d92-aac1-bad1fb53178a/pkg")]
    // Marker present but no valid GUID after it.
    [InlineData("/filestreamingservice/files/not-a-guid")]
    [InlineData("/filestreamingservice/files/12345678-90ab")]
    public void IsValidFragment_RejectsNonXboxOrMalformedFragments(string? fragment)
    {
        Assert.False(XboxMappingService.IsValidFragment(fragment));
    }

    [Theory]
    [InlineData("wsus", true)]
    [InlineData("xboxlive", true)]
    [InlineData("steam", false)]
    [InlineData("epicgames", false)]
    public void CandidateServiceFilter_IncludesXboxCacheServicesOnly(string service, bool expected)
    {
        // Mirrors the ResolveDownloadsAsync candidate filter
        //   EF.Functions.Like(d.Service, "%wsus%") || EF.Functions.Like(d.Service, "%xboxlive%")
        // which translates to SQL `Service LIKE '%wsus%' OR Service LIKE '%xboxlive%'`. Service tags
        // are recorded lowercased at ingest, so a `%x%` LIKE is an ordinal substring test; EF.Functions
        // .Like is translation-only and cannot be invoked client-side, so we assert the equivalent
        // substring semantics here. A steam row never matches → it can never be relabeled to xbox.
        // Same lowercase fixtures as the Rust is_xbox_cache_service guard test.
        bool matches = MatchesXboxCacheServiceFilter(service);
        Assert.Equal(expected, matches);
    }

    // Behavioral mirror of the production `%wsus%` OR `%xboxlive%` LIKE filter (lowercased tags).
    private static bool MatchesXboxCacheServiceFilter(string service) =>
        service.Contains("wsus", StringComparison.Ordinal)
        || service.Contains("xboxlive", StringComparison.Ordinal);
}
