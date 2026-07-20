using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Behavioural coverage for the service-detection positive-evidence eviction gate
/// (GameCacheDetectionDataService.SaveServicesAsync) and the Downloads-keyed service
/// self-heal (CacheReconciliationService.UnevictCachedServiceDetectionsAsync →
/// GameCacheDetectionDataService.GetServicesToUnevictAsync). Before the fix, a service
/// ABSENT from a full-scan report was blind-evicted with zero disk evidence; now it is
/// only badged Evicted when ALL of its Downloads are already evicted. These tests run
/// against the EF Core InMemory provider so the actual production code paths execute.
///
/// A separate Npgsql translation smoke test (RetroQueryTranslationTests-style) guards
/// the new servicesWithLiveDownloads query shape against a LINQ-to-SQL regression.
/// </summary>
public class ServiceEvictionGateTests
{
    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private static DbContextOptions<AppDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"service_evict_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static Download ServiceDownload(string service, bool evicted, long? depotId = null)
        => new Download
        {
            Service = service,
            ClientIp = "10.0.0.1",
            StartTimeUtc = DateTime.UtcNow,
            StartTimeLocal = DateTime.UtcNow,
            EndTimeUtc = DateTime.UtcNow,
            EndTimeLocal = DateTime.UtcNow,
            CacheHitBytes = 1,
            CacheMissBytes = 1,
            IsActive = false,
            IsEvicted = evicted,
            GameAppId = null,
            EpicAppId = null,
            DepotId = depotId,
            Datasource = "default"
        };

    /// <summary>
    /// A service-scoped Download that carries a named-game name (Blizzard/Riot path:
    /// GameAppId/EpicAppId null, Service set, GameName populated). Riot's entire cache is made
    /// of these (League of Legends / Valorant / Legends of Runeterra).
    /// </summary>
    private static Download NamedGameServiceDownload(string service, string gameName, bool evicted)
        => new Download
        {
            Service = service,
            ClientIp = "10.0.0.1",
            StartTimeUtc = DateTime.UtcNow,
            StartTimeLocal = DateTime.UtcNow,
            EndTimeUtc = DateTime.UtcNow,
            EndTimeLocal = DateTime.UtcNow,
            CacheHitBytes = 1,
            CacheMissBytes = 1,
            IsActive = false,
            IsEvicted = evicted,
            GameAppId = null,
            EpicAppId = null,
            GameName = gameName,
            Datasource = "default"
        };

    /// <summary>
    /// A zero-byte service-scoped Download (aborted or metadata-only session - wsus accumulates
    /// these constantly). Never flagged evicted because it never proved cache content existed,
    /// and for the same reason it must be NEUTRAL in every alive/veto/re-cache-evidence test.
    /// </summary>
    private static Download ZeroByteServiceDownload(string service)
    {
        var download = ServiceDownload(service, evicted: false);
        download.CacheHitBytes = 0;
        download.CacheMissBytes = 0;
        return download;
    }

    private static CachedServiceDetection EvictedServiceRow(string serviceName)
        => new CachedServiceDetection
        {
            ServiceName = serviceName,
            CacheFilesFound = 0,
            TotalSizeBytes = 0,
            IsEvicted = true,
            LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
        };

