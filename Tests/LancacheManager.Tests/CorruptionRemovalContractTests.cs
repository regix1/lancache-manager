using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LancacheManager.Tests;

public sealed class CorruptionRemovalContractTests
{
    [Fact]
    public void StructuralSelection_DoesNotRequireLogMutation()
    {
        var selection = Selection(
            CorruptionDetectionMethod.Structural,
            new StructuralCorruptionEvidence
            {
                Issues = [StructuralCorruptionIssue.EmptyCacheFile],
                CacheKeyEncoding = "hex",
                CacheKey = string.Empty,
                CacheKeyMd5 = "d41d8cd98f00b204e9800998ecf8427e",
                CacheVersion = 5,
                FileLength = 0,
                Fingerprint = new StructuralFileFingerprint(),
                DetectedAtUtc = "2026-07-12T00:00:00Z"
            });

        Assert.True(selection.HasStructuralEvidence);
        Assert.False(selection.HasRepeatedMissEvidence);
        Assert.False(CacheController.RequiresLogMutation([selection]));
        Assert.False(CacheController.RequiresLogMutation([selection], "default"));
        Assert.True(CacheController.HasRequiredWritePermissions(
            new ResolvedDatasource { Name = "default", CacheWritable = true, LogsWritable = false },
            [selection]));
        Assert.False(CacheController.HasRequiredWritePermissions(
            new ResolvedDatasource { Name = "default", CacheWritable = false, LogsWritable = true },
            [selection]));
    }

    [Fact]
    public void RepeatedMissSelection_RequiresLogMutationOnlyForItsDatasource()
    {
        var selection = Selection(
            CorruptionDetectionMethod.RepeatedMiss,
            new RepeatedMissCorruptionEvidence());

        Assert.True(selection.HasRepeatedMissEvidence);
        Assert.True(CacheController.RequiresLogMutation([selection]));
        Assert.True(CacheController.RequiresLogMutation([selection], "default"));
        Assert.False(CacheController.RequiresLogMutation([selection], "secondary"));
        Assert.False(CacheController.HasRequiredWritePermissions(
            new ResolvedDatasource { Name = "default", CacheWritable = true, LogsWritable = false },
            [selection]));
    }

