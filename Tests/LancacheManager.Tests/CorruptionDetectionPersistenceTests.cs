using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class CorruptionDetectionPersistenceTests
{
    [Theory]
    [InlineData("logs_only", CorruptionDetectionMode.LogsOnly, "logs_only")]
    [InlineData("cache_and_logs", CorruptionDetectionMode.CacheAndLogs, "cache_and_logs")]
    [InlineData("redownload", CorruptionDetectionMode.Redownload, "redownload")]
    [InlineData("miss_count", CorruptionDetectionMode.CacheAndLogs, "cache_and_logs")]
    public void DetectionMode_UsesCanonicalWireValues(
        string input,
        CorruptionDetectionMode expected,
        string canonical)
    {
        Assert.Equal(expected, CorruptionDetectionModeExtensions.Parse(input));
        Assert.Equal(canonical, expected.ToWireString());
        Assert.Equal($"\"{canonical}\"", JsonSerializer.Serialize(expected));
    }

    [Fact]
    public void ScanInput_RejectsUnknownModeAndUnsupportedThreshold()
    {
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput("random", 3));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput("cache_and_logs", 4));
        Assert.Equal(
            CorruptionDetectionMode.CacheAndLogs,
            CorruptionDetectionService.ValidateScanInput("miss_count", 5));
    }

    [Fact]
    public void CandidateJson_RoundTripsExactEvidence()
    {
        var candidate = Candidate("default:mid-slice", CorruptionDetectionMode.Redownload, 5);
        candidate.Observations = NormalizationEquivalentObservations("HIT");
        candidate.ObservedRange = new ObservedByteRange
        {
            Kind = "inclusive",
            Start = 26_380_013,
            End = 32_837_297
        };
        candidate.CacheSlice = new CacheSliceIdentity
        {
            Kind = "ranged",
            Start = 26_214_400,
            End = 27_262_975
        };

        var json = CorruptionDetectionService.SerializeCandidates([candidate]);
        var roundTrip = CorruptionDetectionService.DeserializeCandidates(new CachedCorruptionDetection
        {
            Id = 7,
            DatasourceName = "default",
            CandidatesJson = json
        }).Single();

        Assert.Equal(candidate.CandidateId, roundTrip.CandidateId);
        Assert.Equal(CorruptionDetectionMode.Redownload, roundTrip.Mode);
        Assert.Equal((ulong)26_380_013, roundTrip.ObservedRange.Start);
        Assert.Equal((ulong)26_214_400, roundTrip.CacheSlice.Start);
        Assert.Equal(
            ["/depot/chunk?token=alpha", "/depot/chunk?token=beta"],
            roundTrip.Observations.Select(observation => observation.RawUrl));
        Assert.All(
            roundTrip.Observations,
            observation => Assert.Equal("bytes=26380013-32837297", observation.RawRange));
        Assert.True(roundTrip.RemovalAllowed);
    }

    [Fact]
    public void CandidateObservation_AbsentLegacyRawUrlDeserializesConservatively()
    {
        const string legacyJson =
            """
            {
              "timestamp": "2026-07-11T00:00:00Z",
              "client_ip": "192.0.2.10",
              "method": "GET",
              "http_status": 206,
              "cache_status": "MISS",
              "raw_range": "bytes=26380013-32837297"
            }
            """;

        var observation = Assert.IsType<CandidateObservation>(
            JsonSerializer.Deserialize<CandidateObservation>(legacyJson));

        Assert.Equal(string.Empty, observation.RawUrl);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(observation));
        Assert.Equal(string.Empty, serialized.RootElement.GetProperty("raw_url").GetString());
    }

    [Fact]
    public void ReportValidation_EnforcesModeAndExactPathCapability()
    {
        var logsCandidate = Candidate("review", CorruptionDetectionMode.LogsOnly, 3);
        var logsReport = new CorruptionReport
        {
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Mode = CorruptionDetectionMode.LogsOnly,
            Threshold = 3,
            Candidates = [logsCandidate]
        };
        CorruptionDetectionService.ValidateAndAttachDatasource(
            logsReport,
            "default",
            CorruptionDetectionMode.LogsOnly,
            3);
        Assert.False(logsReport.Candidates.Single().RemovalAllowed);
        Assert.Equal("default:review", logsReport.Candidates.Single().CandidateId);

        var invalidCacheCandidate = Candidate("invalid");
        invalidCacheCandidate.ValidationState = "exact_path_missing";
        var invalidCacheReport = new CorruptionReport
        {
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Mode = CorruptionDetectionMode.CacheAndLogs,
            Threshold = 3,
            Candidates = [invalidCacheCandidate]
        };
        Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                invalidCacheReport,
                "default",
                CorruptionDetectionMode.CacheAndLogs,
                3));
    }

    [Fact]
    public async Task PersistCompletedScan_AtomicallyReplacesAndPersistsZeroResultAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var firstScan = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            firstScan,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow,
            [Report("default", Candidate("default:first"))]);

        var emptyScan = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            emptyScan,
            CorruptionDetectionMode.LogsOnly,
            10,
            DateTime.UtcNow,
            DateTime.UtcNow.AddSeconds(1),
            []);

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(emptyScan, result.ScanId);
        Assert.Equal(CorruptionDetectionMode.LogsOnly, result.DetectionMode);
        Assert.Equal(10, result.Threshold);
        Assert.Empty(result.CorruptionCounts);
        Assert.False(result.RemovalAllowed);

        await using var assertContext = database.Factory.CreateDbContext();
        Assert.Equal(1, await assertContext.CachedCorruptionScans.CountAsync());
        Assert.Equal(0, await assertContext.CachedCorruptionDetections.CountAsync());
    }

    [Fact]
    public async Task PersistCompletedScan_RollsBackReplacementOnFailureAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var originalScan = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            originalScan,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            DateTime.UtcNow,
            DateTime.UtcNow,
            [Report("default", Candidate("default:original"))]);

        var invalidReport = Report("secondary", Candidate("secondary:new"));
        invalidReport.Report.ContractVersion = 2;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PersistCompletedScanAsync(
            Guid.NewGuid(),
            CorruptionDetectionMode.CacheAndLogs,
            3,
            DateTime.UtcNow,
            DateTime.UtcNow,
            [Report("default", Candidate("default:new")), invalidReport]));

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(originalScan, result.ScanId);
        Assert.Equal(1, result.TotalCorruptedChunks);
    }

    [Fact]
    public async Task Details_AreScanBoundAndNeverReinterpretedAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var candidate = Candidate("secondary:retry", CorruptionDetectionMode.Redownload, 5);
        candidate.Observations = NormalizationEquivalentObservations("HIT");
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.Redownload,
            5,
            DateTime.UtcNow,
            DateTime.UtcNow,
            [Report("secondary", candidate)]);

        var details = await service.GetDetailsAsync(scanId, "steam");
        var detail = Assert.Single(details);
        Assert.Equal("secondary", detail.Datasource);
        Assert.Equal(CorruptionDetectionMode.Redownload, detail.Mode);
        Assert.Equal(5, detail.Threshold);
        Assert.Equal(
            ["/depot/chunk?token=alpha", "/depot/chunk?token=beta"],
            detail.Observations.Select(observation => observation.RawUrl));

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetDetailsAsync(Guid.NewGuid(), "steam"));
    }

    [Fact]
    public async Task Details_RejectUnknownScanAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetDetailsAsync(Guid.NewGuid(), "steam"));
    }

    [Fact]
    public async Task RemovalSelection_RejectsLogsOnlyAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var candidate = Candidate("default:review", CorruptionDetectionMode.LogsOnly, 3);
        candidate.RemovalAllowed = false;
        candidate.ValidationState = "log_suspect";
        candidate.ExactPaths = [];
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.LogsOnly,
            3,
            DateTime.UtcNow,
            DateTime.UtcNow,
            [Report("default", candidate)]);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam"));
    }

    [Fact]
    public async Task RemovalSelection_CanOnlyNarrowStoredCandidatesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var selectedCandidate = Candidate("default:b");
        selectedCandidate.Observations = NormalizationEquivalentObservations("MISS");
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            DateTime.UtcNow,
            DateTime.UtcNow,
            [Report("default", Candidate("default:a"), selectedCandidate)]);

        var selection = await service.GetRemovalSelectionAsync(
            scanId,
            "steam",
            ["default:b"]);
        Assert.Equal(["default:b"], selection.CandidateIds);
        Assert.Equal(
            ["/depot/chunk?token=alpha", "/depot/chunk?token=beta"],
            Assert.Single(selection.CandidatesByDatasource["default"])
                .Observations
                .Select(observation => observation.RawUrl));

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam", ["default:handcrafted"]));

        var unchanged = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(2, unchanged.TotalCorruptedChunks);
    }

    [Fact]
    public async Task RemovalEvidence_IsPrunedOnlyAfterRecordedSuccessAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            DateTime.UtcNow,
            DateTime.UtcNow,
            [Report("default", Candidate("default:a"), Candidate("default:b"))]);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.ApplyRemovalSuccessAsync(scanId, ["default:missing"]));
        Assert.Equal(2, (await service.GetDetectionAsync())!.TotalCorruptedChunks);

        await service.ApplyRemovalSuccessAsync(scanId, ["default:a"]);
        var remaining = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(scanId, remaining.ScanId);
        Assert.Equal(1, remaining.TotalCorruptedChunks);
        Assert.Equal("default:b", (await service.GetDetailsAsync(scanId, "steam")).Single().CandidateId);
    }

    [Fact]
    public async Task LogEntryModel_KeepsMethodAndNullableRangeAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var entity = context.Model.FindEntityType(typeof(LogEntryRecord));
        Assert.NotNull(entity?.FindProperty(nameof(LogEntryRecord.Method)));
        Assert.True(entity?.FindProperty(nameof(LogEntryRecord.HttpRange))?.IsNullable);
    }

    [Fact]
    public void DetailAndRemovalEndpoints_AreBoundOnlyToStoredScanIdentity()
    {
        var detailParameters = typeof(CacheController)
            .GetMethod(nameof(CacheController.GetCorruptionDetailsAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("scanId", detailParameters);
        Assert.DoesNotContain("threshold", detailParameters);
        Assert.DoesNotContain("detectionMode", detailParameters);
        Assert.DoesNotContain("compareToCacheLogs", detailParameters);

        var singleRemovalParameters = typeof(CacheController)
            .GetMethod(nameof(CacheController.RemoveCorruptedChunksAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("scanId", singleRemovalParameters);
        Assert.Contains("candidateIds", singleRemovalParameters);
        Assert.DoesNotContain("threshold", singleRemovalParameters);
        Assert.DoesNotContain("detectionMode", singleRemovalParameters);
        Assert.DoesNotContain("compareToCacheLogs", singleRemovalParameters);

        var bulkRemovalParameters = typeof(CacheController)
            .GetMethod(nameof(CacheController.RemoveAllCorruptedChunksAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("scanId", bulkRemovalParameters);
        Assert.DoesNotContain("threshold", bulkRemovalParameters);
        Assert.DoesNotContain("detectionMode", bulkRemovalParameters);
        Assert.DoesNotContain("compareToCacheLogs", bulkRemovalParameters);
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
        params CorruptionCandidate[] candidates)
    {
        var mode = candidates.FirstOrDefault()?.Mode ?? CorruptionDetectionMode.CacheAndLogs;
        var threshold = candidates.FirstOrDefault()?.Threshold ?? 3;
        return new DatasourceCorruptionReport(datasource, new CorruptionReport
        {
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Mode = mode,
            Threshold = threshold,
            Candidates = candidates.ToList(),
            Total = candidates.Length
        });
    }

    private static CorruptionCandidate Candidate(
        string id,
        CorruptionDetectionMode mode = CorruptionDetectionMode.CacheAndLogs,
        int threshold = 3) =>
        new()
        {
            CandidateId = id,
            Datasource = id.Split(':')[0],
            Mode = mode,
            Threshold = threshold,
            Service = "steam",
            RawUrl = "/depot/chunk",
            NormalizedUri = "/depot/chunk",
            ObservedRange = new ObservedByteRange { Kind = "inclusive", Start = 1_048_576, End = 2_097_151 },
            CacheSlice = new CacheSliceIdentity { Kind = "ranged", Start = 1_048_576, End = 2_097_151 },
            ExactPaths = [$"C:/cache/{id.Replace(':', '_')}"] ,
            EvidenceCount = threshold,
            FirstSeen = "2026-07-11T00:00:00Z",
            LastSeen = "2026-07-11T00:00:10Z",
            RetryClient = mode == CorruptionDetectionMode.Redownload ? "192.0.2.10" : null,
            Reason = mode == CorruptionDetectionMode.Redownload
                ? "same_client_hit_retry_burst"
                : "repeated_miss_burst",
            ValidationState = "exact_path_present",
            RemovalAllowed = true,
            Observations =
            [
                new CandidateObservation
                {
                    Timestamp = "2026-07-11T00:00:00Z",
                    ClientIp = "192.0.2.10",
                    Method = "GET",
                    HttpStatus = 206,
                    CacheStatus = mode == CorruptionDetectionMode.Redownload ? "HIT" : "MISS",
                    RawUrl = "/depot/chunk",
                    RawRange = "bytes=26380013-32837297"
                }
            ]
        };

    private static List<CandidateObservation> NormalizationEquivalentObservations(string cacheStatus) =>
    [
        new CandidateObservation
        {
            RawUrl = "/depot/chunk?token=alpha",
            Timestamp = "2026-07-11T00:00:00Z",
            ClientIp = "192.0.2.10",
            Method = "GET",
            HttpStatus = 206,
            CacheStatus = cacheStatus,
            RawRange = "bytes=26380013-32837297"
        },
        new CandidateObservation
        {
            RawUrl = "/depot/chunk?token=beta",
            Timestamp = "2026-07-11T00:00:10Z",
            ClientIp = "192.0.2.10",
            Method = "GET",
            HttpStatus = 206,
            CacheStatus = cacheStatus,
            RawRange = "bytes=26380013-32837297"
        }
    ];

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

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
