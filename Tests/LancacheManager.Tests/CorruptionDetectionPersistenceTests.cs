using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Data.Migrations;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
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
    public void ContractV4_ExposesOnlyClosedPublicMethods()
    {
        Assert.Equal(4, CorruptionReport.SupportedContractVersion);
        Assert.True(CorruptionDetectionMethodExtensions.TryParseWire(
            "repeated_miss", out var repeatedMiss));
        Assert.Equal(CorruptionDetectionMethod.RepeatedMiss, repeatedMiss);
        Assert.True(CorruptionDetectionMethodExtensions.TryParseWire(
            "structural", out var structural));
        Assert.Equal(CorruptionDetectionMethod.Structural, structural);

        foreach (var invalid in new[] { "", "behavior", "combined", "cache_and_logs", "Structural", "1" })
        {
            Assert.False(CorruptionDetectionMethodExtensions.TryParseWire(invalid, out _));
        }

        Assert.Equal(
            CorruptionDetectionMode.CacheAndLogs,
            CorruptionDetectionModeExtensions.Parse("cache_and_logs"));
        Assert.Equal(
            CorruptionDetectionMode.RepeatedMiss,
            CorruptionDetectionModeExtensions.Parse("repeated_miss"));
        Assert.Equal(
            CorruptionDetectionMode.Structural,
            CorruptionDetectionModeExtensions.Parse("structural"));
    }

    [Fact]
    public void ScanInputAndControllerSurface_AreClosed()
    {
        CorruptionDetectionService.ValidateScanInput(3, 1, CorruptionDetectionMethod.RepeatedMiss);
        CorruptionDetectionService.ValidateScanInput(10, 365, CorruptionDetectionMethod.Structural);
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput(4, LookbackDays));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput(3, 0));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput(
                3, LookbackDays, (CorruptionDetectionMethod)99));

        var parameters = typeof(CacheController)
            .GetMethod(nameof(CacheController.StartCorruptionDetectionAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToList();
        Assert.Equal(["threshold", "lookbackDays", "detectionMethod", "cancellationToken"], parameters);
        Assert.DoesNotContain(parameters, name => name is "path" or "reason" or "evidence" or "datasource");
    }

    [Fact]
    public void ContractV4_StrictlyRejectsUnknownFieldsKindsAndIssues()
    {
        var valid = JsonSerializer.Serialize(StructuralReport("default", StructuralCandidate("structural" )).Report);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CorruptionReport>(
            valid.Replace("\"total\":1", "\"unexpected\":true,\"total\":1", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CorruptionReport>(
            valid.Replace("\"service\":\"steam\"", "\"unknown\":1,\"service\":\"steam\"", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CorruptionReport>(
            valid.Replace("\"kind\":\"structural\"", "\"kind\":\"combined\"", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CorruptionReport>(
            valid.Replace("\"empty_cache_file\"", "\"unknown_issue\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void CandidateJson_RoundTripsBothTaggedEvidenceBranches()
    {
        var candidates = new[] { RepeatedMissCandidate("miss"), StructuralCandidate("structural") };
        foreach (var candidate in candidates)
        {
            candidate.Datasource = "default";
        }

        var json = CorruptionDetectionService.SerializeCandidates(candidates);
        var roundTrip = CorruptionDetectionService.DeserializeCandidates(new CachedCorruptionDetection
        {
            Id = 7,
            DatasourceName = "default",
            CandidatesJson = json
        });

        Assert.IsType<RepeatedMissCorruptionEvidence>(roundTrip[0].Evidence);
        Assert.IsType<StructuralCorruptionEvidence>(roundTrip[1].Evidence);
        Assert.All(roundTrip, candidate => Assert.Equal("default", candidate.Datasource));
    }

    [Fact]
    public void ReportValidation_AcceptsCanonicalRepeatedMissAndStructuralReports()
    {
        var repeatedMiss = RepeatedMissReport("default", RepeatedMissCandidate("miss"));
        CorruptionDetectionService.ValidateAndAttachDatasource(
            repeatedMiss.Report,
            "default",
            3,
            LookbackDays,
            CorruptionDetectionMethod.RepeatedMiss,
            ScanStartedWire);
        Assert.Equal("default:miss", Assert.Single(repeatedMiss.Report.Candidates).CandidateId);

        var structural = StructuralReport("secondary", StructuralCandidate("structural"));
        CorruptionDetectionService.ValidateAndAttachDatasource(
            structural.Report,
            "secondary",
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            ScanStartedWire);
        var candidate = Assert.Single(structural.Report.Candidates);
        Assert.Equal("secondary:structural", candidate.CandidateId);
        Assert.IsType<StructuralCorruptionEvidence>(candidate.Evidence);
    }

    [Theory]
    [InlineData(StructuralCorruptionIssue.EmptyCacheFile)]
    [InlineData(StructuralCorruptionIssue.TruncatedCacheHeader)]
    [InlineData(StructuralCorruptionIssue.MalformedCacheHeader)]
    [InlineData(StructuralCorruptionIssue.InvalidPayloadOffset)]
    [InlineData(StructuralCorruptionIssue.TruncatedBeforePayload)]
    public void StructuralEnvelopeFindings_AcceptMissingCacheKey(
        StructuralCorruptionIssue issue)
    {
        var candidate = StructuralCandidate(issue.ToString());
        var evidence = Assert.IsType<StructuralCorruptionEvidence>(candidate.Evidence);
        evidence.Issues = [issue];
        evidence.CacheKey = string.Empty;
        var report = StructuralReport("default", candidate);

        CorruptionDetectionService.ValidateAndAttachDatasource(
            report.Report,
            "default",
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            ScanStartedWire);
    }

    [Theory]
    [InlineData(StructuralCorruptionIssue.TruncatedCacheHeader)]
    [InlineData(StructuralCorruptionIssue.MalformedCacheHeader)]
    public void StructuralHeaderFindings_AcceptOffsetsBeyondFileLength(
        StructuralCorruptionIssue issue)
    {
        var candidate = StructuralCandidate(issue.ToString());
        var evidence = Assert.IsType<StructuralCorruptionEvidence>(candidate.Evidence);
        // The scanner proves the header offset before it can prove the file holds it, so a
        // truncated/malformed header carries body_start (and header_start) beyond the file
        // length while tagged only with its own issue. Validation must accept that instead of
        // failing the entire structural scan.
        evidence.Issues = [issue];
        evidence.CacheKey = string.Empty;
        evidence.FileLength = 40;
        evidence.HeaderStart = 54;
        evidence.BodyStart = 56;
        evidence.Fingerprint.Length = 40;
        Assert.True(evidence.BodyStart > evidence.FileLength);
        var report = StructuralReport("default", candidate);

        CorruptionDetectionService.ValidateAndAttachDatasource(
            report.Report,
            "default",
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            ScanStartedWire);
    }

    [Fact]
    public void ReportValidation_RejectsMethodEvidenceCountsAndPhysicalIdentityMismatches()
    {
        AssertInvalid(RepeatedMissReport("default", RepeatedMissCandidate("wrong-method")),
            CorruptionDetectionMethod.Structural);

        var noPath = StructuralReport("default", StructuralCandidate("no-path"));
        noPath.Report.Candidates[0].ExactPaths = [];
        AssertInvalid(noPath, CorruptionDetectionMethod.Structural);

        var twoPaths = StructuralReport("default", StructuralCandidate("two-paths"));
        twoPaths.Report.Candidates[0].ExactPaths.Add("C:/cache/other");
        AssertInvalid(twoPaths, CorruptionDetectionMethod.Structural);

        var forgedDatasource = StructuralReport("default", StructuralCandidate("forged-datasource"));
        forgedDatasource.Report.Candidates[0].Datasource = "forged";
        AssertInvalid(forgedDatasource, CorruptionDetectionMethod.Structural);

        var duplicateId = StructuralReport(
            "default", StructuralCandidate("same", 1), StructuralCandidate("same", 2));
        AssertInvalid(duplicateId, CorruptionDetectionMethod.Structural);

        var duplicatePath = StructuralReport(
            "default", StructuralCandidate("first"), StructuralCandidate("second"));
        AssertInvalid(duplicatePath, CorruptionDetectionMethod.Structural);

        var badCounts = StructuralReport("default", StructuralCandidate("counts"));
        badCounts.Report.DetectionCounts["structural"] = 2;
        AssertInvalid(badCounts, CorruptionDetectionMethod.Structural);

        var badSettings = StructuralReport("default", StructuralCandidate("settings"));
        badSettings.Report.Settings.MaximumPrefixBytes = 65_534;
        AssertInvalid(badSettings, CorruptionDetectionMethod.Structural);

        var badCoverage = StructuralReport("default", StructuralCandidate("coverage"));
        badCoverage.Report.Coverage!.FilesChecked = 3;
        AssertInvalid(badCoverage, CorruptionDetectionMethod.Structural);

        static void AssertInvalid(
            DatasourceCorruptionReport datasourceReport,
            CorruptionDetectionMethod method) =>
            Assert.Throws<InvalidDataException>(() =>
                CorruptionDetectionService.ValidateAndAttachDatasource(
                    datasourceReport.Report,
                    "default",
                    3,
                    LookbackDays,
                    method,
                    ScanStartedWire));
    }

    [Fact]
    public void RepeatedMissValidation_RetainsClosedObservationRules()
    {
        var report = RepeatedMissReport("default", RepeatedMissCandidate("invalid"));
        var evidence = Assert.IsType<RepeatedMissCorruptionEvidence>(report.Report.Candidates[0].Evidence);
        evidence.Observations[0].CacheStatus = "HIT";
        Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                report.Report,
                "default",
                3,
                LookbackDays,
                CorruptionDetectionMethod.RepeatedMiss,
                ScanStartedWire));
    }

    [Fact]
    public void RepeatedMissValidation_RejectsNonActionableStoredEvidence()
    {
        foreach (var mutation in Enumerable.Range(0, 4))
        {
            var report = RepeatedMissReport("default", RepeatedMissCandidate($"invalid-{mutation}"));
            var evidence = Assert.IsType<RepeatedMissCorruptionEvidence>(report.Report.Candidates[0].Evidence);
            switch (mutation)
            {
                case 0:
                    evidence.EvidenceCount = 4;
                    evidence.Observations.Add(evidence.Observations[^1]);
                    break;
                case 1:
                    evidence.Observations[0].ClientIp = string.Empty;
                    break;
                case 2:
                    evidence.Observations.Reverse();
                    break;
                case 3:
                    evidence.Observations[0].RawRange = "bytes=0-1";
                    break;
            }

            Assert.Throws<InvalidDataException>(() =>
                CorruptionDetectionService.ValidateAndAttachDatasource(
                    report.Report,
                    "default",
                    3,
                    LookbackDays,
                    CorruptionDetectionMethod.RepeatedMiss,
                    ScanStartedWire));
        }
    }

    [Fact]
    public async Task PersistCompletedScan_UsesExistingSchemaAndProjectsStructuralCoverageAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var report = StructuralReport("default", StructuralCandidate("candidate"));
        Validate(report, CorruptionDetectionMethod.Structural, "default");
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [report]);

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(CorruptionDetectionMethod.Structural, result.DetectionMethod);
        Assert.Equal(1, result.DetectionCounts["structural"]);
        Assert.Equal(4, result.Coverage?.FilesSeen);
        Assert.Equal(1, result.TotalCorruptedChunks);

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(CorruptionDetectionMode.Structural,
            (await context.CachedCorruptionScans.SingleAsync()).DetectionMode);
        Assert.Equal(2, await context.CachedCorruptionDetections.CountAsync());
        Assert.DoesNotContain(
            context.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()),
            property => property.Name.Contains("Coverage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulV4ZeroResult_ReplacesV3WithoutDeserializingLegacyCandidatesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var legacyScanId = await SeedV3ScanAsync(
            database.Factory,
            "{ deliberately invalid candidate JSON }");

        Assert.Null(await service.GetDetectionAsync());
        await Assert.ThrowsAsync<ConflictException>(() => service.GetDetailsAsync(legacyScanId, "steam"));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetRemovalSelectionAsync(legacyScanId, "steam"));

        var emptyReport = RepeatedMissReport("default");
        Validate(emptyReport, CorruptionDetectionMethod.RepeatedMiss, "default");
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            CorruptionDetectionMethod.RepeatedMiss,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(2),
            [emptyReport]);

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(scanId, result.ScanId);
        Assert.Equal(0, result.DetectionCounts["repeated_miss"]);
        Assert.Empty(result.CorruptionCounts);
    }

    [Fact]
    public async Task PersistCompletedScan_RejectsDuplicatePathsAcrossDatasourcesBeforeReplacementAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var first = StructuralReport("first", StructuralCandidate("candidate"));
        var second = StructuralReport("second", StructuralCandidate("candidate"));
        Validate(first, CorruptionDetectionMethod.Structural, "first");
        Validate(second, CorruptionDetectionMethod.Structural, "second");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.PersistCompletedScanAsync(
            Guid.NewGuid(),
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [first, second]));
        Assert.Null(await service.GetDetectionAsync());
    }

    [Fact]
    public async Task RemovalSelection_IsServerOwnedNarrowingAndMethodAwareAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var first = StructuralCandidate("first", 1);
        var second = StructuralCandidate("second", 2);
        var report = StructuralReport("default", first, second);
        Validate(report, CorruptionDetectionMethod.Structural, "default");
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [report]);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam", ["forged"]));
        var selection = await service.GetRemovalSelectionAsync(
            scanId,
            "steam",
            ["default:first"]);
        Assert.True(selection.HasStructuralEvidence);
        Assert.False(selection.HasRepeatedMissEvidence);
        Assert.Equal(CorruptionDetectionMethod.Structural, selection.DetectionMethod);
        Assert.Equal(["default:first"], selection.CandidateIds);

        await service.ApplyRemovalSuccessAsync(scanId, selection.CandidateIds);
        Assert.Equal("default:second", Assert.Single(await service.GetDetailsAsync(scanId, "steam")).CandidateId);
        var cached = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(1, cached.TotalCorruptedChunks);
        Assert.Equal(1, cached.DetectionCounts["structural"]);
    }

    [Fact]
    public void PublicCachedDetailsAndSignalRContracts_AreMethodAwareAndCamelCase()
    {
        var candidateJson = JsonSerializer.Serialize(
            CorruptionCandidateResponse.From(StructuralCandidate("candidate")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"candidateId\"", candidateJson);
        Assert.Contains("\"cacheKeyMd5\"", candidateJson);
        Assert.DoesNotContain("cache_key_md5", candidateJson, StringComparison.Ordinal);

        var completion = new SignalRNotifications.CorruptionDetectionComplete(
            Success: true,
            OperationId: Guid.NewGuid(),
            StageKey: "signalr.corruptionDetect.complete",
            DetectionMethod: "structural",
            DetectionCounts: new Dictionary<string, long> { ["structural"] = 1 },
            Coverage: CorruptionScanCoverageResponse.From(StructuralCoverage()));
        var completionJson = JsonSerializer.Serialize(
            completion,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"detectionMethod\":\"structural\"", completionJson);
        Assert.Contains("\"detectionCounts\"", completionJson);
        Assert.Contains("\"filesSeen\"", completionJson);
    }

    [Fact]
    public void PersistenceModelAndMigrationSnapshotRemainUnchanged()
    {
        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.CachedCorruptionScans)));
        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.CachedCorruptionDetections)));
        Assert.NotNull(typeof(CachedCorruptionScan).GetProperty(nameof(CachedCorruptionScan.DetectionMode)));
        Assert.Null(typeof(CachedCorruptionScan).GetProperty("Coverage"));

        var operations = new TestableV2LookbackMigration().BuildUpOperations();
        Assert.Contains(operations, operation =>
            operation is AddColumnOperation column && column.Name == "LookbackDays");
    }

    private static void Validate(
        DatasourceCorruptionReport report,
        CorruptionDetectionMethod method,
        string datasource) =>
        CorruptionDetectionService.ValidateAndAttachDatasource(
            report.Report,
            datasource,
            3,
            LookbackDays,
            method,
            ScanStartedWire);

    private static DatasourceCorruptionReport RepeatedMissReport(
        string datasource,
        params CorruptionCandidate[] candidates) =>
        Report(datasource, CorruptionDetectionMethod.RepeatedMiss, candidates);

    private static DatasourceCorruptionReport StructuralReport(
        string datasource,
        params CorruptionCandidate[] candidates) =>
        Report(datasource, CorruptionDetectionMethod.Structural, candidates);

    private static DatasourceCorruptionReport Report(
        string datasource,
        CorruptionDetectionMethod method,
        params CorruptionCandidate[] candidates) =>
        new(datasource, new CorruptionReport
        {
            ContractVersion = CorruptionReport.SupportedContractVersion,
            DetectionMethod = method,
            ScanStartedUtc = ScanStartedWire,
            Settings = method == CorruptionDetectionMethod.RepeatedMiss
                ? new CorruptionScanSettings { Threshold = 3, LookbackDays = LookbackDays }
                : new CorruptionScanSettings { MinimumStableAgeSeconds = 600, MaximumPrefixBytes = 65_535 },
            ServiceCounts = candidates
                .GroupBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.OrdinalIgnoreCase),
            DetectionCounts = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [method.ToWireString()] = candidates.LongLength
            },
            Coverage = method == CorruptionDetectionMethod.Structural
                ? StructuralCoverage(candidates.LongLength)
                : null,
            Total = candidates.LongLength,
            Candidates = candidates.ToList()
        });

    private static CorruptionCandidate RepeatedMissCandidate(string id)
    {
        var observations = Enumerable.Range(0, 3)
            .Select(index => new CandidateObservation
            {
                RawUrl = "/depot/chunk",
                Timestamp = ScanStartedUtc.AddSeconds(index - 2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
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
            Service = "steam",
            ExactPaths = [$"C:/cache/{id}"],
            Evidence = new RepeatedMissCorruptionEvidence
            {
                RawUrl = "/depot/chunk",
                NormalizedUri = "/depot/chunk",
                ObservedRange = new ObservedByteRange { Kind = "inclusive", Start = 1_048_576, End = 2_097_151 },
                CacheSlice = new CacheSliceIdentity { Kind = "ranged", Start = 1_048_576, End = 2_097_151 },
                EvidenceCount = 3,
                FirstSeen = observations[0].Timestamp,
                LastSeen = observations[^1].Timestamp,
                Observations = observations
            }
        };
    }

    private static CorruptionCandidate StructuralCandidate(string id, int pathSuffix = 0) => new()
    {
        CandidateId = id,
        Service = "steam",
        ExactPaths = [$"C:/cache/aa/bb/{pathSuffix:D32}"],
        Evidence = new StructuralCorruptionEvidence
        {
            Issues = [StructuralCorruptionIssue.EmptyCacheFile],
            CacheKeyEncoding = "hex",
            CacheKey = string.Empty,
            CacheKeyMd5 = "d41d8cd98f00b204e9800998ecf8427e",
            CacheVersion = 5,
            FileLength = 0,
            Fingerprint = new StructuralFileFingerprint
            {
                Device = 1,
                Inode = (ulong)(pathSuffix + 1),
                Length = 0,
                ModifiedNanoseconds = 1,
                ChangedNanoseconds = 1
            },
            DetectedAtUtc = ScanStartedUtc.AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        }
    };

    private static CorruptionScanCoverage StructuralCoverage(long candidateCount = 1) => new()
    {
        FilesSeen = 4,
        FilesChecked = 1 + candidateCount,
        Consistent = 1,
        BytesRead = 512,
        SparseFiles = 0,
        SkippedByReason = new Dictionary<string, long> { ["recent"] = 1 },
        IoErrors = 0
    };

    private static async Task<Guid> SeedV3ScanAsync(
        IDbContextFactory<AppDbContext> factory,
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
            ContractVersion = 3,
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
            CorruptedChunkCount = 1,
            CandidatesJson = candidatesJson,
            RemovalAllowed = true,
            LastDetectedUtc = ScanStartedUtc.AddSeconds(1),
            CreatedAtUtc = ScanStartedUtc.AddSeconds(1)
        });
        await context.SaveChangesAsync();
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
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
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