    [Fact]
    public void RustStructuralRemovalCommand_HasNoLogOrServiceArguments()
    {
        var arguments = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-structural",
            "C:/logs-secret",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json");

        Assert.Equal(
            "remove-structural \"C:/cache\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress",
            arguments);
        Assert.DoesNotContain("logs-secret", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("steam", arguments, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-structural", "", "", null, "evidence", "progress"));
    }

    [Fact]
    public void StructuralRemovalEvidence_OmitsThresholdAndKeepsServerDatasource()
    {
        var selection = Selection(
            CorruptionDetectionMethod.Structural,
            new StructuralCorruptionEvidence());
        var stored = selection.CandidatesByDatasource["default"].Single();
        var envelope = new CorruptionRemovalEvidence
        {
            ContractVersion = 4,
            DetectionMethod = CorruptionDetectionMethod.Structural,
            ScanId = selection.ScanId,
            Threshold = null,
            Datasource = "default",
            Candidates =
            [
                new CorruptionCandidate
                {
                    CandidateId = stored.CandidateId,
                    Datasource = stored.Datasource,
                    Service = stored.Service,
                    ExactPaths = stored.ExactPaths,
                    Evidence = stored.Evidence
                }
            ]
        };

        var json = JsonSerializer.Serialize(envelope);
        Assert.Contains("\"detection_method\":\"structural\"", json);
        Assert.Contains("\"datasource\":\"default\"", json);
        Assert.Contains("\"exact_paths\":[\"C:/cache/exact\"]", json);
        Assert.DoesNotContain("\"threshold\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiDatasourceProgress_IsMonotonicAndRejectsMalformedRustValues()
    {
        var values = new[]
        {
            CorruptionDetectionService.CalculateOverallProgress(0, 2, 0),
            CorruptionDetectionService.CalculateOverallProgress(0, 2, 100),
            CorruptionDetectionService.CalculateOverallProgress(1, 2, 0),
            CorruptionDetectionService.CalculateOverallProgress(1, 2, 100)
        };
        Assert.Equal([0, 50, 50, 100], values);
        Assert.True(values.SequenceEqual(values.Order()));
        Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.CalculateOverallProgress(0, 1, 101));
        Assert.Throws<InvalidDataException>(() =>
            CorruptionDetectionService.CalculateOverallProgress(0, 0, 0));
    }

    [Fact]
    public void StructuralRemovalCompletion_StrictlyValidatesRustOutcome()
    {
        var valid = new Dictionary<string, object?>
        {
            ["detectionMethod"] = "structural",
            ["count"] = 3,
            ["files"] = 1,
            ["alreadyMissing"] = 1,
            ["healed"] = 1,
            ["bytesFreed"] = 512L
        };
        var outcome = CacheController.ValidateStructuralRemovalCompletion(valid, 3);
        Assert.Equal(1, outcome.Files);
        Assert.Equal(512, outcome.BytesFreed);

        var unknownField = new Dictionary<string, object?>(valid) { ["logLines"] = 1 };
        Assert.Throws<InvalidDataException>(() =>
            CacheController.ValidateStructuralRemovalCompletion(unknownField, 3));
        var wrongTotal = new Dictionary<string, object?>(valid) { ["count"] = 4 };
        Assert.Throws<InvalidDataException>(() =>
            CacheController.ValidateStructuralRemovalCompletion(wrongTotal, 3));
        var stringlyTyped = new Dictionary<string, object?>(valid) { ["files"] = "1" };
        Assert.Throws<InvalidDataException>(() =>
            CacheController.ValidateStructuralRemovalCompletion(stringlyTyped, 3));
    }

    [Fact]
    public void ExistingRemovalSignalREvents_AreMethodAwareWithoutNewEventNames()
    {
        var payload = new SignalRNotifications.CorruptionRemovalComplete(
            Success: true,
            Service: "steam",
            StageKey: "signalr.corruptionRemove.complete",
            DetectionMethod: "structural");
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"detectionMethod\":\"structural\"", json);
        Assert.Null(typeof(SignalRNotifications).GetNestedType("StructuralCorruptionRemovalComplete"));
    }

    [Fact]
    public void CorruptionCurrentAndHistoryRoutes_AreMethodAwareAndDedicated()
    {
        var cachedMethod = typeof(CacheController)
            .GetMethod(nameof(CacheController.GetCachedCorruptionAsync))!;
        Assert.Equal(
            ["detectionMethod", "cancellationToken"],
            cachedMethod.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(
            "corruption/cached",
            cachedMethod.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
                .Cast<HttpGetAttribute>()
                .Single()
                .Template);

        Assert.Equal(
            "corruption/history",
            RouteTemplate<HttpGetAttribute>(nameof(CacheController.GetCorruptionHistoryAsync)));
        Assert.Equal(
            "corruption/history/{scanId:guid}/services/{service}",
            RouteTemplate<HttpGetAttribute>(nameof(CacheController.GetCorruptionHistoryDetailsAsync)));
        Assert.Equal(
            "corruption/history/{scanId:guid}",
            RouteTemplate<HttpDeleteAttribute>(nameof(CacheController.DeleteCorruptionHistoryAsync)));
    }

    [Fact]
    public void CorruptionHistoryWireContract_IsClosedAndTruthfulAboutScanMode()
    {
        var entry = new CorruptionScanHistoryEntryResponse
        {
            ScanId = Guid.NewGuid(),
            ContractVersion = CorruptionReport.SupportedContractVersion,
            DetectionMethod = "structural",
            ScanMode = null,
            IsCurrent = false,
            CompletedAtUtc = "2026-07-12T00:01:00.0000000Z",
            Settings = new CorruptionScanSettingsResponse
            {
                MinStableAgeSeconds = 60,
                MaxPrefixBytes = 4096
            },
            CorruptionCounts = new Dictionary<string, long> { ["steam"] = 2 },
            DetectionCounts = new Dictionary<string, long> { ["structural"] = 2 },
            Coverage = new CorruptionScanCoverageResponse { FilesChecked = 2 },
            TotalServicesWithCorruption = 1,
            TotalCorruptedChunks = 2
        };

        var json = JsonSerializer.SerializeToElement(
            new CorruptionScanHistoryResponse { Scans = [entry] },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var scan = json.GetProperty("scans").EnumerateArray().Single();

        Assert.Equal("structural", scan.GetProperty("detectionMethod").GetString());
        Assert.Equal(JsonValueKind.Null, scan.GetProperty("scanMode").ValueKind);
        Assert.False(scan.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(2, scan.GetProperty("corruptionCounts").GetProperty("steam").GetInt64());
        Assert.False(scan.TryGetProperty("removalAllowed", out _));
        Assert.False(scan.TryGetProperty("isReadOnly", out _));
    }

    [Fact]
    public void CachedCorruptionWireContract_IncludesPersistedStructuralScanMode()
    {
        var json = JsonSerializer.SerializeToElement(
            new CachedCorruptionResponse
            {
                HasCachedResults = true,
                ScanId = Guid.NewGuid(),
                DetectionMethod = "structural",
                ScanMode = "incremental"
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("structural", json.GetProperty("detectionMethod").GetString());
        Assert.Equal("incremental", json.GetProperty("scanMode").GetString());
    }

    [Fact]
    public void CorruptionRemovalMetadata_CarriesTheExactScanIdentity()
    {
        var scanId = Guid.NewGuid();
        var metadata = new RemovalMetrics
        {
            EntityKey = "steam",
            EntityName = "steam",
            DetectionMethod = CorruptionDetectionMethod.Structural,
            CorruptionScanId = scanId
        };

        Assert.Equal(scanId, metadata.CorruptionScanId);
        Assert.Equal(CorruptionDetectionMethod.Structural, metadata.DetectionMethod);
    }

    [Fact]
    public void SnapshotDeletionGuard_MatchesOnlyTheExactCorruptionRemovalScan()
    {
        var activeScanId = Guid.NewGuid();
        var otherScanId = Guid.NewGuid();
        var operations = new[]
        {
            Operation(
                OperationType.CorruptionRemoval,
                new RemovalMetrics
                {
                    EntityKey = "steam",
                    CorruptionScanId = activeScanId
                }),
            Operation(
                OperationType.CorruptionRemoval,
                new RemovalMetrics
                {
                    EntityKey = "wsus",
                    CorruptionScanId = otherScanId
                }),
            Operation(
                OperationType.ServiceRemoval,
                new RemovalMetrics
                {
                    EntityKey = "steam",
                    CorruptionScanId = activeScanId
                })
        };

        Assert.True(CacheController.HasActiveCorruptionRemovalForScan(operations, activeScanId));
        Assert.True(CacheController.HasActiveCorruptionRemovalForScan(operations, otherScanId));
        Assert.False(CacheController.HasActiveCorruptionRemovalForScan(operations, Guid.NewGuid()));
        Assert.False(CacheController.HasActiveCorruptionRemovalForScan(
            [Operation(OperationType.CorruptionRemoval, new RemovalMetrics { EntityKey = "steam" })],
            activeScanId));
    }

    [Fact]
    public async Task CorruptionRemovalRegistration_FinalRevalidationPrecedesRegistrationUnderSameGate()
    {
        using var mutationGate = new SemaphoreSlim(1, 1);
        var expectedOperationId = Guid.NewGuid();
        var calls = new List<string>();

        var operationId = await CacheController.RevalidateAndRegisterCorruptionRemovalAsync(
            mutationGate,
            () =>
            {
                Assert.False(mutationGate.Wait(0));
                calls.Add("revalidate");
                return Task.CompletedTask;
            },
            () =>
            {
                Assert.False(mutationGate.Wait(0));
                calls.Add("register");
                return expectedOperationId;
            });

        Assert.Equal(expectedOperationId, operationId);
        Assert.Equal(["revalidate", "register"], calls);
        Assert.True(mutationGate.Wait(0));
        mutationGate.Release();
    }

    private static CorruptionRemovalSelection Selection(
        CorruptionDetectionMethod method,
        CorruptionEvidence evidence)
    {
        var candidate = new CorruptionCandidate
        {
            CandidateId = "default:candidate",
            Datasource = "default",
            Service = "steam",
            ExactPaths = ["C:/cache/exact"],
            Evidence = evidence
        };
        return new CorruptionRemovalSelection
        {
            ScanId = Guid.NewGuid(),
            DetectionMethod = method,
            ContractVersion = 4,
            Threshold = 3,
            Service = "steam",
            CandidatesByDatasource = new Dictionary<string, IReadOnlyList<CorruptionCandidate>>
            {
                ["default"] = [candidate]
            }
        };
    }

    private static OperationInfo Operation(OperationType type, RemovalMetrics metadata) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Name = "Removal",
        Status = OperationStatus.Running,
        Metadata = metadata
    };

    private static string? RouteTemplate<TAttribute>(string methodName)
        where TAttribute : HttpMethodAttribute =>
        typeof(CacheController)
            .GetMethod(methodName)!
            .GetCustomAttributes(typeof(TAttribute), inherit: true)
            .Cast<TAttribute>()
            .Single()
            .Template;
}