    /// <summary>
    /// Criterion 1 + 3: a service ABSENT from the scan results but with at least one
    /// non-evicted Download must NOT be flipped to IsEvicted. This is the wsus
    /// false-positive case. In this scan false-negative branch the last-known snapshot
    /// columns (CacheFilesFound / TotalSizeBytes) are deliberately PRESERVED, not zeroed:
    /// zeroing them would drop the row below the frontend's active-list filter
    /// (getActiveServices requires cache_files_found > 0) and make the service silently
    /// vanish until the next full rescan re-detects it (see SaveServicesAsync's
    /// "scan false-negative" branch). The Downloads-keyed self-heal reconciles later.
    /// </summary>
    [Fact]
    public async Task SaveServices_AbsentServiceWithLiveDownload_IsNotEvicted_ButSnapshotRefreshedAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "wsus",
                CacheFilesFound = 42,
                TotalSizeBytes = 1000,
                IsEvicted = false,
                LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            // A live (non-evicted) Download proves the files are believed present on disk.
            seed.Downloads.Add(ServiceDownload("wsus", evicted: false));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        // Empty scan results => wsus is "absent" from this scan.
        await dataService.SaveServicesAsync(new List<ServiceCacheInfo>());

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "wsus");
        Assert.False(row.IsEvicted); // NOT blind-evicted - has a live Download
        // Scan false-negative branch PRESERVES the last-known counts (does not zero them) so the
        // service stays on the active list until a full rescan re-detects it.
        Assert.Equal(42, row.CacheFilesFound);
        Assert.Equal(1000UL, row.TotalSizeBytes);
    }

    /// <summary>
    /// Criterion 2: a service ABSENT from the scan results whose Downloads are ALL evicted
    /// must still be marked IsEvicted (no regression of legitimate eviction).
    /// </summary>
    [Fact]
    public async Task SaveServices_AbsentServiceWithAllDownloadsEvicted_IsEvictedAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "steam",
                CacheFilesFound = 5,
                TotalSizeBytes = 500,
                IsEvicted = false,
                LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            // All Downloads for this service are already evicted => positive eviction evidence.
            seed.Downloads.Add(ServiceDownload("steam", evicted: true));
            seed.Downloads.Add(ServiceDownload("steam", evicted: true));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.SaveServicesAsync(new List<ServiceCacheInfo>());

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "steam");
        Assert.True(row.IsEvicted);
        Assert.Equal(0, row.CacheFilesFound);
    }

    /// <summary>
    /// Criterion 2 (no-live-Downloads sub-case): a service ABSENT from the scan with NO
    /// Downloads at all has no live evidence, so it is still evicted (matches prior behaviour
    /// for the genuinely-gone case).
    /// </summary>
    [Fact]
    public async Task SaveServices_AbsentServiceWithNoDownloads_IsEvictedAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(EvictedServiceRow("origin"));
            // origin's row currently IsEvicted=true; no Downloads exist for it.
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.SaveServicesAsync(new List<ServiceCacheInfo>());

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "origin");
        Assert.True(row.IsEvicted);
    }

    /// <summary>
    /// Criteria 5 + 7: a re-cached service is selected for self-heal by the Downloads-keyed
    /// helper. The CachedServiceDetection is IsEvicted=true with a STALE CacheFilesFound=0
    /// snapshot, but a matching Download is now !IsEvicted. The old self-heal keyed off
    /// CacheFilesFound>0 and would have missed this; GetServicesToUnevictAsync keys off
    /// Downloads.IsEvicted and returns the re-cached service name. (The subsequent
    /// ExecuteUpdateAsync that clears the flag is the same proven shape as the games path and
    /// is guarded separately by the Npgsql translation smoke test below; the InMemory provider
    /// does not support ExecuteUpdate, so the selection helper is exercised directly here.)
    /// </summary>
    [Fact]
    public async Task GetServicesToUnevict_RecachedService_IsReturnedAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            // Stale snapshot: CacheFilesFound=0 (would defeat the old CacheFilesFound>0 filter).
            seed.CachedServiceDetections.Add(EvictedServiceRow("wsus"));
            // Download re-cached: IsEvicted=false now.
            seed.Downloads.Add(ServiceDownload("wsus", evicted: false));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await using var context = new AppDbContext(options);
        var toUnevict = await dataService.GetServicesToUnevictAsync(context, CancellationToken.None);

        Assert.Contains("wsus", toUnevict);
    }

    /// <summary>
    /// Criterion 5 (negative): a service whose Downloads are all still evicted must NOT be
    /// returned for self-heal (the helper must not over-clear).
    /// </summary>
    [Fact]
    public async Task GetServicesToUnevict_AllDownloadsStillEvicted_IsNotReturnedAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(EvictedServiceRow("steam"));
            seed.Downloads.Add(ServiceDownload("steam", evicted: true));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await using var context = new AppDbContext(options);
        var toUnevict = await dataService.GetServicesToUnevictAsync(context, CancellationToken.None);

        Assert.DoesNotContain("steam", toUnevict);
    }

    /// <summary>
    /// Zero-byte veto regression (the "wsus never shows as evicted" bug): a service whose every
    /// BYTE-BACKED Download is evicted must be recovered into the Evicted Items projection even
    /// when non-evicted zero-byte Downloads exist. Before the fix one aborted session vetoed the
    /// all-evicted test forever, so the service could never surface (and never be removed).
    /// </summary>
    [Fact]
    public async Task RecoverEvictedServices_ZeroByteSiblingDoesNotVetoAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(ServiceDownload("wsus", evicted: true));
            seed.Downloads.Add(ZeroByteServiceDownload("wsus"));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        var recovered = await dataService.RecoverEvictedServicesAsync();

        Assert.Equal(1, recovered);
        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "wsus");
        Assert.True(row.IsEvicted);
        Assert.Equal(0, row.CacheFilesFound);
    }

    /// <summary>
    /// Zero-byte alive-test companion: a service ABSENT from the scan whose only non-evicted
    /// Downloads are zero-byte must still flip to IsEvicted - zero-byte rows are not alive
    /// evidence, so this case behaves like the all-downloads-evicted case.
    /// </summary>
    [Fact]
    public async Task SaveServices_AbsentServiceWithOnlyZeroByteLiveDownloads_IsEvictedAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "wsus",
                CacheFilesFound = 5,
                TotalSizeBytes = 500,
                IsEvicted = false,
                LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            seed.Downloads.Add(ServiceDownload("wsus", evicted: true));
            seed.Downloads.Add(ZeroByteServiceDownload("wsus"));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.SaveServicesAsync(new List<ServiceCacheInfo>());

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "wsus");
        Assert.True(row.IsEvicted);
        Assert.Equal(0, row.CacheFilesFound);
    }

    /// <summary>
    /// Zero-byte self-heal companion: a zero-byte non-evicted Download is not re-cache evidence,
    /// so it must NOT un-evict a service. Without this, the same aborted session that used to
    /// block eviction would instead clear the badge right back off after recovery.
    /// </summary>
    [Fact]
    public async Task GetServicesToUnevict_ZeroByteDownloadIsNotRecacheEvidenceAsync()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(EvictedServiceRow("wsus"));
            seed.Downloads.Add(ServiceDownload("wsus", evicted: true));
            seed.Downloads.Add(ZeroByteServiceDownload("wsus"));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await using var context = new AppDbContext(options);
        var toUnevict = await dataService.GetServicesToUnevictAsync(context, CancellationToken.None);

        Assert.DoesNotContain("wsus", toUnevict);
    }

    /// <summary>
    /// Named-game false-eviction regression (the "riot" bug, but SERVICE-AGNOSTIC): a service
    /// whose ONLY Downloads are named-game (GameName set) and NOT evicted must count as ALIVE and
    /// therefore must NOT be flipped to IsEvicted when absent from the scan. Before the fix the
    /// alive test filtered GameName==null, so these services had zero "live" downloads and were
    /// blind-evicted despite their full cache being present on disk. Parameterized over multiple
    /// service names to prove the production code contains NO per-service allowlist.
    /// </summary>
    [Theory]
    [InlineData("riot")]
    [InlineData("blizzard")]
    [InlineData("riotgames")]
    public async Task SaveServices_AbsentServiceWithOnlyNamedGameLiveDownloads_IsNotEvictedAsync(string serviceName)
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = serviceName,
                CacheFilesFound = 10,
                TotalSizeBytes = 1000,
                IsEvicted = false,
                LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            // Every Download for this service maps to a named game and is present (not evicted) on disk.
            seed.Downloads.Add(NamedGameServiceDownload(serviceName, "Game Title A", evicted: false));
            seed.Downloads.Add(NamedGameServiceDownload(serviceName, "Game Title B", evicted: false));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        // Empty scan results => service is "absent" from this scan.
        await dataService.SaveServicesAsync(new List<ServiceCacheInfo>());

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == serviceName);
        Assert.False(row.IsEvicted); // alive via named-game Downloads - NOT blind-evicted
    }

    /// <summary>
    /// Named-game self-heal (SERVICE-AGNOSTIC): a falsely-evicted service whose named-game cache
    /// is back on disk (Downloads !IsEvicted, GameName set) must be returned by
    /// GetServicesToUnevictAsync so it un-evicts. Before the fix the GameName==null filter blocked
    /// self-heal entirely. Parameterized to prove no per-service hardcoding.
    /// </summary>
    [Theory]
    [InlineData("riot")]
    [InlineData("blizzard")]
    [InlineData("riotgames")]
    public async Task GetServicesToUnevict_RecachedNamedGameService_IsReturnedAsync(string serviceName)
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(EvictedServiceRow(serviceName));
            seed.Downloads.Add(NamedGameServiceDownload(serviceName, "Game Title A", evicted: false));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await using var context = new AppDbContext(options);
        var toUnevict = await dataService.GetServicesToUnevictAsync(context, CancellationToken.None);

        Assert.Contains(serviceName, toUnevict);
    }

    /// <summary>
    /// Criterion 2 (named-game, all-evicted sub-case — SERVICE-AGNOSTIC): a service whose
    /// named-game Downloads are ALL evicted must still be marked Evicted - the fix must not make
    /// genuinely-gone services un-evictable. Parameterized to prove the eviction path has no
    /// per-service special-casing either.
    /// </summary>
    [Theory]
    [InlineData("riot")]
    [InlineData("blizzard")]
    [InlineData("riotgames")]
    public async Task SaveServices_AbsentServiceWithAllNamedGameDownloadsEvicted_IsEvictedAsync(string serviceName)
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = serviceName,
                CacheFilesFound = 5,
                TotalSizeBytes = 500,
                IsEvicted = false,
                LastDetectedUtc = DateTime.UtcNow.AddHours(-1),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            seed.Downloads.Add(NamedGameServiceDownload(serviceName, "Game Title A", evicted: true));
            seed.Downloads.Add(NamedGameServiceDownload(serviceName, "Game Title B", evicted: true));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.SaveServicesAsync(new List<ServiceCacheInfo>());

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == serviceName);
        Assert.True(row.IsEvicted);
    }

    /// <summary>
    /// Criterion 4: the new servicesWithLiveDownloads query (top-level Where/Select/Distinct
    /// selecting the RAW Service string - lowercasing happens in C# with ToLowerInvariant() after
    /// materializing, to stay keyed consistently with existingDict/incomingByName) compiles through
    /// the Npgsql provider without opening a connection, so a translation regression (e.g. an
    /// accidental ToLowerInvariant or nested projection inside the query) fails here instead of at
    /// runtime. Mirrors the RetroQueryTranslationTests convention.
    /// </summary>
    [Fact]
    public void ServicesWithLiveDownloadsQuery_TranslatesToSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=service_evict_translation_smoke")
            .Options;

        using var context = new AppDbContext(options);

        // Shape mirrors SaveServicesAsync.servicesWithLiveDownloads: raw Service select, lowered
        // in C#, and only byte-backed rows count as alive evidence.
        var liveQuery = context.Downloads
            .Where(d => d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && !d.IsEvicted
                     && (d.CacheHitBytes > 0 || d.CacheMissBytes > 0))
            .Select(d => d.Service!)
            .Distinct();

        var liveSql = liveQuery.ToQueryString();
        Assert.Contains("SELECT", liveSql, StringComparison.OrdinalIgnoreCase);

        // Shape mirrors GetServicesToUnevictAsync second query (the .Contains over a
        // materialized name list is exercised with a constant list to keep it translatable).
        var evictedNames = new List<string> { "wsus", "steam" };
        var unevictQuery = context.Downloads
            .Where(d => d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && !d.IsEvicted
                     && (d.CacheHitBytes > 0 || d.CacheMissBytes > 0)
                     && evictedNames.Contains(d.Service!.ToLower()))
            .Select(d => d.Service!.ToLower())
            .Distinct();

        var unevictSql = unevictQuery.ToQueryString();
        Assert.Contains("SELECT", unevictSql, StringComparison.OrdinalIgnoreCase);

        // Shape mirrors the CacheReconciliationService.EvictCachedGameDetectionsAsync group gate:
        // at least one evicted row plus zero-byte rows treated as neutral. Grouped Any/All is the
        // most fragile translation in the family, so compile it explicitly.
        var appIds = new List<long> { 1L, 2L };
        var groupGateQuery = context.Downloads
            .Where(d => d.GameAppId != null && appIds.Contains(d.GameAppId.Value))
            .GroupBy(d => d.GameAppId!.Value)
            .Where(g => g.Any(d => d.IsEvicted)
                     && g.All(d => d.IsEvicted || (d.CacheHitBytes == 0 && d.CacheMissBytes == 0)))
            .Select(g => g.Key);

        var groupGateSql = groupGateQuery.ToQueryString();
        Assert.Contains("SELECT", groupGateSql, StringComparison.OrdinalIgnoreCase);

        // Shape mirrors the RecoverEvictedServicesAsync veto subquery: only a byte-backed
        // non-evicted sibling blocks recovery.
        var recoveryQuery = context.Downloads
            .Where(d => d.IsEvicted
                     && d.GameAppId == null
                     && d.EpicAppId == null
                     && d.Service != null
                     && d.GameName == null
                     && !context.Downloads.Any(other =>
                         other.GameAppId == null
                         && other.EpicAppId == null
                         && other.Service == d.Service
                         && other.GameName == null
                         && !other.IsEvicted
                         && (other.CacheHitBytes > 0 || other.CacheMissBytes > 0)))
            .GroupBy(d => d.Service!)
            .Select(g => new { ServiceName = g.Key, Datasource = g.Min(d => d.Datasource) });

        var recoverySql = recoveryQuery.ToQueryString();
        Assert.Contains("SELECT", recoverySql, StringComparison.OrdinalIgnoreCase);
    }
}
