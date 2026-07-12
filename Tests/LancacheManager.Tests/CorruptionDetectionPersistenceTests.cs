using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Data.Migrations;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Hubs;
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
            CorruptionDetectionService.ValidateScanInput("random", 3, LookbackDays));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput("cache_and_logs", 4, LookbackDays));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput("cache_and_logs", 3, 0));
        Assert.Throws<ValidationException>(() =>
            CorruptionDetectionService.ValidateScanInput("cache_and_logs", 3, 366));
        Assert.Equal(
            CorruptionDetectionMode.CacheAndLogs,
            CorruptionDetectionService.ValidateScanInput("cache_and_logs", 3, 1));
        Assert.Equal(
            CorruptionDetectionMode.CacheAndLogs,
            CorruptionDetectionService.ValidateScanInput("miss_count", 5, 365));
    }

    [Fact]
    public void CorruptionReportV2_RequiresSplitFieldsEvenWhenEmpty()
    {
        const string incompleteJson =
            """
            {
              "contract_version": 2,
              "mode": "cache_and_logs",
              "threshold": 3,
              "lookback_days": 30,
              "scan_started_utc": "2026-07-11T00:00:00Z",
              "service_counts": {},
              "total": 0,
              "candidates": []
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CorruptionReport>(incompleteJson));
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
        Assert.All(roundTrip.Observations, observation => Assert.True(observation.BytesServed > 0));
        Assert.Null(roundTrip.SupportingSibling);
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
              "raw_range": "bytes=26380013-32837297",
              "bytes_served": 6457285
            }
            """;

        var observation = Assert.IsType<CandidateObservation>(
            JsonSerializer.Deserialize<CandidateObservation>(legacyJson));

        Assert.Equal(string.Empty, observation.RawUrl);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(observation));
        Assert.Equal(string.Empty, serialized.RootElement.GetProperty("raw_url").GetString());
    }

    [Fact]
    public void ReportValidation_EnforcesModeAndMissingSliceProofMatrix()
    {
        var logsCandidate = Candidate("review", CorruptionDetectionMode.LogsOnly, 3);
        var logsReport = Report("default", logsCandidate).Report;
        CorruptionDetectionService.ValidateAndAttachDatasource(
            logsReport,
            "default",
            CorruptionDetectionMode.LogsOnly,
            3,
            LookbackDays,
            ScanStartedWire);
        Assert.False(logsReport.Candidates.Single().RemovalAllowed);
        Assert.Equal("default:review", logsReport.Candidates.Single().CandidateId);

        var validMissingReport = Report("default", MissingCandidate("missing")).Report;
        CorruptionDetectionService.ValidateAndAttachDatasource(
            validMissingReport,
            "default",
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedWire);
        var validMissing = Assert.Single(validMissingReport.Candidates);
        Assert.Equal("missing_cached_slice", validMissing.Reason);
        Assert.False(validMissing.RemovalAllowed);
        Assert.NotNull(validMissing.SupportingSibling);

        AssertInvalid(candidate => candidate.RemovalAllowed = true);
        AssertInvalid(candidate => candidate.ValidationState = "exact_path_present");
        AssertInvalid(candidate => candidate.ExactPaths = []);
        AssertInvalid(candidate => candidate.SupportingSibling = null);
        AssertInvalid(candidate => candidate.SupportingSibling!.CacheSlice = candidate.CacheSlice);
        AssertInvalid(candidate => candidate.EvidenceCount = 2);
        AssertInvalid(candidate => candidate.Observations[0].Method = "POST");
        AssertInvalid(candidate => candidate.Observations[0].HttpStatus = 200);
        AssertInvalid(candidate => candidate.Observations[0].CacheStatus = "MISS");
        AssertInvalid(candidate => candidate.Observations[0].BytesServed = 1);
        AssertInvalid(candidate => candidate.Observations[0].Timestamp = "2026-06-10T23:59:59Z");

        void AssertInvalid(Action<CorruptionCandidate> mutate)
        {
            var candidate = MissingCandidate("invalid");
            mutate(candidate);
            var report = Report("default", candidate).Report;
            Assert.Throws<InvalidDataException>(() =>
                CorruptionDetectionService.ValidateAndAttachDatasource(
                    report,
                    "default",
                    CorruptionDetectionMode.CacheAndLogs,
                    3,
                    LookbackDays,
                    ScanStartedWire));
        }
    }

    [Fact]
    public void ReportValidation_RejectsIndependentCountProjectionMismatch()
    {
        AssertMismatch(report => report.ServiceCounts["steam"] = 2);
        AssertMismatch(report =>
        {
            report.ServiceCounts.Clear();
            report.ServiceCounts["STEAM"] = 1;
        });
        AssertMismatch(report => report.RemovableServiceCounts.Clear());
        AssertMismatch(report => report.ReviewOnlyServiceCounts["steam"] = 1);
        AssertMismatch(report => report.Total = 2);
        AssertMismatch(report => report.RemovableTotal = 2);
        AssertMismatch(report => report.ReviewOnlyTotal = 1);
        AssertMismatch(report => report.LookbackDays = 90);
        AssertMismatch(report => report.ScanStartedUtc = "2026-07-11T00:00:01Z");

        void AssertMismatch(Action<CorruptionReport> mutate)
        {
            var report = Report("default", Candidate("candidate")).Report;
            mutate(report);
            Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                report,
                "default",
                CorruptionDetectionMode.CacheAndLogs,
                3,
                LookbackDays,
                ScanStartedWire));
        }
    }

    [Fact]
    public void ReportValidation_RejectsDuplicatePhysicalIdentityButAllowsDifferentSlices()
    {
        var firstClient = Candidate(
            "client-a",
            CorruptionDetectionMode.Redownload);
        var secondClient = Candidate(
            "client-b",
            CorruptionDetectionMode.Redownload);
        secondClient.RetryClient = "192.0.2.11";
        secondClient.Observations[0].ClientIp = "192.0.2.11";
        var forgedReport = Report("default", firstClient, secondClient).Report;

        Assert.Equal(2, forgedReport.Total);
        Assert.Equal(2, forgedReport.RemovableTotal);
        var exception = Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.ValidateAndAttachDatasource(
                forgedReport,
                "default",
                CorruptionDetectionMode.Redownload,
                3,
                LookbackDays,
                ScanStartedWire));
        Assert.Contains("duplicate immutable physical candidate identity", exception.Message);

        var firstSlice = Candidate(
            "slice-a",
            CorruptionDetectionMode.Redownload);
        var secondSlice = Candidate(
            "slice-b",
            CorruptionDetectionMode.Redownload);
        secondSlice.RetryClient = "192.0.2.11";
        secondSlice.Observations[0].ClientIp = "192.0.2.11";
        secondSlice.CacheSlice = new CacheSliceIdentity
        {
            Kind = "ranged",
            Start = 2_097_152,
            End = 3_145_727
        };
        var validReport = Report("default", firstSlice, secondSlice).Report;

        CorruptionDetectionService.ValidateAndAttachDatasource(
            validReport,
            "default",
            CorruptionDetectionMode.Redownload,
            3,
            LookbackDays,
            ScanStartedWire);

        Assert.Equal(2, validReport.Candidates.Count);
        Assert.Equal(2, validReport.RemovableTotal);
        Assert.Equal(2, validReport.RemovableServiceCounts["steam"]);
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
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(10),
            [Report("default", Candidate("default:first"))]);

        var emptyScan = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            emptyScan,
            CorruptionDetectionMode.LogsOnly,
            10,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(20),
            []);

        var result = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(emptyScan, result.ScanId);
        Assert.Equal(CorruptionDetectionMode.LogsOnly, result.DetectionMode);
        Assert.Equal(10, result.Threshold);
        Assert.Equal(LookbackDays, result.LookbackDays);
        Assert.Empty(result.CorruptionCounts);
        Assert.Empty(result.RemovableServiceCounts);
        Assert.Empty(result.ReviewOnlyServiceCounts);
        Assert.False(result.RemovalAllowed);

        await using var assertContext = database.Factory.CreateDbContext();
        Assert.Equal(1, await assertContext.CachedCorruptionScans.CountAsync());
        Assert.Equal(0, await assertContext.CachedCorruptionDetections.CountAsync());
        var persistedHeader = await assertContext.CachedCorruptionScans.SingleAsync();
        Assert.Equal(LookbackDays, persistedHeader.LookbackDays);
        Assert.Equal(ScanStartedUtc, persistedHeader.StartedAtUtc);
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
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", Candidate("default:original"))]);

        var invalidReport = Report("secondary", Candidate("secondary:new"));
        invalidReport.Report.LookbackDays = 90;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PersistCompletedScanAsync(
            Guid.NewGuid(),
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(2),
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
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
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
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
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
        var selectedCandidate = Candidate("default:removable");
        selectedCandidate.Observations = NormalizationEquivalentObservations("MISS");
        var reviewCandidate = MissingCandidate("default:review");
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", selectedCandidate, reviewCandidate)]);

        var mixed = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(2, mixed.CorruptionCounts["steam"]);
        Assert.Equal(1, mixed.RemovableServiceCounts["steam"]);
        Assert.Equal(1, mixed.ReviewOnlyServiceCounts["steam"]);
        Assert.Equal(2, mixed.TotalCorruptedChunks);
        Assert.Equal(1, mixed.RemovableTotal);
        Assert.Equal(1, mixed.ReviewOnlyTotal);
        Assert.True(mixed.RemovalAllowed);

        var selection = await service.GetRemovalSelectionAsync(
            scanId,
            "steam",
            ["default:removable"]);
        Assert.Equal(["default:removable"], selection.CandidateIds);
        Assert.Equal(
            ["/depot/chunk?token=alpha", "/depot/chunk?token=beta"],
            Assert.Single(selection.CandidatesByDatasource["default"])
                .Observations
                .Select(observation => observation.RawUrl));

        var unfiltered = await service.GetRemovalSelectionAsync(scanId, "steam");
        Assert.Equal(["default:removable"], unfiltered.CandidateIds);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam", ["default:review"]));

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.GetRemovalSelectionAsync(scanId, "steam", ["default:handcrafted"]));
    }

    [Fact]
    public async Task RemovalEvidence_IsPrunedOnlyAfterRecordedSuccessAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var removableCandidate = Candidate("default:removable");
        var reviewCandidate = MissingCandidate("default:review");
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", removableCandidate, reviewCandidate)]);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.ApplyRemovalSuccessAsync(scanId, ["default:missing"]));
        Assert.Equal(2, (await service.GetDetectionAsync())!.TotalCorruptedChunks);

        await service.ApplyRemovalSuccessAsync(scanId, ["default:removable"]);
        var remaining = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(scanId, remaining.ScanId);
        Assert.Equal(1, remaining.TotalCorruptedChunks);
        Assert.Equal(0, remaining.RemovableTotal);
        Assert.Equal(1, remaining.ReviewOnlyTotal);
        Assert.Empty(remaining.RemovableServiceCounts);
        Assert.Equal(1, remaining.ReviewOnlyServiceCounts["steam"]);
        Assert.False(remaining.RemovalAllowed);
        var reviewDetail = Assert.Single(await service.GetDetailsAsync(scanId, "steam"));
        Assert.Equal("default:review", reviewDetail.CandidateId);
        Assert.Equal("missing_cached_slice", reviewDetail.Reason);
        Assert.Equal(1_024, reviewDetail.Observations.Single().BytesServed);
        Assert.Equal(
            "C:/cache/default_review_sibling",
            Assert.IsType<SupportingSiblingEvidence>(reviewDetail.SupportingSibling).ExactPath);

        await using var assertContext = database.Factory.CreateDbContext();
        Assert.Equal(1, await assertContext.CachedCorruptionScans.CountAsync());
    }

    [Fact]
    public async Task HistoricalEvidenceSelection_IsCurrentServerOwnedAndReviewOnlyAcrossScopesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var removable = Candidate("default:steam-removable");
        removable.Observations = NormalizationEquivalentObservations("MISS");
        var steamReview = MissingCandidate("default:steam-review");
        var epicReview = MissingCandidate("secondary:epic-review");
        epicReview.Service = "epicgames";
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", removable, steamReview), Report("secondary", epicReview)]);

        var steam = await service.GetHistoricalEvidencePurgeSelectionAsync(scanId, "steam");
        Assert.Equal("steam", steam.Scope);
        Assert.Equal(["default:steam-review"], steam.CandidateIds);
        Assert.DoesNotContain("default:steam-removable", steam.CandidateIds);

        var all = await service.GetHistoricalEvidencePurgeSelectionAsync(scanId);
        Assert.Equal("all", all.Scope);
        Assert.Equal(
            ["default:steam-review", "secondary:epic-review"],
            all.CandidateIds.OrderBy(id => id));
        Assert.Equal(["default", "secondary"], all.CandidatesByDatasource.Keys.OrderBy(name => name));

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.GetHistoricalEvidencePurgeSelectionAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetHistoricalEvidencePurgeSelectionAsync(scanId, "unknown"));
    }

    [Fact]
    public async Task HistoricalEvidenceSuccess_PrunesExactReviewIdsAndKeepsHeaderAndUnrelatedEvidenceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var removable = Candidate("default:removable");
        var purged = MissingCandidate("default:purged");
        var retained = MissingCandidate("default:retained");
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", removable, purged, retained)]);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.ApplyHistoricalEvidencePurgeSuccessAsync(scanId, ["default:missing"]));
        Assert.Equal(3, (await service.GetDetectionAsync())!.TotalCorruptedChunks);

        await service.ApplyHistoricalEvidencePurgeSuccessAsync(scanId, ["default:purged"]);
        var remaining = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(scanId, remaining.ScanId);
        Assert.Equal(2, remaining.TotalCorruptedChunks);
        Assert.Equal(1, remaining.RemovableTotal);
        Assert.Equal(1, remaining.ReviewOnlyTotal);
        Assert.Equal(
            ["default:removable", "default:retained"],
            (await service.GetDetailsAsync(scanId, "steam")).Select(candidate => candidate.CandidateId));

        await service.ApplyHistoricalEvidencePurgeSuccessAsync(scanId, ["default:retained"]);
        await service.ApplyRemovalSuccessAsync(scanId, ["default:removable"]);
        Assert.Equal(0, (await service.GetDetectionAsync())!.TotalCorruptedChunks);
        await using var assertContext = database.Factory.CreateDbContext();
        Assert.Equal(1, await assertContext.CachedCorruptionScans.CountAsync());
        Assert.Equal(0, await assertContext.CachedCorruptionDetections.CountAsync());
    }

    [Fact]
    public async Task GlobalOrphanPrune_DoesNotSelectDownloadsBackedByReviewObservationRowsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var ended = DateTime.UtcNow.AddHours(-1);
        var backed = new Download
        {
            Service = "steam",
            ClientIp = "192.0.2.10",
            StartTimeUtc = ended.AddMinutes(-1),
            StartTimeLocal = ended.AddMinutes(-1),
            EndTimeUtc = ended,
            EndTimeLocal = ended,
            IsActive = false,
            IsEvicted = false
        };
        var orphan = new Download
        {
            Service = "steam",
            ClientIp = "192.0.2.11",
            StartTimeUtc = ended.AddMinutes(-1),
            StartTimeLocal = ended.AddMinutes(-1),
            EndTimeUtc = ended,
            EndTimeLocal = ended,
            IsActive = false,
            IsEvicted = false
        };
        context.Downloads.AddRange(backed, orphan);
        await context.SaveChangesAsync();
        context.LogEntries.Add(new LogEntryRecord
        {
            DownloadId = backed.Id,
            Download = backed,
            Timestamp = ended,
            ClientIp = backed.ClientIp,
            Service = backed.Service,
            Method = "GET",
            Url = "/depot/chunk",
            StatusCode = 206,
            CacheStatus = "HIT",
            BytesServed = 1_024,
            HttpRange = "bytes=1048576-1049599"
        });
        await context.SaveChangesAsync();

        // Mirrors the deliberately unchanged CacheReconciliationService predicate. A review
        // observation is still a backing LogEntries row, so this setting cannot purge it.
        var pruneCutoffUtc = DateTime.UtcNow.AddMinutes(-5);
        var selectedIds = await context.Downloads
            .Where(download => !download.IsActive
                && !download.IsEvicted
                && download.EndTimeUtc < pruneCutoffUtc
                && !context.LogEntries.Any(entry => entry.DownloadId == download.Id))
            .Select(download => download.Id)
            .ToListAsync();

        Assert.DoesNotContain(backed.Id, selectedIds);
        Assert.Contains(orphan.Id, selectedIds);
    }

    [Fact]
    public async Task DismissReviewOnly_PerServiceRetainsRemovableAndOtherServiceEvidenceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var removable = Candidate("default:steam-removable");
        removable.Observations = NormalizationEquivalentObservations("MISS");
        var steamReview = MissingCandidate("default:steam-review");
        var epicReview = MissingCandidate("secondary:epic-review");
        epicReview.Service = "epic";
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", removable, steamReview), Report("secondary", epicReview)]);

        string epicJsonBefore;
        string removableBefore;
        await using (var beforeContext = database.Factory.CreateDbContext())
        {
            epicJsonBefore = (await beforeContext.CachedCorruptionDetections
                .SingleAsync(row => row.ServiceName == "epic")).CandidatesJson;
            removableBefore = CorruptionDetectionService.SerializeCandidates(
                [Assert.Single(CorruptionDetectionService.DeserializeCandidates(
                    await beforeContext.CachedCorruptionDetections
                        .SingleAsync(row => row.ServiceName == "steam")),
                    candidate => candidate.RemovalAllowed)]);
        }

        var dismissal = await service.DismissReviewOnlyFindingsAsync(scanId, "steam");

        Assert.Equal(1, dismissal.DismissedCount);
        Assert.Equal(2, dismissal.Detection.TotalCorruptedChunks);
        Assert.Equal(1, dismissal.Detection.RemovableTotal);
        Assert.Equal(1, dismissal.Detection.ReviewOnlyTotal);
        Assert.Equal(1, dismissal.Detection.RemovableServiceCounts["steam"]);
        Assert.Equal(1, dismissal.Detection.ReviewOnlyServiceCounts["epic"]);
        var remainingSteam = Assert.Single(await service.GetDetailsAsync(scanId, "steam"));
        Assert.True(remainingSteam.RemovalAllowed);
        Assert.Equal(removableBefore, CorruptionDetectionService.SerializeCandidates([remainingSteam]));

        await using var assertContext = database.Factory.CreateDbContext();
        Assert.Equal(
            epicJsonBefore,
            (await assertContext.CachedCorruptionDetections
                .SingleAsync(row => row.ServiceName == "epic")).CandidatesJson);
        var steamRow = await assertContext.CachedCorruptionDetections
            .SingleAsync(row => row.ServiceName == "steam");
        Assert.Equal(1, steamRow.CorruptedChunkCount);
        Assert.True(steamRow.RemovalAllowed);
    }

    [Fact]
    public async Task DismissReviewOnly_AllServicesRemovesOnlyReviewCandidatesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var removable = Candidate("default:removable");
        var steamReview = MissingCandidate("default:steam-review");
        var epicReview = MissingCandidate("secondary:epic-review");
        epicReview.Service = "epic";
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", removable, steamReview), Report("secondary", epicReview)]);

        var dismissal = await service.DismissReviewOnlyFindingsAsync(scanId);

        Assert.Equal(2, dismissal.DismissedCount);
        Assert.Equal(scanId, dismissal.Detection.ScanId);
        Assert.Equal(1, dismissal.Detection.TotalCorruptedChunks);
        Assert.Equal(1, dismissal.Detection.RemovableTotal);
        Assert.Equal(0, dismissal.Detection.ReviewOnlyTotal);
        Assert.Empty(dismissal.Detection.ReviewOnlyServiceCounts);
        Assert.Equal(["default:removable"],
            (await service.GetDetailsAsync(scanId, "steam")).Select(candidate => candidate.CandidateId));
        Assert.Empty(await service.GetDetailsAsync(scanId, "epic"));
    }

    [Fact]
    public async Task DismissReviewOnly_LastCandidatesRetainsZeroResultScanHeaderAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        var completedAt = ScanStartedUtc.AddSeconds(7);
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            completedAt,
            [Report("default", MissingCandidate("default:review"))]);

        var dismissal = await service.DismissReviewOnlyFindingsAsync(scanId);

        Assert.Equal(1, dismissal.DismissedCount);
        Assert.Equal(scanId, dismissal.Detection.ScanId);
        Assert.Equal(CorruptionDetectionMode.CacheAndLogs, dismissal.Detection.DetectionMode);
        Assert.Equal(3, dismissal.Detection.Threshold);
        Assert.Equal(LookbackDays, dismissal.Detection.LookbackDays);
        Assert.Equal(completedAt, dismissal.Detection.LastDetectionTime);
        Assert.Equal(0, dismissal.Detection.TotalCorruptedChunks);
        Assert.Empty(dismissal.Detection.CorruptionCounts);

        await using var assertContext = database.Factory.CreateDbContext();
        Assert.Equal(1, await assertContext.CachedCorruptionScans.CountAsync());
        Assert.Equal(0, await assertContext.CachedCorruptionDetections.CountAsync());
        Assert.Equal(scanId, (await assertContext.CachedCorruptionScans.SingleAsync()).ScanId);
        var cached = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.True(cached.HasCachedResults);
        Assert.Equal(scanId, cached.ScanId);
        Assert.Equal(0, cached.TotalCorruptedChunks);
    }

    [Fact]
    public async Task DismissReviewOnly_IsIdempotentForCurrentScopeAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", Candidate("default:removable"), MissingCandidate("default:review"))]);

        Assert.Equal(1, (await service.DismissReviewOnlyFindingsAsync(scanId, "steam")).DismissedCount);
        var repeated = await service.DismissReviewOnlyFindingsAsync(scanId, "steam");

        Assert.Equal(0, repeated.DismissedCount);
        Assert.Equal(1, repeated.Detection.TotalCorruptedChunks);
        Assert.Equal(1, repeated.Detection.RemovableTotal);
        Assert.Equal(0, repeated.Detection.ReviewOnlyTotal);
        Assert.Equal(["default:removable"],
            (await service.GetDetailsAsync(scanId, "steam")).Select(candidate => candidate.CandidateId));
    }

    [Fact]
    public async Task DismissReviewOnly_RejectsUnknownAndStaleScansWithoutChangingCurrentAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.DismissReviewOnlyFindingsAsync(Guid.NewGuid()));

        var staleScanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            staleScanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", MissingCandidate("default:stale-review"))]);
        var currentScanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            currentScanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(2),
            [Report("default", MissingCandidate("default:current-review"))]);

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.DismissReviewOnlyFindingsAsync(staleScanId));
        var current = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(currentScanId, current.ScanId);
        Assert.Equal(1, current.ReviewOnlyTotal);
        Assert.Equal("default:current-review",
            Assert.Single(await service.GetDetailsAsync(currentScanId, "steam")).CandidateId);
    }

    [Fact]
    public async Task DismissReviewOnly_LaterScanCanRediscoverDismissedEvidenceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var firstScanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            firstScanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", MissingCandidate("default:review"))]);
        await service.DismissReviewOnlyFindingsAsync(firstScanId);

        var rediscoveredScanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            rediscoveredScanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(2),
            [Report("default", MissingCandidate("default:review"))]);

        var rediscovered = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(rediscoveredScanId, rediscovered.ScanId);
        Assert.Equal(1, rediscovered.ReviewOnlyTotal);
        Assert.Equal("default:review",
            Assert.Single(await service.GetDetailsAsync(rediscoveredScanId, "steam")).CandidateId);
    }

    [Fact]
    public async Task DismissReviewOnly_MultiRowFailureRollsBackEveryCandidateAndHeaderAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [
                Report("default", MissingCandidate("default:review")),
                Report("secondary", MissingCandidate("secondary:review"))
            ]);
        await using (var setup = database.Factory.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER "FailSecondaryDismiss"
                BEFORE DELETE ON "CachedCorruptionDetections"
                WHEN OLD."DatasourceName" = 'secondary'
                BEGIN
                    SELECT RAISE(ABORT, 'forced dismissal rollback');
                END;
                """);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.DismissReviewOnlyFindingsAsync(scanId));

        await using var assertContext = database.Factory.CreateDbContext();
        Assert.Equal(1, await assertContext.CachedCorruptionScans.CountAsync());
        Assert.Equal(2, await assertContext.CachedCorruptionDetections.CountAsync());
        var cached = Assert.IsType<CachedCorruptionResult>(await service.GetDetectionAsync());
        Assert.Equal(2, cached.ReviewOnlyTotal);
    }

    [Fact]
    public async Task CandidateJsonConcurrencyTokenRejectsLostRowUpdateAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", Candidate("default:removable"), MissingCandidate("default:review"))]);

        await using var firstContext = database.Factory.CreateDbContext();
        await using var secondContext = database.Factory.CreateDbContext();
        var firstRow = await firstContext.CachedCorruptionDetections.SingleAsync();
        var secondRow = await secondContext.CachedCorruptionDetections.SingleAsync();
        firstRow.CandidatesJson += " ";
        secondRow.CandidatesJson += "\r\n";

        await firstContext.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task CandidatePruning_TranslatesConcurrencyFailuresWithoutChangingEvidenceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var seedService = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await seedService.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", Candidate("default:removable"), MissingCandidate("default:review"))]);
        var conflictingService = NewService(new ThrowingConcurrencyDbContextFactory(database.Factory.Options));

        var dismissalConflict = await Assert.ThrowsAsync<ConflictException>(() =>
            conflictingService.DismissReviewOnlyFindingsAsync(scanId));
        Assert.Equal("The corruption scan changed. Reload results and try again", dismissalConflict.Message);
        var removalConflict = await Assert.ThrowsAsync<ConflictException>(() =>
            conflictingService.ApplyRemovalSuccessAsync(scanId, ["default:removable"]));
        Assert.Equal("The corruption scan changed. Reload results and try again", removalConflict.Message);

        var unchanged = Assert.IsType<CachedCorruptionResult>(await seedService.GetDetectionAsync());
        Assert.Equal(2, unchanged.TotalCorruptedChunks);
        Assert.Equal(1, unchanged.RemovableTotal);
        Assert.Equal(1, unchanged.ReviewOnlyTotal);
    }

    [Fact]
    public async Task DismissReviewController_ReturnsTypedTransactionProjectionAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Factory);
        var scanId = Guid.NewGuid();
        await service.PersistCompletedScanAsync(
            scanId,
            CorruptionDetectionMode.CacheAndLogs,
            3,
            LookbackDays,
            ScanStartedUtc,
            ScanStartedUtc.AddSeconds(1),
            [Report("default", MissingCandidate("default:review"))]);
        var controller = NewController(service);

        var action = await controller.DismissServiceCorruptionReviewFindingsAsync(
            "steam",
            CancellationToken.None,
            scanId);

        var response = Assert.IsType<DismissCorruptionReviewResponse>(Assert.IsType<OkObjectResult>(action).Value);
        Assert.Equal(1, response.DismissedCount);
        Assert.True(response.Result.HasCachedResults);
        Assert.Equal(scanId, response.Result.ScanId);
        Assert.Equal(0, response.Result.TotalCorruptedChunks);
        Assert.Equal(0, response.Result.ReviewOnlyTotal);
    }

    [Fact]
    public async Task DismissReviewController_RejectsInvalidServiceAndEmptyScanIdAsync()
    {
        var controller = NewController(NewService(factory: null!));

        await Assert.ThrowsAsync<ValidationException>(() =>
            controller.DismissServiceCorruptionReviewFindingsAsync(
                "../steam",
                CancellationToken.None,
                Guid.NewGuid()));
        await Assert.ThrowsAsync<ValidationException>(() =>
            controller.DismissAllCorruptionReviewFindingsAsync(
                CancellationToken.None,
                Guid.Empty));
    }

    [Fact]
    public async Task PersistenceModel_KeepsEvidenceIdentityAndOnlyLookbackMetadataAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        var logEntry = context.Model.FindEntityType(typeof(LogEntryRecord));
        Assert.NotNull(logEntry?.FindProperty(nameof(LogEntryRecord.Method)));
        Assert.True(logEntry?.FindProperty(nameof(LogEntryRecord.HttpRange))?.IsNullable);

        var scan = context.Model.FindEntityType(typeof(CachedCorruptionScan));
        Assert.False(scan?.FindProperty(nameof(CachedCorruptionScan.LookbackDays))?.IsNullable);

        var candidate = context.Model.FindEntityType(typeof(CachedCorruptionDetection));
        Assert.True(candidate?.FindProperty(nameof(CachedCorruptionDetection.CandidatesJson))?.IsConcurrencyToken);
        Assert.Null(candidate?.FindProperty("RemovableTotal"));
        Assert.Null(candidate?.FindProperty("ReviewOnlyTotal"));
        Assert.Null(candidate?.FindProperty("RemovableServiceCounts"));
        Assert.Null(candidate?.FindProperty("ReviewOnlyServiceCounts"));
    }

    [Fact]
    public void V2Migration_InvalidatesCandidatesThenHeadersBeforeAddingLookback()
    {
        var operations = new TestableV2LookbackMigration().BuildUpOperations();

        var deleteCandidates = Assert.IsType<SqlOperation>(operations[0]);
        Assert.Equal("DELETE FROM \"CachedCorruptionDetections\";", deleteCandidates.Sql);
        var deleteHeaders = Assert.IsType<SqlOperation>(operations[1]);
        Assert.Equal("DELETE FROM \"CachedCorruptionScans\";", deleteHeaders.Sql);
        var addLookback = Assert.IsType<AddColumnOperation>(operations[2]);
        Assert.Equal("LookbackDays", addLookback.Name);
        Assert.Equal("CachedCorruptionScans", addLookback.Table);
        Assert.False(addLookback.IsNullable);
        Assert.Equal(LookbackDays, addLookback.DefaultValue);
    }

    [Fact]
    public void DetailAndRemovalEndpoints_AreBoundOnlyToStoredScanIdentity()
    {
        var scanParameters = typeof(CacheController)
            .GetMethod(nameof(CacheController.StartCorruptionDetectionAsync))!
            .GetParameters()
            .ToDictionary(parameter => parameter.Name!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(LookbackDays, scanParameters["lookbackDays"].DefaultValue);

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

        AssertDismissEndpoint(
            nameof(CacheController.DismissServiceCorruptionReviewFindingsAsync),
            "services/{service}/corruption/review-findings",
            expectedParameters: ["service", "cancellationToken", "scanId"]);
        AssertDismissEndpoint(
            nameof(CacheController.DismissAllCorruptionReviewFindingsAsync),
            "corruption/review-findings",
            expectedParameters: ["cancellationToken", "scanId"]);
        AssertDismissEndpoint(
            nameof(CacheController.PurgeServiceHistoricalEvidenceAsync),
            "evicted/services/{service}/historical-evidence",
            expectedParameters: ["service", "cancellationToken", "scanId"]);
        AssertDismissEndpoint(
            nameof(CacheController.PurgeAllHistoricalEvidenceAsync),
            "evicted/historical-evidence",
            expectedParameters: ["cancellationToken", "scanId"]);
    }

    [Fact]
    public void CachedAndSignalRContracts_ExposeSplitCountsWithStableCamelCaseNames()
    {
        var cached = new CachedCorruptionResponse
        {
            HasCachedResults = true,
            ScanId = Guid.NewGuid(),
            DetectionMode = CorruptionDetectionMode.CacheAndLogs,
            Threshold = 3,
            LookbackDays = LookbackDays,
            ContractVersion = CorruptionReport.SupportedContractVersion,
            CorruptionCounts = new Dictionary<string, long> { ["steam"] = 2 },
            RemovableServiceCounts = new Dictionary<string, long> { ["steam"] = 1 },
            ReviewOnlyServiceCounts = new Dictionary<string, long> { ["steam"] = 1 },
            TotalCorruptedChunks = 2,
            RemovableTotal = 1,
            ReviewOnlyTotal = 1
        };
        using var cachedJson = JsonDocument.Parse(JsonSerializer.Serialize(
            cached,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(LookbackDays, cachedJson.RootElement.GetProperty("lookbackDays").GetInt32());
        Assert.Equal(1, cachedJson.RootElement.GetProperty("removableTotal").GetInt64());
        Assert.Equal(1, cachedJson.RootElement.GetProperty("reviewOnlyTotal").GetInt64());
        Assert.Equal(
            1,
            cachedJson.RootElement
                .GetProperty("removableServiceCounts")
                .GetProperty("steam")
                .GetInt64());

        var completion = new SignalRNotifications.CorruptionDetectionComplete(
            Success: true,
            OperationId: Guid.NewGuid(),
            StageKey: "signalr.corruptionDetect.complete",
            TotalServicesWithCorruption: 1,
            TotalCorruptedChunks: 2,
            RemovableServiceCounts: new Dictionary<string, long> { ["steam"] = 1 },
            ReviewOnlyServiceCounts: new Dictionary<string, long> { ["steam"] = 1 },
            RemovableTotal: 1,
            ReviewOnlyTotal: 1);
        using var completionJson = JsonDocument.Parse(JsonSerializer.Serialize(
            completion,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(1, completionJson.RootElement.GetProperty("removableTotal").GetInt64());
        Assert.Equal(1, completionJson.RootElement.GetProperty("reviewOnlyTotal").GetInt64());

        var dismissal = new DismissCorruptionReviewResponse
        {
            DismissedCount = 1,
            Result = cached
        };
        using var dismissalJson = JsonDocument.Parse(JsonSerializer.Serialize(
            dismissal,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(1, dismissalJson.RootElement.GetProperty("dismissedCount").GetInt64());
        Assert.Equal(
            cached.ScanId,
            dismissalJson.RootElement.GetProperty("result").GetProperty("scanId").GetGuid());

        Assert.Equal(
            "\"historicalEvidencePurge\"",
            JsonSerializer.Serialize(
                OperationType.HistoricalEvidencePurge,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("HistoricalEvidencePurgeStarted", SignalREvents.HistoricalEvidencePurgeStarted);
        Assert.Equal("HistoricalEvidencePurgeProgress", SignalREvents.HistoricalEvidencePurgeProgress);
        Assert.Equal("HistoricalEvidencePurgeComplete", SignalREvents.HistoricalEvidencePurgeComplete);

        var purgeComplete = new SignalRNotifications.HistoricalEvidencePurgeComplete(
            OperationId: Guid.NewGuid(),
            Success: false,
            Status: OperationStatus.Cancelled,
            Scope: "steam",
            StageKey: "signalr.historicalEvidencePurge.cancelled",
            Cancelled: true,
            CandidateCount: 2);
        using var purgeJson = JsonDocument.Parse(JsonSerializer.Serialize(
            purgeComplete,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("steam", purgeJson.RootElement.GetProperty("scope").GetString());
        Assert.Equal("cancelled", purgeJson.RootElement.GetProperty("status").GetString());
        Assert.True(purgeJson.RootElement.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public void HistoricalEvidenceRustHelper_HasNoCachePathParameter()
    {
        var parameters = typeof(RustProcessHelper)
            .GetMethod(nameof(RustProcessHelper.RunHistoricalEvidencePurgeAsync))!
            .GetParameters()
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("logsPath", parameters);
        Assert.Contains("scope", parameters);
        Assert.Contains("evidenceFile", parameters);
        Assert.Contains("progressFile", parameters);
        Assert.DoesNotContain("cachePath", parameters);
    }

    [Fact]
    public void HistoricalEvidencePermissionGate_RequiresLogsButNotCacheWritability()
    {
        var controller = NewController(NewService(factory: null!));
        var gate = typeof(CacheController).GetMethod(
            "CheckHistoricalEvidenceLogsWritable",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cacheReadonly = new List<ResolvedDatasource>
        {
            new()
            {
                Name = "default",
                CachePath = "C:/cache",
                LogPath = "C:/logs",
                CacheWritable = false,
                LogsWritable = true
            }
        };

        Assert.Null(gate.Invoke(controller, [cacheReadonly]));
        cacheReadonly[0].LogsWritable = false;
        Assert.Contains(
            "logs directory is read-only",
            Assert.IsType<string>(gate.Invoke(controller, [cacheReadonly])));
    }

    private static void AssertDismissEndpoint(
        string methodName,
        string route,
        IReadOnlyCollection<string> expectedParameters)
    {
        var method = typeof(CacheController).GetMethod(methodName)!;
        Assert.Equal(route, Assert.Single(method.GetCustomAttributes(typeof(HttpDeleteAttribute), false)
            .Cast<HttpDeleteAttribute>()).Template);
        Assert.Equal("AdminOnly", Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), false)
            .Cast<AuthorizeAttribute>()).Policy);
        Assert.Equal(
            expectedParameters.OrderBy(name => name),
            method.GetParameters().Select(parameter => parameter.Name!).OrderBy(name => name));
        Assert.DoesNotContain(method.GetParameters(), parameter =>
            parameter.Name is "candidateIds" or "paths" or "observations" or "removalAllowed" or "candidatesJson");
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

    private static CacheController NewController(CorruptionDetectionService service) =>
        new(
            cacheService: null!,
            cacheClearingService: null!,
            corruptionDetectionService: service,
            logger: NullLogger<CacheController>.Instance,
            pathResolver: null!,
            notifications: null!,
            rustProcessHelper: null!,
            nginxLogRotationService: null!,
            operationTracker: null!,
            datasourceService: null!,
            dbContextFactory: null!,
            reconciliationService: null!,
            conflictChecker: null!,
            operationQueue: null!);

    private static DatasourceCorruptionReport Report(
        string datasource,
        params CorruptionCandidate[] candidates)
    {
        var mode = candidates.FirstOrDefault()?.Mode ?? CorruptionDetectionMode.CacheAndLogs;
        var threshold = candidates.FirstOrDefault()?.Threshold ?? 3;
        var all = ProjectCounts(candidates);
        var removable = ProjectCounts(candidates.Where(candidate => candidate.RemovalAllowed));
        var reviewOnly = ProjectCounts(candidates.Where(candidate => !candidate.RemovalAllowed));
        return new DatasourceCorruptionReport(datasource, new CorruptionReport
        {
            ContractVersion = CorruptionReport.SupportedContractVersion,
            Mode = mode,
            Threshold = threshold,
            LookbackDays = LookbackDays,
            ScanStartedUtc = ScanStartedWire,
            ServiceCounts = all,
            RemovableServiceCounts = removable,
            ReviewOnlyServiceCounts = reviewOnly,
            Candidates = candidates.ToList(),
            Total = candidates.Length,
            RemovableTotal = removable.Values.Sum(),
            ReviewOnlyTotal = reviewOnly.Values.Sum()
        });
    }

    private static Dictionary<string, long> ProjectCounts(IEnumerable<CorruptionCandidate> candidates) =>
        candidates
            .GroupBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.LongCount(),
                StringComparer.OrdinalIgnoreCase);

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
            ExactPaths = mode == CorruptionDetectionMode.LogsOnly
                ? []
                : [$"C:/cache/{id.Replace(':', '_')}"] ,
            EvidenceCount = threshold,
            FirstSeen = "2026-07-11T00:00:00Z",
            LastSeen = "2026-07-11T00:00:10Z",
            RetryClient = mode == CorruptionDetectionMode.Redownload ? "192.0.2.10" : null,
            Reason = mode == CorruptionDetectionMode.Redownload
                ? "same_client_hit_retry_burst"
                : "repeated_miss_burst",
            ValidationState = mode == CorruptionDetectionMode.LogsOnly
                ? "log_suspect"
                : "exact_path_present",
            RemovalAllowed = mode != CorruptionDetectionMode.LogsOnly,
            SupportingSibling = null,
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
                    RawRange = "bytes=26380013-32837297",
                    BytesServed = 6_457_285
                }
            ]
        };

    private static CorruptionCandidate MissingCandidate(string id) =>
        new()
        {
            CandidateId = id,
            Datasource = id.Split(':')[0],
            Mode = CorruptionDetectionMode.CacheAndLogs,
            Threshold = 3,
            Service = "steam",
            RawUrl = "/depot/chunk",
            NormalizedUri = "/depot/chunk",
            ObservedRange = new ObservedByteRange
            {
                Kind = "inclusive",
                Start = 1_048_576,
                End = 1_049_599
            },
            CacheSlice = new CacheSliceIdentity
            {
                Kind = "ranged",
                Start = 1_048_576,
                End = 2_097_151
            },
            ExactPaths = [$"C:/cache/{id.Replace(':', '_')}_missing"],
            EvidenceCount = 1,
            FirstSeen = "2026-07-10T23:59:59Z",
            LastSeen = "2026-07-10T23:59:59Z",
            Reason = "missing_cached_slice",
            ValidationState = "exact_path_missing",
            RemovalAllowed = false,
            SupportingSibling = new SupportingSiblingEvidence
            {
                CacheSlice = new CacheSliceIdentity
                {
                    Kind = "ranged",
                    Start = 2_097_152,
                    End = 3_145_727
                },
                ExactPath = $"C:/cache/{id.Replace(':', '_')}_sibling"
            },
            Observations =
            [
                new CandidateObservation
                {
                    RawUrl = "/depot/chunk",
                    Timestamp = "2026-07-10T23:59:59Z",
                    ClientIp = "192.0.2.10",
                    Method = "GET",
                    HttpStatus = 206,
                    CacheStatus = "HIT",
                    RawRange = "bytes=1048576-1049599",
                    BytesServed = 1_024
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
            RawRange = "bytes=26380013-32837297",
            BytesServed = 6_457_285
        },
        new CandidateObservation
        {
            RawUrl = "/depot/chunk?token=beta",
            Timestamp = "2026-07-11T00:00:10Z",
            ClientIp = "192.0.2.10",
            Method = "GET",
            HttpStatus = 206,
            CacheStatus = cacheStatus,
            RawRange = "bytes=26380013-32837297",
            BytesServed = 6_457_285
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
        public DbContextOptions<AppDbContext> Options => options;

        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ThrowingConcurrencyDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new ThrowingConcurrencyDbContext(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ThrowingConcurrencyDbContext(DbContextOptions<AppDbContext> options)
        : AppDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException();
    }
}
