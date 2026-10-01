using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LancacheManager.Tests;

public sealed class CorruptionScanInvalidationTests
{
    [Theory]
    [InlineData("CachedCorruptionDetections")]
    [InlineData("CachedCorruptionScans")]
    public void DatabaseReset_ExpandsExplicitCorruptionSelectionToCandidateAndHeaderTables(
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
    public void DatabaseReset_LogEntriesClearDoesNotDeleteCorruptionSnapshots()
    {
        var tables = DatabaseService.ResolveResetTables(["LogEntries", "invalid"]);

        Assert.Equal(["LogEntries"], tables);
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
            // any child candidate remains fails before PostgreSQL can apply its cascade. The
            // check sits in the function body because a trigger's WHEN clause may not query
            // another table.
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION "RequireCorruptionCandidatesDeleted"() RETURNS trigger AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "CachedCorruptionDetections"
                        WHERE "ScanId" = OLD."ScanId"
                    ) THEN
                        RAISE EXCEPTION 'candidate rows must be deleted first';
                    END IF;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "RequireCorruptionCandidatesDeleted"
                BEFORE DELETE ON "CachedCorruptionScans"
                FOR EACH ROW
                EXECUTE FUNCTION "RequireCorruptionCandidatesDeleted"();
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
                CREATE FUNCTION "PreventCorruptionScanDelete"() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'blocked scan-header deletion';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "PreventCorruptionScanDelete"
                BEFORE DELETE ON "CachedCorruptionScans"
                FOR EACH ROW
                EXECUTE FUNCTION "PreventCorruptionScanDelete"();
                """);
        }

        await using (var context = new AppDbContext(database.Options))
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                CacheClearingService.InvalidateCachedDetectionResultsAsync(
                    context,
                    CancellationToken.None));
        }

        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task EvictionSuccessBoundary_DemotesCurrentScanAndRetainsEvidenceAsync(
        int candidateCount)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount);

        await using (var context = new AppDbContext(database.Options))
        {
            var demoted = await DatabaseService.DemoteCachedCorruptionEvidenceAsync(
                context,
                CancellationToken.None);

            Assert.Equal(1, demoted);
        }

        await AssertScanCountsAsync(
            database.Options,
            scans: 1,
            candidates: candidateCount,
            currentScans: 0);
    }

    [Fact]
    public async Task EvictionSuccessBoundary_DemotesEveryMethodAndKeepsRetainedHistoryAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedRetainedScansAsync(database.Options);

        await using (var context = new AppDbContext(database.Options))
        {
            var demoted = await DatabaseService.DemoteCachedCorruptionEvidenceAsync(
                context,
                CancellationToken.None);

            Assert.Equal(2, demoted);
        }

        await AssertScanCountsAsync(database.Options, scans: 4, candidates: 5, currentScans: 0);
    }

    [Fact]
    public async Task LogClearBoundary_DemotesOnlyTheRepeatedMissCurrentScanAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedRetainedScansAsync(database.Options);

        await using (var context = new AppDbContext(database.Options))
        {
            var demoted = await DatabaseService.DemoteCachedCorruptionEvidenceAsync(
                context,
                CancellationToken.None,
                CorruptionDetectionMode.RepeatedMiss);

            Assert.Equal(1, demoted);

            var currentScan = await context.CachedCorruptionScans
                .SingleAsync(scan => scan.IsCurrent);
            Assert.Equal(CorruptionDetectionMode.Structural, currentScan.DetectionMode);
        }

        await AssertScanCountsAsync(database.Options, scans: 4, candidates: 5, currentScans: 1);
    }

    [Fact]
    public async Task EvictionSuccessBoundary_KeepsCurrentScanWhenDemotionFailsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedScanAsync(database.Options, candidateCount: 1);

        await using (var setup = new AppDbContext(database.Options))
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION "PreventEvictionCorruptionScanDemotion"() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'blocked eviction scan demotion';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER "PreventEvictionCorruptionScanDemotion"
                BEFORE UPDATE OF "IsCurrent" ON "CachedCorruptionScans"
                FOR EACH ROW
                EXECUTE FUNCTION "PreventEvictionCorruptionScanDemotion"();
                """);
        }

        await using (var context = new AppDbContext(database.Options))
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                DatabaseService.DemoteCachedCorruptionEvidenceAsync(
                    context,
                    CancellationToken.None));
        }

        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1, currentScans: 1);
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
                DatabaseService.DemoteCachedCorruptionEvidenceAsync(
                    context,
                    cancellation.Token));
        }

        await AssertScanCountsAsync(database.Options, scans: 1, candidates: 1, currentScans: 1);
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

    private static async Task AssertScanCountsAsync(
        DbContextOptions<AppDbContext> options,
        int scans,
        int candidates,
        int? currentScans = null)
    {
        await using var context = new AppDbContext(options);
        Assert.Equal(scans, await context.CachedCorruptionScans.CountAsync());
        Assert.Equal(candidates, await context.CachedCorruptionDetections.CountAsync());
        if (currentScans is { } expectedCurrent)
        {
            Assert.Equal(
                expectedCurrent,
                await context.CachedCorruptionScans.CountAsync(scan => scan.IsCurrent));
        }
    }

}
