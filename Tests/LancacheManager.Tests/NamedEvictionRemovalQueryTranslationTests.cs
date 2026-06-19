using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the LINQ shapes used by the <c>EvictionScope.Named</c> arms in
/// <c>CacheReconciliationService.RemoveEvictedRecordsForEntityAsync</c> /
/// <c>PurgeLogEntriesForEntityAsync</c>. Named (Blizzard/Riot) games carry no Steam/Epic
/// AppId; their identity is (Service, GameName), with two non-obvious invariants the arms
/// depend on:
///   - Downloads rows have <c>GameAppId == null</c> and <c>EpicAppId == null</c>.
///   - CachedGameDetection rows have <c>GameAppId == 0</c> (never null) and <c>EpicAppId == null</c>.
/// <c>ToQueryString()</c> compiles each predicate through the Npgsql provider without opening a
/// connection, so a translation regression (e.g. an in-memory-only <c>string.Equals</c> overload)
/// fails here instead of at runtime when a user removes a single named game's evicted records.
/// </summary>
public class NamedEvictionRemovalQueryTranslationTests
{
    private const string ServiceKeyLower = "blizzard";
    private const string GameName = "Diablo IV";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=named_eviction_translation_smoke_test")
            .Options);

    [Fact]
    public void NamedEvictedDownloadsQuery_TranslatesToSql()
    {
        using var context = CreateContext();

        // Mirrors the EvictionScope.Named Downloads-delete / PurgeLogEntries arm.
        var sql = context.Downloads
            .Where(d => d.IsEvicted
                     && d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service == ServiceKeyLower
                     && d.GameName == GameName)
            .Select(d => d.Id)
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedEvictedDetectionDeleteQuery_TranslatesToSql()
    {
        using var context = CreateContext();

        // Mirrors the EvictionScope.Named CachedGameDetection delete arm (GameAppId == 0,
        // Service compared lowercased, GameName case-sensitive).
        var sql = context.CachedGameDetections
            .Where(g => g.GameAppId == 0
                     && g.EpicAppId == null
                     && g.Service != null
                     && g.Service.ToLower() == ServiceKeyLower
                     && g.GameName == GameName)
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NamedEvictedDetectionUnevictQuery_TranslatesToSql()
    {
        using var context = CreateContext();

        // Mirrors the EvictionScope.Named CachedGameDetection IsEvicted-clear arm (partial case).
        var sql = context.CachedGameDetections
            .Where(g => g.IsEvicted
                     && g.GameAppId == 0
                     && g.EpicAppId == null
                     && g.Service != null
                     && g.Service.ToLower() == ServiceKeyLower
                     && g.GameName == GameName)
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
    }
}
