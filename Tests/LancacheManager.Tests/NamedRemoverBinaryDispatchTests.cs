using LancacheManager.Infrastructure.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the per-service dispatch in <see cref="PathResolverBase.GetRustNamedGameRemoverPath(string)"/>.
///
/// The name-keyed removal bins were consolidated into five thin per-service binaries over a
/// shared core (cache_steam_remove / cache_epic_remove / cache_blizzard_remove /
/// cache_riot_remove / cache_xbox_remove). C# no longer passes the owning service as a Rust
/// positional arg; instead each named service (Blizzard/Riot/Xbox) is routed to its OWN binary
/// here. These tests pin that routing so a future rename or a missing service arm fails fast at
/// build/test time rather than at runtime when a user removes a single named game.
/// </summary>
public class NamedRemoverBinaryDispatchTests
{
    /// <summary>
    /// Minimal concrete resolver with a fixed, recognizable extension so the asserted binary
    /// basename is unambiguous regardless of host OS. Only the members the base ctor / the
    /// resolver under test require are implemented.
    /// </summary>
    private sealed class TestPathResolver : PathResolverBase
    {
        public TestPathResolver(ILogger logger) : base(logger) { }

        protected override string BasePath => "/test";
        protected override string RustExecutableExtension => ".test";

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }

    private static TestPathResolver CreateResolver() => new(NullLogger.Instance);

    [Theory]
    [InlineData("blizzard", "cache_blizzard_remove")]
    [InlineData("riot", "cache_riot_remove")]
    [InlineData("xbox", "cache_xbox_remove")]
    public void GetRustNamedGameRemoverPath_RoutesEachServiceToOwnBinary(string service, string expectedBinary)
    {
        var path = CreateResolver().GetRustNamedGameRemoverPath(service);

        Assert.Equal($"{expectedBinary}.test", Path.GetFileName(path));
    }

    [Theory]
    [InlineData("Blizzard")]
    [InlineData("RIOT")]
    [InlineData("Xbox")]
    public void GetRustNamedGameRemoverPath_IsCaseInsensitive(string service)
    {
        // The owning service may arrive in any casing; the resolver lowercases before matching.
        var path = CreateResolver().GetRustNamedGameRemoverPath(service);

        Assert.Equal($"cache_{service.ToLowerInvariant()}_remove.test", Path.GetFileName(path));
    }

    [Theory]
    [InlineData("steam")]   // Steam has its own dedicated route/binary, not the named dispatch.
    [InlineData("epic")]    // Epic likewise.
    [InlineData("unknown")]
    [InlineData("")]
    public void GetRustNamedGameRemoverPath_ThrowsForUnregisteredService(string service)
    {
        Assert.Throws<ArgumentException>(() => CreateResolver().GetRustNamedGameRemoverPath(service));
    }
}
