using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Data.Migrations;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
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
        Assert.True(StructuralScanModeExtensions.TryParseWire("full", out var full));
        Assert.Equal(StructuralScanMode.Full, full);
        Assert.True(StructuralScanModeExtensions.TryParseWire("incremental", out var incremental));
        Assert.Equal(StructuralScanMode.Incremental, incremental);

        foreach (var invalid in new[] { "", "behavior", "combined", "cache_and_logs", "Structural", "1" })
        {
            Assert.False(CorruptionDetectionMethodExtensions.TryParseWire(invalid, out _));
        }
        foreach (var invalid in new[] { "", "Full", "INCREMENTAL", "baseline", "1" })
        {
            Assert.False(StructuralScanModeExtensions.TryParseWire(invalid, out _));
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
        Assert.Null(CorruptionDetectionService.ResolveStructuralScanMode(
            CorruptionDetectionMethod.RepeatedMiss, null));
        Assert.Equal(
            StructuralScanMode.Full,
            CorruptionDetectionService.ResolveStructuralScanMode(
                CorruptionDetectionMethod.Structural, null));
        Assert.Equal(
            StructuralScanMode.Incremental,
            CorruptionDetectionService.ResolveStructuralScanMode(
                CorruptionDetectionMethod.Structural, "incremental"));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ResolveStructuralScanMode(
                CorruptionDetectionMethod.RepeatedMiss, "full"));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ResolveStructuralScanMode(
                CorruptionDetectionMethod.Structural, "Full"));
        Assert.Equal(
            "Corruption Detection",
            CorruptionDetectionService.DetectionOperationName(
                CorruptionDetectionMethod.RepeatedMiss,
                null));
        Assert.Equal(
            "Structural Corruption Detection (full)",
            CorruptionDetectionService.DetectionOperationName(
                CorruptionDetectionMethod.Structural,
                StructuralScanMode.Full));
        Assert.Equal(
            "Structural Corruption Detection (incremental)",
            CorruptionDetectionService.DetectionOperationName(
                CorruptionDetectionMethod.Structural,
                StructuralScanMode.Incremental));

        var parameters = typeof(CacheController)
            .GetMethod(nameof(CacheController.StartCorruptionDetectionAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToList();
        Assert.Equal(["threshold", "lookbackDays", "detectionMethod", "scanMode", "cancellationToken"], parameters);
        Assert.DoesNotContain(parameters, name => name is "path" or "reason" or "evidence" or "datasource");
    }

    [Fact]
    public void ContractV4_StrictlyRejectsMissingAndUnknownFieldsKindsAndIssues()
    {
        var valid = JsonSerializer.Serialize(StructuralReport("default", StructuralCandidate("structural" )).Report);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CorruptionReport>(
            valid.Replace("\"cancelled\":false,", string.Empty, StringComparison.Ordinal)));
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

    [Fact]
    public void IncrementalCoverageCountsReusedConsistentFilesWithoutClaimingHeaderReads()
    {
        var report = StructuralReport("default", StructuralCandidate("structural"));
        report.Report.Coverage = new CorruptionScanCoverage
        {
            FilesSeen = 4,
            FilesChecked = 1,
            Consistent = 1,
            BytesRead = 128,
            SparseFiles = 0,
            SkippedByReason = new Dictionary<string, long> { ["recent"] = 2 },
            IoErrors = 0
        };

        CorruptionDetectionService.ValidateAndAttachDatasource(
            report.Report,
            "default",
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            StructuralScanMode.Incremental,
            ScanStartedWire,
            new StructuralScanStatusResponse
            {
                ScanMode = "incremental",
                EffectiveScanMode = "incremental",
                BaselineStatus = "ready",
                FilesDiscovered = 4,
                FilesProcessed = 4,
                FilesReused = 1,
                FilesInspected = 3,
                InvalidFiles = 1,
                FilesPendingRetry = 2,
                StateEntries = 2,
                StateCommitted = true
            });

        Assert.Equal(1, report.Report.Coverage.FilesChecked);
        Assert.Equal(1, report.Report.Coverage.Consistent);
    }

    [Fact]
    public void StructuralReportValidation_RejectsIncompleteTerminalState()
    {
        var report = StructuralReport("default", StructuralCandidate("structural"));

        var error = Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                report.Report,
                "default",
                3,
                LookbackDays,
                CorruptionDetectionMethod.Structural,
                StructuralScanMode.Incremental,
                ScanStartedWire,
                new StructuralScanStatusResponse
                {
                    ScanMode = "incremental",
                    EffectiveScanMode = "incremental",
                    BaselineStatus = "incomplete",
                    FilesDiscovered = 4,
                    FilesProcessed = 4,
                    FilesInspected = 4,
                    InvalidFiles = 1,
                    FilesPendingRetry = 1,
                    StateEntries = 2,
                    StateCommitted = false
                }));

        Assert.Contains("progress summary", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledReport_CannotValidateOrPersistAsCompletedAsync()
    {
        var report = StructuralReport("default", StructuralCandidate("structural"));
        report.Report.Cancelled = true;

        var validationError = Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                report.Report,
                "default",
                3,
                LookbackDays,
                CorruptionDetectionMethod.Structural,
                ScanStartedWire));
        Assert.Contains("cancelled", validationError.Message, StringComparison.OrdinalIgnoreCase);

        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var persistenceError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PersistCompletedScanAsync(
                Guid.NewGuid(),
                3,
                LookbackDays,
                CorruptionDetectionMethod.Structural,
                ScanStartedUtc,
                ScanStartedUtc.AddSeconds(1),
                [report],
                StructuralScanMode.Full));
        Assert.Contains("cancelled", persistenceError.Message, StringComparison.OrdinalIgnoreCase);

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.CachedCorruptionScans.ToListAsync());
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
        Validate(
            report,
            CorruptionDetectionMethod.Structural,
            "default",
            StructuralScanMode.Incremental);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            CorruptionDetectionMethod.Structural,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [report],
            StructuralScanMode.Incremental);

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
            CorruptionDetectionMethod.Structural));
        Assert.Equal(CorruptionDetectionMethod.Structural, result.DetectionMethod);
        Assert.Equal(StructuralScanMode.Incremental, result.ScanMode);
        Assert.Equal(1, result.DetectionCounts["structural"]);
        Assert.Equal(4, result.Coverage?.FilesSeen);
        Assert.Equal(1, result.TotalCorruptedChunks);

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(CorruptionDetectionMode.Structural,
            (await context.CachedCorruptionScans.SingleAsync()).DetectionMode);
        Assert.Equal(
            StructuralScanMode.Incremental,
            (await context.CachedCorruptionScans.SingleAsync()).ScanMode);
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

        Assert.Null(await service.GetDetectionAsync(CorruptionDetectionMethod.RepeatedMiss));
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

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
            CorruptionDetectionMethod.RepeatedMiss));
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
        Assert.Null(await service.GetDetectionAsync(CorruptionDetectionMethod.Structural));
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
        var cached = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
            CorruptionDetectionMethod.Structural));
        Assert.Equal(1, cached.TotalCorruptedChunks);
        Assert.Equal(1, cached.DetectionCounts["structural"]);
    }

    [Fact]
    public async Task CompletedScans_CoexistPerMethodAndPersistRequestedStructuralModeAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var repeatedMissScanId = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var structuralScanId = Guid.Parse("00000000-0000-0000-0000-000000000012");

        await PersistScanAsync(
            service,
            repeatedMissScanId,
            CorruptionDetectionMethod.RepeatedMiss,
            sequence: 11,
            ScanStartedUtc.AddSeconds(1));
        await PersistScanAsync(
            service,
            structuralScanId,
            CorruptionDetectionMethod.Structural,
            sequence: 12,
            ScanStartedUtc.AddSeconds(2),
            StructuralScanMode.Incremental);

        var repeatedMiss = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
            CorruptionDetectionMethod.RepeatedMiss));
        var structural = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
            CorruptionDetectionMethod.Structural));
        Assert.Equal(repeatedMissScanId, repeatedMiss.ScanId);
        Assert.Null(repeatedMiss.ScanMode);
        Assert.Equal(structuralScanId, structural.ScanId);
        Assert.Equal(StructuralScanMode.Incremental, structural.ScanMode);
        Assert.Equal(
            repeatedMissScanId,
            (await service.GetCurrentDetectionByScanIdAsync(repeatedMissScanId)).ScanId);
        Assert.Equal(
            structuralScanId,
            (await service.GetCurrentDetectionByScanIdAsync(structuralScanId)).ScanId);

        await using var context = database.Factory.CreateDbContext();
        var scans = await context.CachedCorruptionScans.AsNoTracking().ToListAsync();
        Assert.Equal(2, scans.Count);
        Assert.All(scans, scan => Assert.True(scan.IsCurrent));
        Assert.Single(scans, scan => scan.DetectionMode == CorruptionDetectionMode.RepeatedMiss);
        Assert.Single(scans, scan => scan.DetectionMode == CorruptionDetectionMode.Structural);
    }

    [Fact]
    public async Task Retention_IsDeterministicPerMethodAndIncludesZeroResultScansAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var structuralScanId = Guid.Parse("00000000-0000-0000-0000-000000000100");
        await PersistScanAsync(
            service,
            structuralScanId,
            CorruptionDetectionMethod.Structural,
            sequence: 100,
            ScanStartedUtc.AddSeconds(1));

        var repeatedMissScanIds = Enumerable.Range(1, 4)
            .Select(sequence => Guid.Parse($"00000000-0000-0000-0000-{sequence:D12}"))
            .ToList();
        foreach (var (scanId, index) in repeatedMissScanIds.Select((scanId, index) => (scanId, index)))
        {
            await PersistScanAsync(
                service,
                scanId,
                CorruptionDetectionMethod.RepeatedMiss,
                sequence: index + 1,
                index == 3 ? ScanStartedUtc.AddSeconds(1) : ScanStartedUtc.AddSeconds(5),
                empty: index == 3);
        }

        var current = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
            CorruptionDetectionMethod.RepeatedMiss));
        Assert.Equal(repeatedMissScanIds[3], current.ScanId);
        Assert.Equal(0, current.TotalCorruptedChunks);
        Assert.Empty(current.CorruptionCounts);

        var history = await service.GetHistoryAsync();
        var repeatedMissHistory = history
            .Where(item => item.DetectionMethod == CorruptionDetectionMethod.RepeatedMiss)
            .ToList();
        Assert.Equal(
            [repeatedMissScanIds[2], repeatedMissScanIds[1], repeatedMissScanIds[3]],
            repeatedMissHistory.Select(item => item.ScanId));
        Assert.Equal(
            repeatedMissScanIds[3],
            Assert.Single(repeatedMissHistory, item => item.IsCurrent).ScanId);
        Assert.Equal(0, repeatedMissHistory[2].TotalCorruptedChunks);
        Assert.Empty(repeatedMissHistory[2].CorruptionCounts);
        Assert.Contains(history, item =>
            item.ScanId == structuralScanId
            && item.IsCurrent
            && item.DetectionMethod == CorruptionDetectionMethod.Structural);

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(3, await context.CachedCorruptionScans.CountAsync(scan =>
            scan.DetectionMode == CorruptionDetectionMode.RepeatedMiss));
        Assert.Equal(1, await context.CachedCorruptionScans.CountAsync(scan =>
            scan.DetectionMode == CorruptionDetectionMode.Structural));
        Assert.False(await context.CachedCorruptionScans.AnyAsync(scan =>
            scan.ScanId == repeatedMissScanIds[0]));
        Assert.False(await context.CachedCorruptionDetections.AnyAsync(row =>
            row.ScanId == repeatedMissScanIds[0]));
        Assert.Equal(1, await context.CachedCorruptionScans.CountAsync(scan =>
            scan.DetectionMode == CorruptionDetectionMode.RepeatedMiss && scan.IsCurrent));
        Assert.Equal(1, await context.CachedCorruptionScans.CountAsync(scan =>
            scan.DetectionMode == CorruptionDetectionMode.Structural && scan.IsCurrent));
        Assert.Single(await context.CachedCorruptionDetections
            .Where(row => row.ScanId == repeatedMissScanIds[3])
            .ToListAsync());
    }

    [Fact]
    public async Task FilteredUniqueIndex_RejectsTwoCurrentRowsForOneMethodAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        await PersistScanAsync(
            service,
            Guid.Parse("00000000-0000-0000-0000-000000000201"),
            CorruptionDetectionMethod.RepeatedMiss,
            sequence: 201,
            ScanStartedUtc.AddSeconds(1));

        await using var context = database.Factory.CreateDbContext();
        context.CachedCorruptionScans.Add(new CachedCorruptionScan
        {
            ScanId = Guid.Parse("00000000-0000-0000-0000-000000000202"),
            DetectionMode = CorruptionDetectionMode.RepeatedMiss,
            IsCurrent = true,
            Threshold = 3,
            LookbackDays = LookbackDays,
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Status = OperationStatus.Completed.ToWireString(),
            StartedAtUtc = ScanStartedUtc,
            CompletedAtUtc = ScanStartedUtc.AddSeconds(2),
            CreatedAtUtc = ScanStartedUtc.AddSeconds(2)
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task RetentionFailure_RollsBackDemotionInsertionTrimAndOtherMethodAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var repeatedMissScanIds = Enumerable.Range(1, 4)
            .Select(sequence => Guid.Parse($"00000000-0000-0000-0001-{sequence:D12}"))
            .ToList();
        var structuralScanId = Guid.Parse("00000000-0000-0000-0002-000000000001");
        for (var index = 0; index < 3; index++)
        {
            await PersistScanAsync(
                service,
                repeatedMissScanIds[index],
                CorruptionDetectionMethod.RepeatedMiss,
                sequence: 300 + index,
                ScanStartedUtc.AddSeconds(index + 1));
        }
        await PersistScanAsync(
            service,
            structuralScanId,
            CorruptionDetectionMethod.Structural,
            sequence: 400,
            ScanStartedUtc.AddSeconds(1));

        await using (var triggerContext = database.Factory.CreateDbContext())
        {
            await triggerContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER fail_corruption_scan_trim
                BEFORE DELETE ON "CachedCorruptionScans"
                BEGIN
                    SELECT RAISE(ABORT, 'forced retention failure');
                END;
                """);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => PersistScanAsync(
            service,
            repeatedMissScanIds[3],
            CorruptionDetectionMethod.RepeatedMiss,
            sequence: 303,
            ScanStartedUtc.AddSeconds(4)));

        await using var context = database.Factory.CreateDbContext();
        var repeatedMissScans = await context.CachedCorruptionScans
            .AsNoTracking()
            .Where(scan => scan.DetectionMode == CorruptionDetectionMode.RepeatedMiss)
            .ToListAsync();
        Assert.Equal(3, repeatedMissScans.Count);
        Assert.DoesNotContain(repeatedMissScans, scan => scan.ScanId == repeatedMissScanIds[3]);
        Assert.Equal(
            repeatedMissScanIds[2],
            Assert.Single(repeatedMissScans, scan => scan.IsCurrent).ScanId);
        Assert.True(await context.CachedCorruptionDetections.AnyAsync(row =>
            row.ScanId == repeatedMissScanIds[0]));
        Assert.Equal(
            structuralScanId,
            (await context.CachedCorruptionScans.SingleAsync(scan =>
                scan.DetectionMode == CorruptionDetectionMode.Structural && scan.IsCurrent)).ScanId);
    }

    [Fact]
    public async Task HistoryDeletion_IsExactAndCurrentDeletionNeverPromotesHistoricalEvidenceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var historicalToDelete = Guid.Parse("00000000-0000-0000-0003-000000000001");
        var historicalToKeep = Guid.Parse("00000000-0000-0000-0003-000000000002");
        var currentScanId = Guid.Parse("00000000-0000-0000-0003-000000000003");
        var structuralScanId = Guid.Parse("00000000-0000-0000-0004-000000000001");
        await PersistScanAsync(
            service,
            historicalToDelete,
            CorruptionDetectionMethod.RepeatedMiss,
            sequence: 501,
            ScanStartedUtc.AddSeconds(1));
        await PersistScanAsync(
            service,
            historicalToKeep,
            CorruptionDetectionMethod.RepeatedMiss,
            sequence: 502,
            ScanStartedUtc.AddSeconds(2));
        await PersistScanAsync(
            service,
            currentScanId,
            CorruptionDetectionMethod.RepeatedMiss,
            sequence: 503,
            ScanStartedUtc.AddSeconds(3));
        await PersistScanAsync(
            service,
            structuralScanId,
            CorruptionDetectionMethod.Structural,
            sequence: 504,
            ScanStartedUtc.AddSeconds(4));

        var historyBeforeDelete = await service.GetHistoryAsync();
        var historicalSummary = Assert.Single(historyBeforeDelete, item =>
            item.ScanId == historicalToKeep);
        Assert.False(historicalSummary.IsCurrent);
        Assert.Equal(1, historicalSummary.TotalServicesWithCorruption);
        Assert.Equal(1, historicalSummary.TotalCorruptedChunks);
        Assert.Equal(1, historicalSummary.CorruptionCounts["steam"]);
        Assert.Equal(3, historicalSummary.Settings.Threshold);
        Assert.Equal(LookbackDays, historicalSummary.Settings.LookbackDays);
        Assert.Equal(
            "default:scan-502",
            Assert.Single(await service.GetSnapshotDetailsAsync(historicalToKeep, "steam")).CandidateId);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetCurrentDetectionByScanIdAsync(historicalToKeep));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetDetailsAsync(historicalToKeep, "steam"));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetRemovalSelectionAsync(historicalToKeep, "steam"));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.ApplyRemovalSuccessAsync(historicalToKeep, ["default:scan-502"]));

        await service.DeleteSnapshotAsync(historicalToDelete);
        await using (var afterHistoricalDelete = database.Factory.CreateDbContext())
        {
            Assert.False(await afterHistoricalDelete.CachedCorruptionScans.AnyAsync(scan =>
                scan.ScanId == historicalToDelete));
            Assert.False(await afterHistoricalDelete.CachedCorruptionDetections.AnyAsync(row =>
                row.ScanId == historicalToDelete));
            Assert.True(await afterHistoricalDelete.CachedCorruptionScans.AnyAsync(scan =>
                scan.ScanId == currentScanId && scan.IsCurrent));
            Assert.True(await afterHistoricalDelete.CachedCorruptionScans.AnyAsync(scan =>
                scan.ScanId == structuralScanId && scan.IsCurrent));
        }

        await service.DeleteSnapshotAsync(currentScanId);
        Assert.Null(await service.GetDetectionAsync(CorruptionDetectionMethod.RepeatedMiss));
        Assert.Equal(
            structuralScanId,
            Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
                CorruptionDetectionMethod.Structural)).ScanId);
        var historyAfterCurrentDelete = await service.GetHistoryAsync();
        Assert.Contains(historyAfterCurrentDelete, item =>
            item.ScanId == historicalToKeep && !item.IsCurrent);
        Assert.Equal(
            "default:scan-502",
            Assert.Single(await service.GetSnapshotDetailsAsync(historicalToKeep, "steam")).CandidateId);
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetRemovalSelectionAsync(historicalToKeep, "steam"));
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.ApplyRemovalSuccessAsync(historicalToKeep, ["default:scan-502"]));
    }

    [Fact]
    public async Task DeleteSnapshotFailure_RollsBackChildAndHeaderDeletionAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.Parse("00000000-0000-0000-0005-000000000001");
        await PersistScanAsync(
            service,
            scanId,
            CorruptionDetectionMethod.RepeatedMiss,
            sequence: 601,
            ScanStartedUtc.AddSeconds(1));

        await using (var triggerContext = database.Factory.CreateDbContext())
        {
            await triggerContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER fail_corruption_scan_delete
                BEFORE DELETE ON "CachedCorruptionScans"
                BEGIN
                    SELECT RAISE(ABORT, 'forced snapshot delete failure');
                END;
                """);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => service.DeleteSnapshotAsync(scanId));

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.CachedCorruptionScans.AnyAsync(scan =>
            scan.ScanId == scanId && scan.IsCurrent));
        Assert.Equal(2, await context.CachedCorruptionDetections.CountAsync(row =>
            row.ScanId == scanId));
        Assert.Equal(
            scanId,
            Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
                CorruptionDetectionMethod.RepeatedMiss)).ScanId);
    }

    [Fact]
    public async Task LegacyStructuralMode_RemainsUnknownAndUnsupportedSnapshotsStayHiddenAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var structuralScanId = Guid.Parse("00000000-0000-0000-0006-000000000001");
        await PersistScanAsync(
            service,
            structuralScanId,
            CorruptionDetectionMethod.Structural,
            sequence: 701,
            ScanStartedUtc.AddSeconds(1),
            StructuralScanMode.Full);
        await using (var context = database.Factory.CreateDbContext())
        {
            await context.CachedCorruptionScans
                .Where(scan => scan.ScanId == structuralScanId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(scan => scan.ScanMode, (StructuralScanMode?)null));
        }
        var unsupportedScanId = await SeedV3ScanAsync(
            database.Factory,
            "{ deliberately invalid candidate JSON }");

        var current = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync(
            CorruptionDetectionMethod.Structural));
        Assert.Equal(structuralScanId, current.ScanId);
        Assert.Null(current.ScanMode);
        var history = await service.GetHistoryAsync();
        Assert.Null(Assert.Single(history, item => item.ScanId == structuralScanId).ScanMode);
        Assert.DoesNotContain(history, item => item.ScanId == unsupportedScanId);
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetSnapshotDetailsAsync(
            unsupportedScanId,
            "steam"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteSnapshotAsync(unsupportedScanId));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteSnapshotAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task EvidenceDemotion_HidesCurrentDetectionsAndKeepsHistoryAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            await PersistScanAsync(
                service,
                Guid.Parse($"00000000-0000-0000-0007-{sequence:D12}"),
                CorruptionDetectionMethod.RepeatedMiss,
                sequence: 800 + sequence,
                ScanStartedUtc.AddSeconds(sequence));
            await PersistScanAsync(
                service,
                Guid.Parse($"00000000-0000-0000-0008-{sequence:D12}"),
                CorruptionDetectionMethod.Structural,
                sequence: 900 + sequence,
                ScanStartedUtc.AddSeconds(sequence));
        }

        await using (var demoteContext = database.Factory.CreateDbContext())
        {
            Assert.Equal(
                2,
                await DatabaseService.DemoteCachedCorruptionEvidenceAsync(
                    demoteContext,
                    CancellationToken.None));
        }

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(4, await context.CachedCorruptionScans.CountAsync());
        Assert.Equal(0, await context.CachedCorruptionScans.CountAsync(scan => scan.IsCurrent));
        Assert.True(await context.CachedCorruptionDetections.AnyAsync());

        Assert.Null(await service.GetDetectionAsync(CorruptionDetectionMethod.RepeatedMiss));
        Assert.Null(await service.GetDetectionAsync(CorruptionDetectionMethod.Structural));

        var history = await service.GetHistoryAsync();
        Assert.Equal(4, history.Count);
        Assert.All(history, item => Assert.False(item.IsCurrent));
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
            Coverage: CorruptionScanCoverageResponse.From(StructuralCoverage()),
            ScanMode: "incremental",
            EffectiveScanMode: "baseline",
            BaselineStatus: "ready",
            StateCommitted: true,
            Resumed: true,
            FilesDiscovered: 4,
            FilesProcessed: 4,
            InvalidFiles: 1,
            ScanSummary: new StructuralScanStatusResponse
            {
                ScanMode = "incremental",
                EffectiveScanMode = "baseline",
                BaselineStatus = "ready",
                StateCommitted = true,
                Resumed = true,
                FilesInspected = 4
            });
        var completionJson = JsonSerializer.Serialize(
            completion,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"detectionMethod\":\"structural\"", completionJson);
        Assert.Contains("\"detectionCounts\"", completionJson);
        Assert.Contains("\"filesSeen\"", completionJson);
        Assert.Contains("\"scanMode\":\"incremental\"", completionJson);
        Assert.Contains("\"scanSummary\"", completionJson);
        Assert.Contains("\"filesDiscovered\":4", completionJson);

        var repeatedMissJson = JsonSerializer.SerializeToElement(
            new SignalRNotifications.CorruptionDetectionComplete(
                Success: true,
                OperationId: Guid.NewGuid(),
                StageKey: "signalr.corruptionDetect.completeRepeatedMiss",
                DetectionMethod: "repeated_miss"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Null, repeatedMissJson.GetProperty("scanMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, repeatedMissJson.GetProperty("stateCommitted").ValueKind);
        Assert.Equal(JsonValueKind.Null, repeatedMissJson.GetProperty("scanSummary").ValueKind);
    }

    [Fact]
    public void PersistenceModelAndMigration_AreExplicitCurrentAndTruthfullyNullable()
    {
        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.CachedCorruptionScans)));
        Assert.NotNull(typeof(AppDbContext).GetProperty(nameof(AppDbContext.CachedCorruptionDetections)));
        Assert.NotNull(typeof(CachedCorruptionScan).GetProperty(nameof(CachedCorruptionScan.DetectionMode)));
        Assert.NotNull(typeof(CachedCorruptionScan).GetProperty(nameof(CachedCorruptionScan.IsCurrent)));
        Assert.NotNull(typeof(CachedCorruptionScan).GetProperty(nameof(CachedCorruptionScan.ScanMode)));
        Assert.Null(typeof(CachedCorruptionScan).GetProperty("Coverage"));

        var migration = new TestableCorruptionScanHistoryMigration();
        var operations = migration.BuildUpOperations();
        var isCurrent = Assert.Single(operations.OfType<AddColumnOperation>(),
            column => column.Name == "IsCurrent");
        Assert.False(isCurrent.IsNullable);
        Assert.Equal(false, isCurrent.DefaultValue);
        var scanMode = Assert.Single(operations.OfType<AddColumnOperation>(),
            column => column.Name == "ScanMode");
        Assert.True(scanMode.IsNullable);
        Assert.Null(scanMode.DefaultValue);

        var backfill = Assert.Single(operations.OfType<SqlOperation>()).Sql;
        Assert.Contains("PARTITION BY \"DetectionMode\"", backfill, StringComparison.Ordinal);
        Assert.Contains(
            "ORDER BY \"CompletedAtUtc\" DESC, \"ScanId\" DESC",
            backfill,
            StringComparison.Ordinal);
        Assert.Contains("\"ContractVersion\" = 4", backfill, StringComparison.Ordinal);
        Assert.Contains("\"Status\" = 'completed'", backfill, StringComparison.Ordinal);
        Assert.DoesNotContain("ScanMode\" =", backfill, StringComparison.Ordinal);

        var index = Assert.Single(operations.OfType<CreateIndexOperation>(),
            operation => operation.Name == "IX_CachedCorruptionScans_Current_DetectionMode");
        Assert.True(index.IsUnique);
        Assert.Equal("\"IsCurrent\"", index.Filter);
        Assert.Equal(["DetectionMode"], index.Columns);

        var downOperations = migration.BuildDownOperations();
        Assert.Contains(downOperations.OfType<DropIndexOperation>(),
            operation => operation.Name == index.Name);
        Assert.Contains(downOperations.OfType<DropColumnOperation>(),
            operation => operation.Name == "IsCurrent");
        Assert.Contains(downOperations.OfType<DropColumnOperation>(),
            operation => operation.Name == "ScanMode");

        var snapshotModel = new AppDbContextModelSnapshot().Model;
        var snapshotEntity = snapshotModel.FindEntityType(typeof(CachedCorruptionScan));
        Assert.NotNull(snapshotEntity);
        Assert.NotNull(snapshotEntity.FindProperty(nameof(CachedCorruptionScan.IsCurrent)));
        Assert.True(snapshotEntity.FindProperty(nameof(CachedCorruptionScan.ScanMode))!.IsNullable);
        var snapshotIndex = Assert.Single(snapshotEntity.GetIndexes(), modelIndex =>
            modelIndex.GetDatabaseName() == index.Name);
        Assert.True(snapshotIndex.IsUnique);
        Assert.Equal("\"IsCurrent\"", snapshotIndex.GetFilter());
    }

    private static void Validate(
        DatasourceCorruptionReport report,
        CorruptionDetectionMethod method,
        string datasource,
        StructuralScanMode? scanMode = null) =>
        CorruptionDetectionService.ValidateAndAttachDatasource(
            report.Report,
            datasource,
            3,
            LookbackDays,
            method,
            method == CorruptionDetectionMethod.Structural
                ? scanMode ?? StructuralScanMode.Full
                : null,
            ScanStartedWire);

    private static async Task PersistScanAsync(
        CorruptionDetectionService service,
        Guid scanId,
        CorruptionDetectionMethod method,
        int sequence,
        DateTime completedAtUtc,
        StructuralScanMode? scanMode = null,
        bool empty = false)
    {
        var report = method == CorruptionDetectionMethod.RepeatedMiss
            ? RepeatedMissReport(
                "default",
                empty ? [] : [RepeatedMissCandidate($"scan-{sequence}")])
            : StructuralReport(
                "default",
                empty ? [] : [StructuralCandidate($"scan-{sequence}", sequence)]);
        Validate(report, method, "default", scanMode);
        await service.PersistCompletedScanAsync(
            scanId,
            3,
            LookbackDays,
            method,
            ScanStartedUtc,
            completedAtUtc,
            [report],
            method == CorruptionDetectionMethod.Structural
                ? scanMode ?? StructuralScanMode.Full
                : null);
    }

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
            operationTracker: null!,
            capabilityService: null!);

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

    private sealed class TestableCorruptionScanHistoryMigration : AddCorruptionScanHistory
    {
        public IReadOnlyList<MigrationOperation> BuildUpOperations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Up(builder);
            return builder.Operations;
        }

        public IReadOnlyList<MigrationOperation> BuildDownOperations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Down(builder);
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
