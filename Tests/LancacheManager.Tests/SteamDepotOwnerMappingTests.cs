using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class SteamDepotOwnerMappingTests
{
    [Fact]
    public async Task PrefillMapping_DoesNotReplaceExistingPicsOwner()
    {
        var options = NewOptions();
        await using (var context = new AppDbContext(options))
        {
            context.SteamDepotMappings.Add(new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 1770480,
                AppName = "VTOL VR: AH-94",
                IsOwner = true,
                Source = "SteamKit2-PICS"
            });
            await context.SaveChangesAsync();
        }

        var service = new PrefillCacheService(
            new TestDbContextFactory(options),
            NullLogger<PrefillCacheService>.Instance);

        await service.RecordCachedDepotAsync(
            667970,
            1770481,
            4625979481897414804,
            "VTOL VR",
            1,
            null);

        await using var verification = new AppDbContext(options);
        var mappings = await verification.SteamDepotMappings
            .Where(mapping => mapping.DepotId == 1770481)
            .OrderBy(mapping => mapping.AppId)
            .ToListAsync();

        Assert.Equal(2, mappings.Count);
        Assert.Equal(1770480, Assert.Single(mappings, mapping => mapping.IsOwner).AppId);
        Assert.False(Assert.Single(mappings, mapping => mapping.AppId == 667970).IsOwner);
    }

    [Fact]
    public async Task DashboardNameEnrichment_ToleratesExistingDuplicateOwners()
    {
        var options = NewOptions();
        await using var context = new AppDbContext(options);
        context.SteamDepotMappings.AddRange(
            new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 1770480,
                AppName = "VTOL VR: AH-94",
                IsOwner = true,
                Source = "SteamKit2-PICS"
            },
            new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 667970,
                AppName = "VTOL VR",
                IsOwner = true,
                Source = "Prefill"
            });
        await context.SaveChangesAsync();

        var downloads = new List<Download>
        {
            new()
            {
                DepotId = 1770481,
                Service = "steam"
            }
        };

        await GameNameResolver.ResolveAsync(context, downloads, CancellationToken.None);

        var download = Assert.Single(downloads);
        Assert.Equal(1770480, download.GameAppId);
        Assert.Equal("VTOL VR: AH-94", download.GameName);
    }

    [Fact]
    public async Task UnknownGameResolution_ToleratesExistingDuplicateOwners()
    {
        var options = NewOptions();
        await using (var context = new AppDbContext(options))
        {
            context.SteamDepotMappings.AddRange(
                new SteamDepotMapping
                {
                    DepotId = 1770481,
                    AppId = 1770480,
                    AppName = "VTOL VR: AH-94",
                    IsOwner = true,
                    Source = "SteamKit2-PICS"
                },
                new SteamDepotMapping
                {
                    DepotId = 1770481,
                    AppId = 667970,
                    AppName = "VTOL VR",
                    IsOwner = true,
                    Source = "Prefill"
                });
            context.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 1770481,
                GameName = "Unknown Game (Depot 1770481)",
                DepotIdsJson = JsonSerializer.Serialize(new[] { 1770481u })
            });
            await context.SaveChangesAsync();
        }

        var service = new UnknownGameResolutionService(
            new TestDbContextFactory(options),
            NullLogger<UnknownGameResolutionService>.Instance);

        var resolvedCount = await service.ResolveUnknownGamesAsync(CancellationToken.None);

        Assert.Equal(1, resolvedCount);

        await using var verification = new AppDbContext(options);
        var game = Assert.Single(await verification.CachedGameDetections.ToListAsync());
        Assert.Equal(1770480, game.GameAppId);
        Assert.Equal("VTOL VR: AH-94", game.GameName);
    }

    /// <summary>
    /// The latest-downloads query used to join <c>SteamDepotMappings</c> in SQL, which emits the
    /// download once per owner row. A depot with two owners therefore returned the same download
    /// twice, doubling its bytes in the totals the downloads header and the dashboard panel add up
    /// client-side, and spending two of the caller's limit slots on one download.
    /// </summary>
    [Fact]
    public async Task LatestDownloads_ReturnOneRowPerDownloadWhenADepotHasTwoOwners()
    {
        var options = NewOptions();
        await using var context = new AppDbContext(options);
        context.SteamDepotMappings.AddRange(
            new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 1770480,
                AppName = "VTOL VR: AH-94",
                IsOwner = true,
                Source = "SteamKit2-PICS"
            },
            new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 667970,
                AppName = "VTOL VR",
                IsOwner = true,
                Source = "Prefill"
            });
        context.Downloads.Add(new Download
        {
            DepotId = 1770481,
            Service = "steam",
            CacheHitBytes = 1024,
            StartTimeUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();

        var downloads = await new StatsDataService(context).GetLatestDownloadsAsync();

        var download = Assert.Single(downloads);
        Assert.Equal(1024, download.CacheHitBytes);
        Assert.Equal("VTOL VR: AH-94", download.GameName);
        Assert.Equal(1770480, download.GameAppId);
    }

    /// <summary>
    /// A depot whose only <see cref="SteamDepotMapping"/> rows carry <c>IsOwner = false</c> - the
    /// state <see cref="PrefillCacheService"/> records when another app already owns the depot -
    /// has no owning app to name it, so the unknown-depot query must claim it as an
    /// <c>Unknown Game (Depot N)</c> row. Its mapped-depot sibling requires <c>IsOwner = true</c>,
    /// so an owner-blind test here drops that depot out of both game branches and its bytes
    /// become anonymous service bytes instead.
    /// The query is built inline against a live pool, so the predicate is pinned at the source
    /// the way <c>BareMetalDiskFeatureContractTests</c> pins the Rust named-removal core.
    /// The single-occurrence check keeps the SELECT collapsed: while it was written out once per
    /// sampling mode, a predicate could be added to one copy and silently missed on the other.
    /// </summary>
    [Fact]
    public void UnknownDepotQueryMatchesOnlyDepotsWithNoOwnedMapping()
    {
        var sql = ReadDetectionQuerySource();

        const string unknownDepotSelect =
            """SELECT le.\"Service\", le.\"DepotId\", le.\"Url\", MAX(le.\"BytesServed\")""";
        const string ownerBlindMappingTest =
            """NOT EXISTS ( SELECT 1 FROM \"SteamDepotMappings\" sdm WHERE sdm.\"DepotId\" = le.\"DepotId\" )""";

        Assert.Equal(1, sql.Split(unknownDepotSelect).Length - 1);
        Assert.Contains(OwnedMappingPredicate, sql, StringComparison.Ordinal);
        Assert.DoesNotContain(ownerBlindMappingTest, sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unknown-depot gate and the service query's Steam gate are a matched pair and have to
    /// stay disjoint. The unknown branch claims every depot row with no owned mapping and the
    /// mapped branch claims the rest, so the service query must leave depot rows alone entirely
    /// and take only rows carrying no depot id. Letting it match them again would not double-count
    /// bytes - <c>GamesOnDiskCalculator.ComputeAttributedCacheFromDisk</c> walks games
    /// before services over one shared path set - but it would leave the steam service row
    /// reporting the file count of objects whose bytes the game claimed, because
    /// <c>RefreshDiskSummaryCoreAsync</c> recomputes a service's <c>TotalSizeBytes</c> and keeps
    /// its scan-time <c>CacheFilesFound</c>. Editing either gate alone reopens that, so both are
    /// asserted together here.
    /// </summary>
    [Fact]
    public void ServiceQueryLeavesEveryDepotRowToTheGameQueries()
    {
        var sql = ReadDetectionQuerySource();

        Assert.Contains(OwnedMappingPredicate, sql, StringComparison.Ordinal);
        Assert.Contains(
            """AND le.\"Service\" != '' AND le.\"DepotId\" IS NULL""",
            sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The sampled path caps URLs per depot by counting rows and resetting that count whenever the
    /// depot id changes, so it caps nothing unless a depot's rows arrive together. Both paths share
    /// one <c>ORDER BY le."DepotId"</c> push for that reason. Nothing calls the sampled path today
    /// - the single caller passes <c>None</c> - so no run would reveal a lost ordering.
    /// </summary>
    [Fact]
    public void UnknownDepotQueryOrdersByDepotForItsPerDepotCap()
    {
        var sql = ReadDetectionQuerySource();

        Assert.Contains(
            """
            GROUP BY le.\"DepotId\", le.\"Url\", le.\"Service\" ORDER BY le.\"DepotId\"
            """,
            sql,
            StringComparison.Ordinal);

        // The sampled path used to group and then LIMIT with no ordering in between.
        Assert.DoesNotContain(
            """GROUP BY le.\"DepotId\", le.\"Url\", le.\"Service\" LIMIT""",
            sql,
            StringComparison.Ordinal);
    }

    private const string OwnedMappingPredicate =
        """NOT EXISTS ( SELECT 1 FROM \"SteamDepotMappings\" sdm WHERE sdm.\"DepotId\" = le.\"DepotId\" AND sdm.\"IsOwner\" = true )""";

    /// <summary>
    /// The detection queries are built inline against a live pool, so they are asserted as source
    /// text. Whitespace is collapsed so reindenting the SQL cannot break a contract.
    /// </summary>
    private static string ReadDetectionQuerySource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        var source = File.ReadAllText(Path.Combine(root, "rust-processor", "src", "cache_detect_queries.rs"));

        return string.Join(' ', source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"steam_depot_owner_{Guid.NewGuid():N}")
            .Options;
}
