using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

public sealed class CorruptionScanInvalidationTests
{
    [Theory]
    [InlineData("LogEntries")]
    [InlineData("CachedCorruptionDetections")]
    [InlineData("CachedCorruptionScans")]
    public void DatabaseReset_ExpandsEvidenceInvalidationToCandidateAndHeaderTables(
        string requestedTable)
    {
        var tables = DatabaseService.ResolveResetTables([requestedTable, requestedTable, "invalid"]);

        Assert.Contains("CachedCorruptionDetections", tables);
        Assert.Contains("CachedCorruptionScans", tables);
        Assert.Equal(1, tables.Count(table => table == "CachedCorruptionDetections"));
        Assert.Equal(1, tables.Count(table => table == "CachedCorruptionScans"));
        Assert.DoesNotContain("invalid", tables);
    }

    [Fact]
    public async Task DatabaseReset_CountsAndDeletesCandidateHeaderPairAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 2);

        await using (var context = new AppDbContext(database.Options))
        {
            Assert.Equal(
                2,
                await DatabaseService.CountResetTableRowsAsync(
                    context,
                    "CachedCorruptionDetections",
                    CancellationToken.None));
            Assert.Equal(
                1,
                await DatabaseService.CountResetTableRowsAsync(
                    context,
                    "CachedCorruptionScans",
                    CancellationToken.None));

            // This trigger makes the relational ordering observable: deleting a header while
            // any child candidate remains fails before SQLite can apply its cascade.
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER "RequireCorruptionCandidatesDeleted"
                BEFORE DELETE ON "CachedCorruptionScans"
                WHEN EXISTS (
                    SELECT 1 FROM "CachedCorruptionDetections"
                    WHERE "ScanId" = OLD."ScanId"
                )
                BEGIN
                    SELECT RAISE(ABORT, 'candidate rows must be deleted first');
                END;
                """);

            await using var transaction = await context.Database.BeginTransactionAsync();
            var deleted = await DatabaseService.DeleteCachedCorruptionEvidenceAsync(
                context,
                CancellationToken.None);
            await transaction.CommitAsync();

            Assert.Equal(2, deleted.Candidates);
            Assert.Equal(1, deleted.Scans);
        }

        await AssertScanCountsAsync(database.Options, scans: 0, candidates: 0);
    }

    [Fact]
    public async Task DatabaseReset_InvalidatesEveryRetainedCurrentAndHistoricalSnapshotAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedRetainedScansAsync(database.Options);

        await using (var context = new AppDbContext(database.Options))
        {
            Assert.Equal(5, await DatabaseService.CountResetTableRowsAsync(
                context,
                "CachedCorruptionDetections",
                CancellationToken.None));
            Assert.Equal(4, await DatabaseService.CountResetTableRowsAsync(
                context,
                "CachedCorruptionScans",
                CancellationToken.None));

            await using var transaction = await context.Database.BeginTransactionAsync();
            var deleted = await DatabaseService.DeleteCachedCorruptionEvidenceAsync(
                context,
                CancellationToken.None);
            await transaction.CommitAsync();

            Assert.Equal(5, deleted.Candidates);
            Assert.Equal(4, deleted.Scans);
        }

        await AssertScanCountsAsync(database.Options, scans: 0, candidates: 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task CacheClearing_InvalidatesZeroResultAndCandidateBackedScansAsync(
        int candidateCount)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount);

        await using (var context = new AppDbContext(database.Options))
        {
            var deleted = await CacheClearingService.InvalidateCachedDetectionResultsAsync(
                context,
                CancellationToken.None);

            Assert.Equal(candidateCount, deleted.CorruptionCandidates);
            Assert.Equal(1, deleted.CorruptionScans);
        }

        await AssertScanCountsAsync(database.Options, scans: 0, candidates: 0);
    }

    [Fact]
    public async Task CacheClearing_InvalidatesAllMethodsAndRetainedHistoryAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedRetainedScansAsync(database.Options);

        await using (var context = new AppDbContext(database.Options))
        {
            var deleted = await CacheClearingService.InvalidateCachedDetectionResultsAsync(
                context,
                CancellationToken.None);

            Assert.Equal(5, deleted.CorruptionCandidates);
            Assert.Equal(4, deleted.CorruptionScans);
        }

        await AssertScanCountsAsync(database.Options, scans: 0, candidates: 0);
    }

    [Fact]
    public async Task CacheClearing_RollsBackCandidatesWhenHeaderDeletionFailsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 1);

        await using (var setup = new AppDbContext(database.Options))
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER "PreventCorruptionScanDelete"
                BEFORE DELETE ON "CachedCorruptionScans"
                BEGIN
                    SELECT RAISE(ABORT, 'blocked scan-header deletion');
                END;
                """);
        }

        await using (var context = new AppDbContext(database.Options))
        {
            await Assert.ThrowsAsync<SqliteException>(() =>
                CacheClearingService.InvalidateCachedDetectionResultsAsync(
                    context,
                    CancellationToken.None));
        }

        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1);
    }

    [Fact]
    public async Task CacheClearing_SuccessMarksOnlyEligibleDownloadsInClearedDatasourceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 1);

        await using (var setup = new AppDbContext(database.Options))
        {
            setup.Downloads.AddRange(
                CreateDownload("eligible", "Default", cacheHitBytes: 1024),
                CreateDownload("active", "Default", cacheHitBytes: 1024, isActive: true),
                CreateDownload("zero-byte", "Default"),
                CreateDownload("other-datasource", "Secondary", cacheMissBytes: 2048),
                CreateDownload("already-evicted", "Default", cacheHitBytes: 1024, isEvicted: true));
            setup.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 10,
                GameName = "Cached Game",
                Service = "steam",
                CacheFilesFound = 1,
                TotalSizeBytes = 1024,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            setup.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "wsus",
                CacheFilesFound = 1,
                TotalSizeBytes = 1024,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await setup.SaveChangesAsync();
        }

        await using (var context = new AppDbContext(database.Options))
        {
            var result = await CacheClearingService.ReconcileSuccessfulCacheClearAsync(
                context,
                ["Default"],
                CancellationToken.None);

            Assert.Equal(1, result.DownloadsEvicted);
            Assert.Equal(1, result.Games);
            Assert.Equal(1, result.Services);
            Assert.Equal(1, result.CorruptionCandidates);
            Assert.Equal(1, result.CorruptionScans);
        }

        await using (var assertContext = new AppDbContext(database.Options))
        {
            var states = await assertContext.Downloads
                .OrderBy(download => download.ClientIp)
                .ToDictionaryAsync(download => download.ClientIp, download => download.IsEvicted);
            Assert.True(states["eligible"]);
            Assert.False(states["active"]);
            Assert.False(states["zero-byte"]);
            Assert.False(states["other-datasource"]);
            Assert.True(states["already-evicted"]);
            Assert.Empty(await assertContext.CachedGameDetections.ToListAsync());
            Assert.Empty(await assertContext.CachedServiceDetections.ToListAsync());
        }

        await AssertScanCountsAsync(database.Options, scans: 0, candidates: 0);
    }

    [Fact]
    public async Task CacheClearing_ReconciliationRollsBackEvictionFlagsWhenInvalidationFailsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 1);

        await using (var setup = new AppDbContext(database.Options))
        {
            setup.Downloads.Add(CreateDownload("rollback", "Default", cacheHitBytes: 1024));
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER "PreventReconciledCorruptionScanDelete"
                BEFORE DELETE ON "CachedCorruptionScans"
                BEGIN
                    SELECT RAISE(ABORT, 'blocked reconciled scan-header deletion');
                END;
                """);
        }

        await using (var context = new AppDbContext(database.Options))
        {
            await Assert.ThrowsAsync<SqliteException>(() =>
                CacheClearingService.ReconcileSuccessfulCacheClearAsync(
                    context,
                    ["Default"],
                    CancellationToken.None));
        }

        await using (var assertContext = new AppDbContext(database.Options))
        {
            Assert.False((await assertContext.Downloads.SingleAsync()).IsEvicted);
        }
        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1);
    }

    [Fact]
    public async Task CacheClearing_CancelledReconciliationDoesNotChangeDownloadsOrEvidenceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 1);
        await using (var setup = new AppDbContext(database.Options))
        {
            setup.Downloads.Add(CreateDownload("cancelled", "Default", cacheHitBytes: 1024));
            await setup.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using (var context = new AppDbContext(database.Options))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CacheClearingService.ReconcileSuccessfulCacheClearAsync(
                    context,
                    ["Default"],
                    cancellation.Token));
        }

        await using (var assertContext = new AppDbContext(database.Options))
        {
            Assert.False((await assertContext.Downloads.SingleAsync()).IsEvicted);
        }
        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task EvictionSuccessBoundary_InvalidatesZeroResultAndCandidateBackedScansAsync(
        int candidateCount)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount);

        await using (var context = new AppDbContext(database.Options))
        {
            var deleted = await DatabaseService.InvalidateCachedCorruptionEvidenceAsync(
                context,
                CancellationToken.None);

            Assert.Equal(candidateCount, deleted.Candidates);
            Assert.Equal(1, deleted.Scans);
        }

        await AssertScanCountsAsync(database.Options, scans: 0, candidates: 0);
    }

    [Fact]
    public async Task EvictionSuccessBoundary_InvalidatesAllMethodsAndRetainedHistoryAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedRetainedScansAsync(database.Options);

        await using (var context = new AppDbContext(database.Options))
        {
            var deleted = await DatabaseService.InvalidateCachedCorruptionEvidenceAsync(
                context,
                CancellationToken.None);

            Assert.Equal(5, deleted.Candidates);
            Assert.Equal(4, deleted.Scans);
        }

        await AssertScanCountsAsync(database.Options, scans: 0, candidates: 0);
    }

    [Fact]
    public async Task EvictionSuccessBoundary_RollsBackCandidatesWhenHeaderDeletionFailsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 1);

        await using (var setup = new AppDbContext(database.Options))
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER "PreventEvictionCorruptionScanDelete"
                BEFORE DELETE ON "CachedCorruptionScans"
                BEGIN
                    SELECT RAISE(ABORT, 'blocked eviction scan-header deletion');
                END;
                """);
        }

        await using (var context = new AppDbContext(database.Options))
        {
            await Assert.ThrowsAsync<SqliteException>(() =>
                DatabaseService.InvalidateCachedCorruptionEvidenceAsync(
                    context,
                    CancellationToken.None));
        }

        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1);
    }

    [Fact]
    public async Task EvictionSuccessBoundary_CancellationRetainsPriorScanAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await using (var context = new AppDbContext(database.Options))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DatabaseService.InvalidateCachedCorruptionEvidenceAsync(
                    context,
                    cancellation.Token));
        }

        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1);
    }

    private static async Task SeedScanAsync(
        DbContextOptions<AppDbContext> options,
        int candidateCount)
    {
        await using var context = new AppDbContext(options);
        var scanId = Guid.NewGuid();
        context.CachedCorruptionScans.Add(new CachedCorruptionScan
        {
            ScanId = scanId,
            DetectionMode = CorruptionDetectionMode.RepeatedMiss,
            IsCurrent = true,
            Threshold = 3,
            LookbackDays = 30,
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Status = "completed",
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-1),
            CompletedAtUtc = DateTime.UtcNow
        });

        for (var index = 0; index < candidateCount; index++)
        {
            context.CachedCorruptionDetections.Add(new CachedCorruptionDetection
            {
                ScanId = scanId,
                ServiceName = $"service-{index}",
                DatasourceName = "default",
                CorruptedChunkCount = 1,
                CandidatesJson = "[]",
                RemovalAllowed = true,
                LastDetectedUtc = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
    }

    private static async Task SeedRetainedScansAsync(DbContextOptions<AppDbContext> options)
    {
        await using var context = new AppDbContext(options);
        var completedAtUtc = DateTime.UtcNow;
        AddScan(
            context,
            CorruptionDetectionMode.RepeatedMiss,
            isCurrent: true,
            candidateCount: 2,
            completedAtUtc);
        AddScan(
            context,
            CorruptionDetectionMode.RepeatedMiss,
            isCurrent: false,
            candidateCount: 1,
            completedAtUtc.AddMinutes(-1));
        AddScan(
            context,
            CorruptionDetectionMode.Structural,
            isCurrent: true,
            candidateCount: 0,
            completedAtUtc.AddMinutes(-2));
        AddScan(
            context,
            CorruptionDetectionMode.Structural,
            isCurrent: false,
            candidateCount: 2,
            completedAtUtc.AddMinutes(-3));
        await context.SaveChangesAsync();
    }

    private static void AddScan(
        AppDbContext context,
        CorruptionDetectionMode detectionMode,
        bool isCurrent,
        int candidateCount,
        DateTime completedAtUtc)
    {
        var scanId = Guid.NewGuid();
        context.CachedCorruptionScans.Add(new CachedCorruptionScan
        {
            ScanId = scanId,
            DetectionMode = detectionMode,
            ScanMode = detectionMode == CorruptionDetectionMode.Structural
                ? StructuralScanMode.Full
                : null,
            IsCurrent = isCurrent,
            Threshold = 3,
            LookbackDays = 30,
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Status = OperationStatus.Completed.ToWireString(),
            StartedAtUtc = completedAtUtc.AddSeconds(-1),
            CompletedAtUtc = completedAtUtc,
            CreatedAtUtc = completedAtUtc
        });

        for (var index = 0; index < candidateCount; index++)
        {
            context.CachedCorruptionDetections.Add(new CachedCorruptionDetection
            {
                ScanId = scanId,
                ServiceName = $"service-{index}",
                DatasourceName = "default",
                CorruptedChunkCount = 1,
                CandidatesJson = "[]",
                RemovalAllowed = true,
                LastDetectedUtc = completedAtUtc,
                CreatedAtUtc = completedAtUtc
            });
        }
    }

    private static Download CreateDownload(
        string identity,
        string datasource,
        long cacheHitBytes = 0,
        long cacheMissBytes = 0,
        bool isActive = false,
        bool isEvicted = false) =>
        new()
        {
            Service = "steam",
            ClientIp = identity,
            Datasource = datasource,
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
            EndTimeUtc = DateTime.UtcNow,
            StartTimeLocal = DateTime.UtcNow.AddMinutes(-1),
            EndTimeLocal = DateTime.UtcNow,
            CacheHitBytes = cacheHitBytes,
            CacheMissBytes = cacheMissBytes,
            IsActive = isActive,
            IsEvicted = isEvicted
        };

    private static async Task AssertScanCountsAsync(
        DbContextOptions<AppDbContext> options,
        int scans,
        int candidates)
    {
        await using var context = new AppDbContext(options);
        Assert.Equal(scans, await context.CachedCorruptionScans.CountAsync());
        Assert.Equal(candidates, await context.CachedCorruptionDetections.CountAsync());
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(
            SqliteConnection connection,
            DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            Options = options;
        }

        public DbContextOptions<AppDbContext> Options { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            await using var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, options);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
