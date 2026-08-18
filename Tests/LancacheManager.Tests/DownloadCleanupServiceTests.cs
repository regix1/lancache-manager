using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the orphaned-service cleanup FK crash and the Xbox data-loss guard.
///
/// Root cause (see DownloadCleanupService.CleanupOrphanedServicesCoreAsync): the cleanup classified
/// any <c>Downloads.Service</c> absent from the log-file service names as orphaned and deleted its
/// children by Service NAME before deleting the Downloads. Xbox uses a cache-split identity -
/// <c>Downloads.Service='xbox'</c> but its cache LogEntries live under <c>'wsus'</c> - so (1) 'xbox'
/// was always flagged orphaned, and (2) the by-name child delete missed the 'wsus' LogEntries that
/// reference the xbox Downloads via <c>DownloadId</c>, so deleting the parent Downloads violated
/// <c>FK_LogEntries_Downloads_DownloadId</c> (NO ACTION) - PostgreSQL 23503.
///
/// The fix: (a) nullify child LogEntries by <c>DownloadId</c> (not by Service name) before deleting
/// the parents, and (b) treat a cache-split service as in-use when its alias appears in the logs so
/// Xbox is never flagged orphaned.
///
/// The pure classification tests exercise the data-loss guard with no provider. The integration
/// tests run the real ExecuteUpdate/ExecuteDelete cleanup against EF Core's Sqlite provider with
/// foreign keys enforced - the only way to reproduce the FK crash, since the InMemory provider
/// neither supports ExecuteUpdate/ExecuteDelete nor enforces foreign keys. On the PRE-FIX code these
/// integration tests throw the FK violation (the cleanup) / delete the xbox Download (data loss);
/// post-fix they complete cleanly and Xbox survives.
/// </summary>
public class DownloadCleanupServiceTests
{
    // ---------------------------------------------------------------------------------------------
    // Pure classification - data-loss guard (no DB provider needed)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ComputeOrphanedServices_XboxPresentViaWsusAlias_NotOrphaned()
    {
        // 'xbox' is absent from the logs under its own name, but its cache lives under 'wsus', which
        // IS present -> xbox must NOT be flagged orphaned (else every Xbox download gets deleted).
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam", "wsus" });

        Assert.DoesNotContain("xbox", orphans);
        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxOnlyService_PresentViaWsusAlias_NotOrphaned()
    {
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox" },
            new HashSet<string> { "wsus" });

        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxWithoutWsusInLogs_IsOrphaned()
    {
        // The alias only protects xbox while its cache (wsus) is still present. With wsus gone too,
        // xbox is genuinely orphaned - the guard is conditional, not an unconditional whitelist.
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam" });

        Assert.Contains("xbox", orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_GenuineOrphanDetected_PresentServiceKept()
    {
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "origin", "steam" },
            new HashSet<string> { "steam", "wsus" });

        Assert.Contains("origin", orphans);
        Assert.DoesNotContain("steam", orphans);
    }

    // ---------------------------------------------------------------------------------------------
    // Integration - real cleanup against Sqlite with foreign keys enforced
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_XboxCacheSplit_NotDeleted_AndNoFkViolation()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        long xboxId;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            // 'steam' is a present service so the "all services orphaned" safety check does not trip.
            var steam = NewDownload("steam");
            var xbox = NewDownload("xbox");
            seed.Downloads.AddRange(steam, xbox);
            await seed.SaveChangesAsync();

            xboxId = xbox.Id;

            // Xbox cache LogEntry is recorded under 'wsus' and references the xbox Download by FK.
            seed.LogEntries.Add(NewLogEntry("wsus", xboxId));
            await seed.SaveChangesAsync();
        }

        // 'xbox' never appears in log-file service names; only steam + wsus do.
        var logServices = new HashSet<string> { "steam", "wsus" };

