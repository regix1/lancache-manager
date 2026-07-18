namespace LancacheManager.Tests;

public sealed class BareMetalDiskFeatureContractTests
{
    [Fact]
    public void NamedGameRemovalHasNoRiotSpecificDenial()
    {
        // Per-game Riot removal works on bare-metal exactly as on monolithic: the game -> URL
        // mapping is materialized in the database at ingest (the CDN host names the game), so
        // removal targets that game's URLs and the cache key never needs to encode the host.
        // Any reintroduction of a Riot-specific block would wrongly diverge from monolithic.
        var namedRemoval = ReadSource("Core", "Services", "CacheManagementService.NamedRemoval.cs");
        var controller = ReadSource("Controllers", "GamesController.cs");

        Assert.DoesNotContain("EnsureNamedGameRemovalSupported", namedRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "per-game Riot removal is not available for bare-metal",
            namedRemoval,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "per-game Riot removal is not available for bare-metal",
            controller,
            StringComparison.Ordinal);

        // Guard the Rust side too: reintroducing a bare-metal Riot bail in the shared
        // named-removal core would silently diverge from monolithic without this check.
        var rustNamedRemove = ReadRepoFile("rust-processor", "src", "named_remove_core.rs");
        Assert.DoesNotContain("Per-game Riot removal is not available", rustNamedRemove, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "service == \"riot\"\n        && cache_utils::active_key_scheme()",
            rustNamedRemove,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EvictionDatasourceConfigIncludesKeyScheme()
    {
        var source = ReadSource("Core", "Services", "CacheReconciliationService.cs");

        Assert.Contains(
            "keyScheme = _capabilityService.GetKeySchemeWireValue(ds)",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathSegments) =>
        ReadRepoFile(["Api", "LancacheManager", .. pathSegments]);

    private static string ReadRepoFile(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, .. pathSegments]));
    }
}
