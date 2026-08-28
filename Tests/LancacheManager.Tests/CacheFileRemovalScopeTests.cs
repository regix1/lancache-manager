using System.Text.RegularExpressions;
using LancacheManager.Controllers;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the separation between the two removal scopes. The Evicted Items panel deletes download
/// and log records; the Game Cache Detection list deletes cached files. A row there once showed
/// "0 files" while its service still held hundreds of gigabytes on disk, and its trash button
/// reached the full service wipe, so these cover both halves: the routes that unlink files refuse
/// a request that did not declare that scope, and the evicted panel no longer calls them.
/// </summary>
public sealed partial class CacheFileRemovalScopeTests
{
    private static CacheController NewCacheController() => new CacheController(
        cacheService: null!,
        cacheClearingService: null!,
        corruptionDetectionService: null!,
        logger: NullLogger<CacheController>.Instance,
        pathResolver: null!,
        notifications: null!,
        rustProcessHelper: null!,
        nginxLogRotationService: null!,
        operationTracker: null!,
        datasourceService: null!,
        dbContextFactory: null!,
        reconciliationService: null!,
        conflictChecker: null!,
        operationQueue: null!,
        capabilityService: null!);

    private static GamesController NewGamesController() => new GamesController(
        gameCacheDetectionService: null!,
        cacheManagementService: null!,
        notifications: null!,
        logger: NullLogger<GamesController>.Instance,
        pathResolver: null!,
        operationTracker: null!,
        conflictChecker: null!,
        operationQueue: null!,
        capabilityService: null!);

    [Fact]
    public async Task ClearServiceCache_WithEvictedRecordsScope_IsRefusedAsync()
    {
        var result = await NewCacheController().ClearServiceCacheAsync(
            "steam",
            CancellationToken.None,
            CacheRemovalScope.EvictedRecords);

        AssertRefused(result);
    }

    [Fact]
    public async Task ClearServiceCache_WithNoDeclaredScope_IsRefusedAsync()
    {
        var result = await NewCacheController().ClearServiceCacheAsync("steam", CancellationToken.None);

        AssertRefused(result);
    }

    [Fact]
    public async Task RemoveGameFromCache_WithEvictedRecordsScope_IsRefusedAsync()
    {
        var result = await NewGamesController().RemoveGameFromCacheAsync(
            570,
            CancellationToken.None,
            CacheRemovalScope.EvictedRecords);

        AssertRefused(result);
    }

    [Fact]
    public async Task RemoveEpicGameFromCache_WithNoDeclaredScope_IsRefusedAsync()
    {
        var result = await NewGamesController().RemoveEpicGameFromCacheAsync(
            "Fortnite",
            CancellationToken.None);

        AssertRefused(result);
    }

    [Fact]
    public async Task RemoveNamedGameFromCache_WithNoDeclaredScope_IsRefusedAsync()
    {
        var result = await NewGamesController().RemoveNamedGameFromCacheAsync(
            "blizzard",
            "Diablo IV",
            CancellationToken.None);

        AssertRefused(result);
    }

    [Fact]
    public void EvictedItemsPanel_NeverReachesACacheFileRemoval()
    {
        var source = ReadWebSource(
            "components", "features", "management", "sections", "StorageSection.tsx");

        Assert.DoesNotContain("runTrackedGameRemoval", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runTrackedServiceRemoval", source, StringComparison.Ordinal);
        Assert.DoesNotContain("removeGameFromCache", source, StringComparison.Ordinal);
        Assert.DoesNotContain("removeServiceFromCache", source, StringComparison.Ordinal);
        Assert.Contains("ApiService.removeEvictedForService(", source, StringComparison.Ordinal);
        Assert.Contains("ApiService.removeEvictedForEpicGame(", source, StringComparison.Ordinal);
        Assert.Contains("ApiService.removeEvictedForNamedGame(", source, StringComparison.Ordinal);
        Assert.Contains("ApiService.removeEvictedForGame(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheFileRemovalCalls_DeclareTheCacheFileScope()
    {
        var source = ReadWebSource("services", "api.service.ts");

        AssertMatches(ScopeConstantRegex(), source, "the removal scope constant");
        AssertMatches(GameRouteRegex(), source, "the Steam game route carrying that scope");
        AssertMatches(EpicGameRouteRegex(), source, "the Epic game route carrying that scope");
        AssertMatches(NamedGameRouteRegex(), source, "the named game route carrying that scope");
        AssertMatches(ServiceRouteRegex(), source, "the service route carrying that scope");
    }

    /// <summary>
    /// Matches with whitespace tolerance wherever the formatter can rewrap a line, so reformatting
    /// api.service.ts cannot fail a backend test.
    /// </summary>
    private static void AssertMatches(Regex pattern, string source, string expected) =>
        Assert.True(
            pattern.IsMatch(source),
            $"api.service.ts no longer contains {expected}. If it moved or was renamed, this guard "
                + "must be pointed at its new home rather than deleted.");

    private static void AssertRefused(IActionResult result)
    {
        var refusal = Assert.IsType<BadRequestObjectResult>(result);
        var message = Assert.IsType<ErrorResponse>(refusal.Value).Error;
        Assert.Contains(CacheRemovalScope.CacheFiles, message, StringComparison.Ordinal);
    }

    private static string ReadWebSource(params string[] pathSegments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), "Web", "src", .. pathSegments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    [GeneratedRegex(@"const\s+CACHE_FILE_REMOVAL_SCOPE\s*=\s*['""]scope=" + CacheRemovalScope.CacheFiles + @"['""]")]
    private static partial Regex ScopeConstantRegex();

    [GeneratedRegex(@"/games/\$\{\s*gameAppId\s*\}\?\$\{\s*CACHE_FILE_REMOVAL_SCOPE\s*\}")]
    private static partial Regex GameRouteRegex();

    [GeneratedRegex(@"/games/epic/\$\{\s*encodeURIComponent\(\s*gameName\s*\)\s*\}\?\$\{\s*CACHE_FILE_REMOVAL_SCOPE\s*\}")]
    private static partial Regex EpicGameRouteRegex();

    [GeneratedRegex(@"/games/named/\$\{\s*encodeURIComponent\(\s*service\s*\)\s*\}/\$\{\s*encodeURIComponent\(\s*gameName\s*\)\s*\}\?\$\{\s*CACHE_FILE_REMOVAL_SCOPE\s*\}")]
    private static partial Regex NamedGameRouteRegex();

    [GeneratedRegex(@"/cache/services/\$\{\s*encodeURIComponent\(\s*serviceName\s*\)\s*\}\?\$\{\s*CACHE_FILE_REMOVAL_SCOPE\s*\}")]
    private static partial Regex ServiceRouteRegex();
}