        await using (var run = new AppDbContext(options))
        {
            // PRE-FIX: 'xbox' is flagged orphaned and the parent Downloads delete throws the FK
            // violation (the wsus child is not matched by Service name). POST-FIX: the wsus alias
            // marks xbox in-use, so nothing is removed and no exception is thrown.
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            // Data-loss guard: the Xbox download survives a cleanup where 'xbox' is absent from logs.
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "xbox"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));

            // The wsus LogEntry is untouched and still references the xbox Download.
            var wsusEntry = await assert.LogEntries.SingleAsync(le => le.Service == "wsus");
            Assert.Equal(xboxId, wsusEntry.DownloadId);
        }
    }

    [Fact]
    public async Task Cleanup_OrphanWithCrossServiceChild_NullifiesFkBeforeDelete_NoViolation()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        long originId;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            var steam = NewDownload("steam");   // present -> safety check passes
            var origin = NewDownload("origin"); // genuinely orphaned (absent from logs, no alias)
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            originId = origin.Id;

            // A child LogEntry referencing the origin Download but recorded under a DIFFERENT Service
            // name, so the by-name child delete misses it - the parent delete would hit the FK unless
            // the child is re-pointed by DownloadId first.
            seed.LogEntries.Add(NewLogEntry("othercdn", originId));
            await seed.SaveChangesAsync();
        }

        var logServices = new HashSet<string> { "steam" };

        await using (var run = new AppDbContext(options))
        {
            // PRE-FIX: deleting the origin Downloads throws (othercdn child still references it).
            // POST-FIX: the child FK is nulled by DownloadId first, so the delete succeeds.
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.False(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));

            // The cross-service child survived (its Service != 'origin') with its FK nulled.
            var child = await assert.LogEntries.SingleAsync(le => le.Service == "othercdn");
            Assert.Null(child.DownloadId);
        }
    }

    [Fact]
    public async Task Cleanup_OrphanWithSameServiceChild_RemovesAllServiceData()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            var steam = NewDownload("steam");
            var origin = NewDownload("origin");
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            seed.LogEntries.Add(NewLogEntry("origin", origin.Id));
            seed.ServiceStats.Add(new ServiceStats
            {
                Service = "origin",
                LastActivityUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var logServices = new HashSet<string> { "steam" };

        await using (var run = new AppDbContext(options))
        {
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.False(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.False(await assert.LogEntries.AnyAsync(le => le.Service == "origin"));
            Assert.False(await assert.ServiceStats.AnyAsync(s => s.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Empty game identity repair - real ExecuteUpdate/ExecuteDelete against Sqlite
    //
    // The writers that stamped "" onto Downloads.EpicAppId, Downloads.GameName,
    // EpicCdnPatterns.AppId and the detection cache are guarded now (see
    // EpicEmptyAppIdIdentityTests and XboxEmptyTitleIdentityTests), so nothing new can be written.
    // These tests cover the rows already sitting in a user's database, which the guards do not
    // reach: NormalizeEmptyGameIdentitiesCoreAsync must repair every one of them and leave rows
    // holding real values exactly as they are.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NormalizeEmptyGameIdentities_ClearsEmptyDownloadEpicIdAndName_LeavesRealValues()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        long emptyEpicId, emptyNameId, realEpicId, realNameId;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            var emptyEpic = NewDownload("epicgames");
            emptyEpic.EpicAppId = "";

            var emptyName = NewDownload("xbox");
            emptyName.GameName = "";

            var realEpic = NewDownload("epicgames");
            realEpic.EpicAppId = "Fortnite";

            var realName = NewDownload("xbox");
            realName.GameName = "Halo Infinite";

            seed.Downloads.AddRange(emptyEpic, emptyName, realEpic, realName);
            await seed.SaveChangesAsync();

            emptyEpicId = emptyEpic.Id;
            emptyNameId = emptyName.Id;
            realEpicId = realEpic.Id;
            realNameId = realName.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            // Both empty values become null, which is what every layer reads as "absent" - and for
            // the Xbox row it is what makes it a re-resolution candidate again.
            Assert.Null((await assert.Downloads.SingleAsync(d => d.Id == emptyEpicId)).EpicAppId);
            Assert.Null((await assert.Downloads.SingleAsync(d => d.Id == emptyNameId)).GameName);

            // Real values are untouched.
            Assert.Equal("Fortnite", (await assert.Downloads.SingleAsync(d => d.Id == realEpicId)).EpicAppId);
            Assert.Equal("Halo Infinite", (await assert.Downloads.SingleAsync(d => d.Id == realNameId)).GameName);
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_IsIdempotent()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            var emptyName = NewDownload("xbox");
            emptyName.GameName = "";
            seed.Downloads.Add(emptyName);
            await seed.SaveChangesAsync();
        }

        await using (var first = new AppDbContext(options))
        {
            Assert.Equal(1, await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                first, NullLogger.Instance, CancellationToken.None));
        }

        await using (var second = new AppDbContext(options))
        {
            // The pass runs on every start, so a second run over an already-repaired database must
            // change nothing rather than churn rows.
            Assert.Equal(0, await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                second, NullLogger.Instance, CancellationToken.None));
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_ClearsDetectionEpicId_WithoutBreakingUniqueIndex()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        long emptyEpicDetectionId, namedDetectionId, realEpicDetectionId;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            // The row being repaired lands on (0, null). A named row already sits on (0, null),
            // which is only legal because IX_CachedGameDetection_GameAppId_EpicAppId is
            // nulls-distinct - so this pair is the collision case if that assumption is wrong.
            var emptyEpic = NewDetection(0, "Some Epic Game", "epicgames");
            emptyEpic.EpicAppId = "";

            var named = NewDetection(0, "Overwatch", "blizzard");

            var realEpic = NewDetection(0, "Fortnite", "epicgames");
            realEpic.EpicAppId = "Fortnite";

            seed.CachedGameDetections.AddRange(emptyEpic, named, realEpic);
            await seed.SaveChangesAsync();

            emptyEpicDetectionId = emptyEpic.Id;
            namedDetectionId = named.Id;
            realEpicDetectionId = realEpic.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            // Repaired row keeps its name and now reads as a named game, matching how the download
            // side keys the same game once its empty Epic id is gone.
            var repairedRow = await assert.CachedGameDetections.SingleAsync(g => g.Id == emptyEpicDetectionId);
            Assert.Null(repairedRow.EpicAppId);
            Assert.Equal("Some Epic Game", repairedRow.GameName);

            // The pre-existing named row on the same (0, null) key survives alongside it.
            Assert.NotNull(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namedDetectionId));

            // A real Epic id is untouched.
            Assert.Equal("Fortnite", (await assert.CachedGameDetections.SingleAsync(g => g.Id == realEpicDetectionId)).EpicAppId);
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_RemovesNamelessDetection_KeepsIdentifiedOnes()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        long namelessId, namelessEvictedId, namelessSteamId, namedId;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            // No name, no Steam app id, no Epic id: nothing can address this row, and its app id 0
            // un-evicts on any app 0 download.
            var nameless = NewDetection(0, "", "xbox");

            // The evicted variant is the one a full scan can never rebuild, so the repair has to
            // reach it here or it stays broken for good.
            var namelessEvicted = NewDetection(0, "", "wsus");
            namelessEvicted.IsEvicted = true;

            // Nameless but still addressable by its Steam app id - keys as steam:4000 either way,
            // and the scan refills the name, so deleting it would throw away eviction state.
            var namelessSteam = NewDetection(4000, "", "steam");

            var named = NewDetection(0, "Overwatch", "blizzard");

            seed.CachedGameDetections.AddRange(nameless, namelessEvicted, namelessSteam, named);
            await seed.SaveChangesAsync();

            namelessId = nameless.Id;
            namelessEvictedId = namelessEvicted.Id;
            namelessSteamId = namelessSteam.Id;
            namedId = named.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Null(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namelessId));
            Assert.Null(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namelessEvictedId));
            Assert.NotNull(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namelessSteamId));
            Assert.NotNull(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namedId));
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_RemovesEmptyCdnPattern_SoItsChunkUrlCanBeRecorded()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        const string blockedChunkUrl = "/Builds/Org/o-blocked/abc/default/";

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            seed.EpicCdnPatterns.AddRange(
                NewCdnPattern("", "", blockedChunkUrl),
                NewCdnPattern("Fortnite", "Fortnite", "/Builds/Org/o-real/def/default/"));
            await seed.SaveChangesAsync();
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            // The pattern with a real app id is untouched.
            Assert.True(await assert.EpicCdnPatterns.AnyAsync(p => p.AppId == "Fortnite"));

            // The chunk URL the empty pattern held is free again. IX_EpicCdnPatterns_ChunkBaseUrl is
            // unique and the merge path only updates LastSeenAtUtc/Name on a URL it already has, so
            // while the empty row existed no real app id could ever be recorded for this URL.
            Assert.False(await assert.EpicCdnPatterns.AnyAsync(p => p.ChunkBaseUrl == blockedChunkUrl));

            assert.EpicCdnPatterns.Add(NewCdnPattern("RealApp", "Real Game", blockedChunkUrl));
            await assert.SaveChangesAsync();

            Assert.Equal("RealApp",
                (await assert.EpicCdnPatterns.SingleAsync(p => p.ChunkBaseUrl == blockedChunkUrl)).AppId);
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_CleanDatabase_ChangesNothing()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            var steam = NewDownload("steam");
            steam.GameAppId = 730;
            steam.GameName = "Counter-Strike 2";

            var epic = NewDownload("epicgames");
            epic.EpicAppId = "Fortnite";

            seed.Downloads.AddRange(steam, epic);
            seed.CachedGameDetections.Add(NewDetection(730, "Counter-Strike 2", "steam"));
            seed.EpicCdnPatterns.Add(NewCdnPattern("Fortnite", "Fortnite", "/Builds/Org/o-real/def/default/"));
            await seed.SaveChangesAsync();
        }

        await using (var run = new AppDbContext(options))
        {
            Assert.Equal(0, await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None));
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Equal(2, await assert.Downloads.CountAsync());
            Assert.Equal(1, await assert.CachedGameDetections.CountAsync());
            Assert.Equal(1, await assert.EpicCdnPatterns.CountAsync());
            Assert.Equal("Counter-Strike 2",
                (await assert.Downloads.SingleAsync(d => d.GameAppId == 730)).GameName);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    // A shared, kept-open in-memory Sqlite connection with foreign keys enforced. The database lives
    // only for as long as the connection is open, so every AppDbContext in a test reuses it.
    private static SqliteConnection OpenSharedConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        return connection;
    }

    private static DbContextOptions<AppDbContext> SqliteOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

    private static Download NewDownload(string service) => new Download
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
        Datasource = "default"
    };

    private static LogEntryRecord NewLogEntry(string service, long downloadId) => new LogEntryRecord
    {
        Service = service,
        ClientIp = "10.0.0.1",
        Url = "/cache/object",
        Timestamp = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        DownloadId = downloadId
    };

    private static CachedGameDetection NewDetection(long gameAppId, string gameName, string service) => new CachedGameDetection
    {
        GameAppId = gameAppId,
        GameName = gameName,
        Service = service,
        CacheFilesFound = 1,
        TotalSizeBytes = 1024,
        LastDetectedUtc = DateTime.UtcNow,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static EpicCdnPattern NewCdnPattern(string appId, string name, string chunkBaseUrl) => new EpicCdnPattern
    {
        AppId = appId,
        Name = name,
        CdnHost = "epicgames-download1.akamaized.net",
        ChunkBaseUrl = chunkBaseUrl,
        DiscoveredAtUtc = DateTime.UtcNow,
        LastSeenAtUtc = DateTime.UtcNow
    };
}
