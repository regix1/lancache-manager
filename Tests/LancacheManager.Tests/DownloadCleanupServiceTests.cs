using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

/// <summary>
/// Covers diagnostic orphan classification, cache-split aliases, retained observation history,
/// datasource attribution normalization, and empty game identity repair.
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
    public void ComputeOrphanedServices_XboxPresentViaXboxliveAlias_NotOrphaned()
    {
        // Prefill-daemon traffic is tagged 'xboxlive' rather than 'wsus', and on a box that pulls its
        // Xbox content that way 'wsus' is a small aging tail that rotates out of the logs first.
        // Either alias proves the Xbox cache is still in use, so 'xboxlive' alone must protect it.
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam", "xboxlive" });

        Assert.DoesNotContain("xbox", orphans);
        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxWithNoCacheAliasInLogs_IsOrphaned()
    {
        // The aliases only protect xbox while its cache is still present. With both wsus and xboxlive
        // gone, xbox is genuinely orphaned - the guard is conditional, not an unconditional whitelist.
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
    // Integration - diagnostic orphan scans against PostgreSQL
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_XboxCacheSplit_NotDeleted_AndNoFkViolation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long xboxId;
        await using (var seed = new AppDbContext(options))
        {
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
    public async Task Cleanup_OrphanWithCrossServiceChild_NoViolation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long originId;
        await using (var seed = new AppDbContext(options))
        {
            var steam = NewDownload("steam");   // present -> safety check passes
            var origin = NewDownload("origin"); // genuinely orphaned (absent from logs, no alias)
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            originId = origin.Id;

            seed.LogEntries.Add(NewLogEntry("othercdn", originId));
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
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));

            var child = await assert.LogEntries.SingleAsync(le => le.Service == "othercdn");
            Assert.Equal(originId, child.DownloadId);
        }
    }

    [Fact]
    public async Task Cleanup_OrphanWithSameServiceChild()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
            var steam = NewDownload("steam");
            var origin = NewDownload("origin");
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            seed.LogEntries.Add(NewLogEntry("origin", origin.Id));
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
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.True(await assert.LogEntries.AnyAsync(le => le.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));
        }
    }

    [Theory]
    [InlineData(null, "Default")]
    [InlineData("", "Default")]
    [InlineData("primary", "Primary")]
    [InlineData("Retired", "Retired")]
    public void NormalizeDatasourceName_PreservesHistoryAndNormalizesOwnedValues(
        string? current,
        string expected)
    {
        var normalized = DownloadCleanupService.NormalizeDatasourceName(
            current,
            ["Default", "Primary"],
            "Default");

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task NormalizeDatasourceMappings_Postgres_CanonicalizesKnownHistory()
    {
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_CASE_CORRECTION_SCHEMA");
        if (string.IsNullOrWhiteSpace(schema))
        {
            await using var database = await TestDatabase.CreateAsync();
            await VerifyDatasourceNormalizationAsync(database.Options);
            return;
        }

        await VerifyDatasourceNormalizationAsync(CreatePostgresOptions(schema));
    }

    // ---------------------------------------------------------------------------------------------
    // Empty game identity repair - real ExecuteUpdate/ExecuteDelete against PostgreSQL
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
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long emptyEpicId, emptyNameId, realEpicId, realNameId;
        await using (var seed = new AppDbContext(options))
        {
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
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
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
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long emptyEpicDetectionId, namedDetectionId, realEpicDetectionId;
        await using (var seed = new AppDbContext(options))
        {
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
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long namelessId, namelessEvictedId, namelessSteamId, namedId;
        await using (var seed = new AppDbContext(options))
        {
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
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        const string blockedChunkUrl = "/Builds/Org/o-blocked/abc/default/";

        await using (var seed = new AppDbContext(options))
        {
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
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
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

    private static async Task VerifyDatasourceNormalizationAsync(DbContextOptions<AppDbContext> options)
    {
        const string alphaClient = "10.0.0.11";
        var eventTime = new DateTime(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc);
        long alphaDownloadId;
        long betaDownloadId;
        long retiredDownloadId;
        long emptyDownloadId;
        long alphaEntryId;
        long alphaHistoryId;
        long betaEntryId;
        long retiredEntryId;

        await using (var seed = new AppDbContext(options))
        {
            var alpha = NewDownload("steam");
            alpha.ClientIp = alphaClient;
            alpha.DepotId = 42;
            alpha.IsActive = true;
            alpha.Datasource = "alpha";

            var beta = NewDownload("steam");
            beta.ClientIp = alphaClient;
            beta.DepotId = 42;
            beta.IsActive = true;
            beta.Datasource = "beta";

            var retired = NewDownload("steam");
            retired.ClientIp = "10.0.0.13";
            retired.DepotId = 42;
            retired.Datasource = "retired";

            var empty = NewDownload("steam");
            empty.ClientIp = "10.0.0.14";
            empty.Datasource = "";

            seed.Downloads.AddRange(alpha, beta, retired, empty);
            await seed.SaveChangesAsync();

            alphaDownloadId = alpha.Id;
            betaDownloadId = beta.Id;
            retiredDownloadId = retired.Id;
            emptyDownloadId = empty.Id;

            var alphaEntry = NewLogEntry("steam", alpha.Id);
            alphaEntry.ClientIp = alphaClient;
            alphaEntry.Timestamp = eventTime;
            alphaEntry.Url = "/depot/42/chunk";
            alphaEntry.BytesServed = 4096;
            alphaEntry.Datasource = "alpha";

            var alphaHistory = NewLogEntry("steam", alpha.Id);
            alphaHistory.DownloadId = null;
            alphaHistory.ClientIp = alphaClient;
            alphaHistory.Timestamp = eventTime.AddSeconds(-1);
            alphaHistory.Url = "/depot/42/history";
            alphaHistory.BytesServed = 2048;
            alphaHistory.Datasource = "alpha";

            var betaEntry = NewLogEntry("steam", beta.Id);
            betaEntry.ClientIp = alphaClient;
            betaEntry.Timestamp = eventTime;
            betaEntry.Url = "/depot/42/chunk";
            betaEntry.BytesServed = 4096;
            betaEntry.Datasource = "beta";

            var retiredEntry = NewLogEntry("steam", retired.Id);
            retiredEntry.ClientIp = retired.ClientIp;
            retiredEntry.Timestamp = eventTime;
            retiredEntry.Url = "/depot/42/retired";
            retiredEntry.BytesServed = 1024;
            retiredEntry.Datasource = "retired";

            seed.LogEntries.AddRange(alphaEntry, alphaHistory, betaEntry, retiredEntry);
            await seed.SaveChangesAsync();

            alphaEntryId = alphaEntry.Id;
            alphaHistoryId = alphaHistory.Id;
            betaEntryId = betaEntry.Id;
            retiredEntryId = retiredEntry.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var updated = await DownloadCleanupService.NormalizeDatasourceMappingsCoreAsync(
                run,
                ["ALPHA", "beta"],
                "ALPHA",
                NullLogger.Instance,
                CancellationToken.None);

            Assert.Equal(4, updated);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Equal("ALPHA", (await assert.Downloads.SingleAsync(row => row.Id == alphaDownloadId)).Datasource);
            Assert.Equal("beta", (await assert.Downloads.SingleAsync(row => row.Id == betaDownloadId)).Datasource);
            Assert.Equal("retired", (await assert.Downloads.SingleAsync(row => row.Id == retiredDownloadId)).Datasource);
            Assert.Equal("ALPHA", (await assert.Downloads.SingleAsync(row => row.Id == emptyDownloadId)).Datasource);

            var alphaEntry = await assert.LogEntries.SingleAsync(row => row.Id == alphaEntryId);
            var alphaHistory = await assert.LogEntries.SingleAsync(row => row.Id == alphaHistoryId);
            var betaEntry = await assert.LogEntries.SingleAsync(row => row.Id == betaEntryId);
            var retiredEntry = await assert.LogEntries.SingleAsync(row => row.Id == retiredEntryId);

            Assert.Equal("ALPHA", alphaEntry.Datasource);
            Assert.Equal("ALPHA", alphaHistory.Datasource);
            Assert.Equal("beta", betaEntry.Datasource);
            Assert.Equal("retired", retiredEntry.Datasource);
            Assert.Equal(alphaDownloadId, alphaEntry.DownloadId);
            Assert.Null(alphaHistory.DownloadId);
            Assert.Equal(betaDownloadId, betaEntry.DownloadId);
            Assert.Equal(retiredDownloadId, retiredEntry.DownloadId);

            Assert.Empty(await (
                from entry in assert.LogEntries
                join download in assert.Downloads on entry.DownloadId equals download.Id
                where entry.Datasource == "ALPHA" && entry.Datasource != download.Datasource
                select entry.Id).ToListAsync());

            Assert.Equal(1, await assert.LogEntries.CountAsync(row =>
                row.Datasource == "ALPHA"
                && row.ClientIp == alphaEntry.ClientIp
                && row.Service == alphaEntry.Service
                && row.Timestamp == alphaEntry.Timestamp
                && row.Url == alphaEntry.Url
                && row.BytesServed == alphaEntry.BytesServed));

            Assert.Equal(alphaDownloadId, await assert.Downloads
                .Where(row => row.ClientIp == alphaClient
                    && row.Service == "steam"
                    && row.DepotId == 42
                    && row.IsActive
                    && row.Datasource == "ALPHA")
                .Select(row => row.Id)
                .SingleAsync());
        }
    }

    private static DbContextOptions<AppDbContext> CreatePostgresOptions(string schema)
    {
        var connection = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("The PostgreSQL test connection is missing");
        }

        var settings = new NpgsqlConnectionStringBuilder(connection)
        {
            SearchPath = schema + ",public",
            ApplicationName = "ds-impl-b-case-correction"
        };

        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(settings.ConnectionString)
            .Options;
    }

    private static Download NewDownload(string service) => new Download
    {
        Service = service,
        ClientIp = "10.0.0.1",
        StartTimeUtc = DateTime.UtcNow,
        EndTimeUtc = DateTime.UtcNow,
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
