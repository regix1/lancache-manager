using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Data.Migrations;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class CorruptionDetectionPersistenceTests
{
    private const int LookbackDays = 30;
    private const string ScanStartedWire = "2026-07-11T00:00:00Z";
    private static readonly DateTime ScanStartedUtc =
        new(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ContractV3_HasOneInternalModeAndRejectsRetiredAliases()
    {
        Assert.Equal(3, CorruptionReport.SupportedContractVersion);
        Assert.Equal(
            CorruptionDetectionMode.CacheAndLogs,
            CorruptionDetectionModeExtensions.Parse("cache_and_logs"));
        Assert.Equal(
            CorruptionDetectionMode.Unknown,
            CorruptionDetectionModeExtensions.Parse("logs_only"));
        Assert.Equal(
            CorruptionDetectionMode.Unknown,
            CorruptionDetectionModeExtensions.Parse("redownload"));
        Assert.Equal(
            CorruptionDetectionMode.Unknown,
            CorruptionDetectionModeExtensions.Parse("miss_count"));
    }

    [Fact]
    public void ScanInput_ValidatesThresholdAndLookbackOnly()
    {
        CorruptionDetectionService.ValidateScanInput(3, 1);
        CorruptionDetectionService.ValidateScanInput(5, 365);
        CorruptionDetectionService.ValidateScanInput(10, LookbackDays);
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput(4, LookbackDays));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput(3, 0));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput(3, 366));

        var parameters = typeof(CacheController)
            .GetMethod(nameof(CacheController.StartCorruptionDetectionAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToList();
        Assert.Equal(["threshold", "lookbackDays", "cancellationToken"], parameters);
        Assert.DoesNotContain("detectionMode", parameters);
    }

    [Fact]
    public void ContractV3_RequiresSingleActionableReportShape()
    {
        const string incompleteJson =
            """
            {
              "contract_version": 3,
              "threshold": 3,
              "lookback_days": 30,
              "scan_started_utc": "2026-07-11T00:00:00Z",
              "service_counts": {},
              "candidates": []
            }
            """;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CorruptionReport>(incompleteJson));

        const string retiredReviewShape =
            """
            {
              "contract_version": 3,
              "threshold": 3,
              "lookback_days": 30,
              "scan_started_utc": "2026-07-11T00:00:00Z",
              "service_counts": {"steam": 1},
              "total": 1,
              "candidates": [{
                "candidate_id": "review",
                "service": "steam",
                "raw_url": "/chunk",
                "normalized_uri": "/chunk",
                "observed_range": {"kind": "no_range"},
                "cache_slice": {"kind": "no_range"},
                "exact_paths": [],
                "evidence_count": 3,
                "first_seen": "2026-07-11T00:00:00Z",
                "last_seen": "2026-07-11T00:00:10Z",
                "reason": "missing_cached_slice",
                "validation_state": "exact_path_missing",
                "removal_allowed": false,
                "observations": []
              }]
            }
            """;
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CorruptionReport>(retiredReviewShape));
    }

    [Fact]
    public void CandidateJson_RoundTripsExactActionableEvidence()
    {
        var candidate = Candidate("default:chunk", threshold: 5);
        var json = CorruptionDetectionService.SerializeCandidates([candidate]);
        var roundTrip = Assert.Single(CorruptionDetectionService.DeserializeCandidates(
            new CachedCorruptionDetection
            {
                Id = 7,
                DatasourceName = "default",
                CandidatesJson = json
            }));

        Assert.Equal(candidate.CandidateId, roundTrip.CandidateId);
        Assert.Equal("default", roundTrip.Datasource);
        Assert.Equal(candidate.ExactPaths, roundTrip.ExactPaths);
        Assert.Equal(5, roundTrip.Observations.Count);
        Assert.All(roundTrip.Observations, observation =>
        {
            Assert.Equal("GET", observation.Method);
            Assert.Equal("MISS", observation.CacheStatus);
            Assert.False(string.IsNullOrWhiteSpace(observation.RawUrl));
        });
    }

    [Fact]
    public void ReportValidation_AcceptsOnlyThresholdQualifiedExactPathMissEvidence()
    {
        var valid = Report("default", Candidate("valid"));
        CorruptionDetectionService.ValidateAndAttachDatasource(
            valid.Report,
            "default",
            3,
            LookbackDays,
            ScanStartedWire);
        Assert.Equal("default:valid", Assert.Single(valid.Report.Candidates).CandidateId);

        AssertInvalid(candidate => candidate.ExactPaths = []);
        AssertInvalid(candidate => candidate.EvidenceCount = 2);
        AssertInvalid(candidate => candidate.Observations.RemoveAt(0));
        AssertInvalid(candidate => candidate.Observations[0].Method = "POST");
        AssertInvalid(candidate => candidate.Observations[0].HttpStatus = 500);
        AssertInvalid(candidate => candidate.Observations[0].CacheStatus = "HIT");
        AssertInvalid(candidate => candidate.Observations[0].RawUrl = "");
        AssertInvalid(candidate => candidate.FirstSeen = "2026-06-10T23:59:59Z");
        AssertInvalid(candidate => candidate.LastSeen = "2026-07-11T00:01:01Z");

        static void AssertInvalid(Action<CorruptionCandidate> mutate)
        {
            var candidate = Candidate("invalid");
            mutate(candidate);
            var report = Report("default", candidate).Report;
            Assert.Throws<InvalidDataException>(() =>
                CorruptionDetectionService.ValidateAndAttachDatasource(
                    report,
                    "default",
                    3,
                    LookbackDays,
                    ScanStartedWire));
        }
    }

    [Fact]
    public void ReportValidation_RejectsCountAndPhysicalIdentityMismatches()
    {
        var countMismatch = Report("default", Candidate("count")).Report;
        countMismatch.ServiceCounts["steam"] = 2;
        Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                countMismatch,
                "default",
                3,
                LookbackDays,
                ScanStartedWire));

        var duplicate = Report(
            "default",
            Candidate("first"),
            Candidate("second")).Report;
        Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                duplicate,
                "default",
                3,
                LookbackDays,
                ScanStartedWire));

        var secondSlice = Candidate("second-slice");
        secondSlice.CacheSlice = new CacheSliceIdentity
        {
            Kind = "ranged",
            Start = 2_097_152,
            End = 3_145_727
        };
        var distinct = Report("default", Candidate("first-slice"), secondSlice).Report;
        CorruptionDetectionService.ValidateAndAttachDatasource(
            distinct,
            "default",
            3,
            LookbackDays,
            ScanStartedWire);
        Assert.Equal(2, distinct.Total);
    }

    [Fact]
    public async Task PersistCompletedScan_AtomicallyReplacesOldScanAndPersistsZeroResultAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var legacyScanId = await SeedLegacyScanAsync(
            database.Factory,
            "logs_only",
            """[{"candidate_id":"legacy","reason":"missing_cached_slice"}]""");

        Assert.Null(await service.GetDetectionAsync());
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            10,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(20),
            []);

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(scanId, result.ScanId);
        Assert.Empty(result.CorruptionCounts);
        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(1, await context.CachedCorruptionScans.CountAsync());
        Assert.Equal(0, await context.CachedCorruptionDetections.CountAsync());
        Assert.DoesNotContain(
            await context.CachedCorruptionScans.Select(scan => scan.ScanId).ToListAsync(),
            id => id == legacyScanId);
    }

    [Fact]
    public async Task PersistCompletedScan_RollsBackReplacementOnFailureAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var originalScan = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            originalScan,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", Candidate("original"))]);

        var invalidReport = Report("secondary", Candidate("new"));
        invalidReport.Report.LookbackDays = 90;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PersistCompletedScanAsync(
            Guid.NewGuid(),
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(2),
            [invalidReport]));

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(originalScan, result.ScanId);
        Assert.Equal(1, result.TotalCorruptedChunks);
    }

    [Theory]
    [InlineData("logs_only")]
    [InlineData("redownload")]
    public async Task Version2LegacyModeScan_IsRejectedBeforeCandidateDeserializationAsync(string mode)
    {
        await AssertLegacyScanRejectedAsync(
            mode,
            """{ this is deliberately not candidate JSON }""");
    }

    [Fact]
    public async Task Version2MixedCacheAndLogsScan_IsNeverPartiallyReinterpretedAsync()
    {
        const string mixedV2Candidates =
            """
            [
              {"candidate_id":"present","reason":"repeated_miss_burst","validation_state":"exact_path_present","removal_allowed":true},
              {"candidate_id":"missing","reason":"missing_cached_slice","validation_state":"exact_path_missing","removal_allowed":false}
            ]
            """;
        await AssertLegacyScanRejectedAsync("cache_and_logs", mixedV2Candidates);
    }

    [Fact]
    public async Task NewV3Scan_ReplacesLegacyRowsAndSupportsDetailsAndNarrowingRemovalAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        await SeedLegacyScanAsync(
            database.Factory,
            "cache_and_logs",
            """[{"candidate_id":"legacy","reason":"missing_cached_slice"}]""");

        var first = Candidate("first");
        var second = Candidate("second");
        second.CacheSlice = new CacheSliceIdentity
        {
            Kind = "ranged",
            Start = 2_097_152,
            End = 3_145_727
        };
        var report = Report("default", first, second);
        CorruptionDetectionService.ValidateAndAttachDatasource(
            report.Report,
            "default",
            3,
            LookbackDays,
            ScanStartedWire);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [report]);

        Assert.Equal(2, (await service.GetDetailsAsync(scanId, "steam")).Count);
        var selected = await service.GetRemovalSelectionAsync(
            scanId,
            "steam",
            ["default:first"]);
        Assert.Equal(["default:first"], selected.CandidateIds);
        await service.ApplyRemovalSuccessAsync(scanId, selected.CandidateIds);
        var remaining = Assert.Single(await service.GetDetailsAsync(scanId, "steam"));
        Assert.Equal("default:second", remaining.CandidateId);
    }

    [Fact]
    public async Task RemovalSelection_RejectsNonActionablePersistedRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", Candidate("candidate"))]);

        await using (var context = database.Factory.CreateDbContext())
        {
            var row = await context.CachedCorruptionDetections.SingleAsync();
            row.RemovalAllowed = false;
            await context.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetDetailsAsync(scanId, "steam"));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam"));
    }

    [Fact]
    public async Task RemovalSelection_IsScanBoundAndNarrowingOnly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", Candidate("candidate"))]);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetDetailsAsync(Guid.NewGuid(), "steam"));
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam", ["forged"]));
        var selection = await service.GetRemovalSelectionAsync(
            scanId,
            "steam",
            ["candidate"]);
        Assert.Equal(["candidate"], selection.CandidateIds);
    }

    [Fact]
    public void CachedAndSignalRContracts_ExposeOneActionableCountMap()
    {
        var cached = new CachedCorruptionResponse
        {
            HasCachedResults = true,
            ScanId = Guid.NewGuid(),
            Threshold = 3,
            LookbackDays = LookbackDays,
            ContractVersion = CorruptionReport.SupportedContractVersion,
            CorruptionCounts = new Dictionary<string, long> { ["steam"] = 2 },
            TotalServicesWithCorruption = 1,
            TotalCorruptedChunks = 2
        };
        var completion = new SignalRNotifications.CorruptionDetectionComplete(
            Success: true,
            OperationId: Guid.NewGuid(),
            StageKey: "signalr.corruptionDetect.complete",
            TotalServicesWithCorruption: 1,
            TotalCorruptedChunks: 2,
            CorruptionCounts: new Dictionary<string, long> { ["steam"] = 2 });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var cachedJson = JsonSerializer.Serialize(cached, options);
        var completionJson = JsonSerializer.Serialize(completion, options);

        Assert.Contains("\"corruptionCounts\"", cachedJson);
        Assert.Contains("\"corruptionCounts\"", completionJson);
        Assert.DoesNotContain("reviewOnly", cachedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("removable", cachedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("detectionMode", cachedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reviewOnly", completionJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("removable", completionJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetiredRoutesEventsOperationsAndHelpers_AreAbsent()
    {
        var routeTemplates = typeof(CacheController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => template != null)
            .ToList();
        Assert.DoesNotContain(routeTemplates, route =>
            route!.Contains("review-findings", StringComparison.Ordinal));
        Assert.DoesNotContain(routeTemplates, route =>
            route!.Contains("historical-evidence", StringComparison.Ordinal));
        Assert.Null(OperationTypeExtensions.TryParseWire("historicalEvidencePurge"));
        Assert.Null(typeof(RustProcessHelper).GetMethod("RunHistoricalEvidencePurgeAsync"));
        Assert.Null(typeof(SignalRNotifications).GetNestedType("CorruptionDetailsProgress"));
        Assert.Null(typeof(SignalRNotifications).GetNestedType("HistoricalEvidencePurgeComplete"));
    }

    [Fact]
    public void PersistenceModelAndHistoricalMigration_RemainIntact()
    {
        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.CachedCorruptionScans)));
        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.CachedCorruptionDetections)));
        Assert.NotNull(typeof(CachedCorruptionScan).GetProperty(nameof(CachedCorruptionScan.DetectionMode)));
        Assert.NotNull(typeof(CachedCorruptionDetection).GetProperty(nameof(CachedCorruptionDetection.RemovalAllowed)));

        var operations = new TestableV2LookbackMigration().BuildUpOperations();
        Assert.Contains(operations, operation =>
            operation is SqlOperation sql
            && sql.Sql.Contains("CachedCorruptionDetections", StringComparison.Ordinal));
        Assert.Contains(operations, operation =>
            operation is AddColumnOperation column
            && column.Name == "LookbackDays");
    }

    private static async Task AssertLegacyScanRejectedAsync(
        string mode,
        string candidatesJson)
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = await SeedLegacyScanAsync(database.Factory, mode, candidatesJson);

        Assert.Null(await service.GetDetectionAsync());
        var detailsError = await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetDetailsAsync(scanId, "steam"));
        var removalError = await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam"));
        Assert.Contains("older format", detailsError.Message);
        Assert.Contains("older format", removalError.Message);
    }

    private static async Task<Guid> SeedLegacyScanAsync(
        IDbContextFactory<AppDbContext> factory,
        string mode,
        string candidatesJson)
    {
        var scanId = Guid.NewGuid();
        await using var context = factory.CreateDbContext();
        context.CachedCorruptionScans.Add(new CachedCorruptionScan
        {
            ScanId = scanId,
            DetectionMode = CorruptionDetectionMode.CacheAndLogs,
            Threshold = 3,
            LookbackDays = LookbackDays,
            ContractVersion = 2,
            Status = OperationStatus.Completed.ToWireString(),
            StartedAtUtc = ScanStartedUtc,
            CompletedAtUtc = ScanStartedUtc.AddSeconds(1),
            CreatedAtUtc = ScanStartedUtc.AddSeconds(1)
        });
        context.CachedCorruptionDetections.Add(new CachedCorruptionDetection
        {
            ScanId = scanId,
            ServiceName = "steam",
            DatasourceName = "default",
            CorruptedChunkCount = 2,
            CandidatesJson = candidatesJson,
            RemovalAllowed = true,
            LastDetectedUtc = ScanStartedUtc.AddSeconds(1),
            CreatedAtUtc = ScanStartedUtc.AddSeconds(1)
        });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"CachedCorruptionScans\" SET \"DetectionMode\" = {mode} WHERE \"ScanId\" = {scanId}");
        return scanId;
    }

    private static CorruptionDetectionService NewService(IDbContextFactory<AppDbContext> factory) =>
        new(
            NullLogger<CorruptionDetectionService>.Instance,
            pathResolver: null!,
            rustProcessHelper: null!,
            notifications: null!,
            datasourceService: null!,
            dbContextFactory: factory,
            operationStateService: null!,
            operationTracker: null!);

    private static DatasourceCorruptionReport Report(
        string datasource,
        params CorruptionCandidate[] candidates) =>
        new(datasource, new CorruptionReport
        {
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Threshold = candidates.Length == 0 ? 3 : (int)candidates[0].EvidenceCount,
            LookbackDays = LookbackDays,
            ScanStartedUtc = ScanStartedWire,
            ServiceCounts = candidates
                .GroupBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.LongCount(),
                    StringComparer.OrdinalIgnoreCase),
            Total = candidates.LongLength,
            Candidates = candidates.ToList()
        });

    private static CorruptionCandidate Candidate(string id, int threshold = 3)
    {
        var observations = Enumerable.Range(0, threshold)
            .Select(index => new CandidateObservation
            {
                RawUrl = "/depot/chunk",
                Timestamp = ScanStartedUtc
                    .AddSeconds(index - threshold + 1)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ClientIp = $"192.0.2.{index + 1}",
                Method = "GET",
                HttpStatus = index % 2 == 0 ? 206 : 200,
                CacheStatus = "MISS",
                RawRange = "bytes=1048576-2097151",
                BytesServed = 1_048_576
            })
            .ToList();
        return new CorruptionCandidate
        {
            CandidateId = id,
            Datasource = "default",
            Service = "steam",
            RawUrl = "/depot/chunk",
            NormalizedUri = "/depot/chunk",
            ObservedRange = new ObservedByteRange
            {
                Kind = "inclusive",
                Start = 1_048_576,
                End = 2_097_151
            },
            CacheSlice = new CacheSliceIdentity
            {
                Kind = "ranged",
                Start = 1_048_576,
                End = 2_097_151
            },
            ExactPaths = [$"C:/cache/{id.Replace(':', '_')}"],
            EvidenceCount = threshold,
            FirstSeen = observations[0].Timestamp,
            LastSeen = observations[^1].Timestamp,
            Observations = observations
        };
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestDbContextFactory(options);
            await using var context = factory.CreateDbContext();
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, factory);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestableV2LookbackMigration : AddCorruptionReportV2Lookback
    {
        public IReadOnlyList<MigrationOperation> BuildUpOperations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Up(builder);
            return builder.Operations;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
