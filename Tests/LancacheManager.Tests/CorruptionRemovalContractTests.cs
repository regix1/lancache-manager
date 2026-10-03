using System.Text.Json;
using System.Reflection;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using System.Collections.Concurrent;
using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LancacheManager.Tests;

public sealed class CorruptionRemovalContractTests
{
    [Fact]
    public async Task OrphanedRunningPresentationCleanupPreservesRepairOwnedStateAndAcceptedScan()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.Structural);
        var orphanedId = Guid.NewGuid();
        var ownedId = Guid.NewGuid();
        fixture.States.SaveState(orphanedId.ToString(), new LancacheManager.Core.Services.OperationState
        {
            Key = orphanedId.ToString(),
            Type = OperationType.CorruptionDetection.ToWireString(),
            Status = OperationStatus.Running.ToWireString()
        });
        fixture.States.SaveState(ownedId.ToString(), new LancacheManager.Core.Services.OperationState
        {
            Key = ownedId.ToString(),
            Type = OperationType.CorruptionDetection.ToWireString(),
            Status = OperationStatus.Running.ToWireString()
        });
        await fixture.States.PrepareRepairAsync(
            new OperationRepair
            {
                Id = ownedId,
                Type = OperationType.CorruptionRemoval,
                Name = "Corruption removal: steam",
                StartedAt = DateTime.UtcNow,
                Corruption = new CorruptionRepair
                {
                    ScanId = fixture.ScanId,
                    ContractVersion = CorruptionReport.SupportedContractVersion,
                    DetectionMethod = CorruptionDetectionMethod.Structural,
                    Service = "steam"
                },
                Removal = new RemovalRepair
                {
                    EntityKey = "steam",
                    EntityName = "steam",
                    EntityKind = "service",
                    Service = "steam",
                    DetectionMethod = CorruptionDetectionMethod.Structural,
                    CorruptionScanId = fixture.ScanId
                },
                Target = new CacheRepairTarget { Service = "steam" },
                Sources =
                [
                    new OperationRepairSource
                    {
                        Datasource = "default",
                        CacheRoot = fixture.Datasources[0].CachePath,
                        KeyScheme = "monolithic",
                        ApplyCorruptionCandidates = true
                    }
                ]
            },
            CancellationToken.None);

        await fixture.Detection.ClearOrphanedPresentationAsync(CancellationToken.None);

        Assert.Null(fixture.States.GetState(orphanedId.ToString()));
        Assert.NotNull(fixture.States.GetState(ownedId.ToString()));
        Assert.True(fixture.States.OwnsRepair(ownedId));
        Assert.NotEmpty((await fixture.Detection.GetRemovalSelectionAsync(fixture.ScanId, "steam")).CandidateIds);
    }

    [Fact]
    public void ReadContextStemCounts_reads_the_map_a_rust_checkpoint_round_trips()
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            "{\"logLines\":41,\"logLinesBySource\":{\"access.log\":40,\"steam-access.log\":1},\"logLinesBeforePositionBySource\":{\"access.log\":39}}")!;

        var counts = CorruptionDetectionService.ReadContextStemCounts(json, "logLinesBySource");

        Assert.Equal(2, counts.Count);
        Assert.Equal(40, counts["access.log"]);
        Assert.Equal(1, counts["steam-access.log"]);

        var before = CorruptionDetectionService.ReadContextStemCounts(json, "logLinesBeforePositionBySource");
        Assert.Equal(39, before["access.log"]);
    }

    [Fact]
    public void ReadContextStemCounts_is_empty_for_a_missing_or_malformed_map()
    {
        Assert.Empty(CorruptionDetectionService.ReadContextStemCounts(null, "logLinesBySource"));
        Assert.Empty(CorruptionDetectionService.ReadContextStemCounts(
            new Dictionary<string, object?>(), "logLinesBySource"));
        var notAMap = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            "{\"logLinesBySource\":\"41\"}")!;
        Assert.Empty(CorruptionDetectionService.ReadContextStemCounts(notAMap, "logLinesBySource"));
    }

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
        var operationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var arguments = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-structural",
            "C:/logs-secret",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "bare_metal",
            operationId: operationId);

        Assert.Equal(
            "remove-structural \"C:/cache\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress --key-scheme bare_metal --operation-id \"11111111-2222-3333-4444-555555555555\"",
            arguments);
        Assert.DoesNotContain("logs-secret", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("steam", arguments, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-structural", "", "", null, "evidence", "progress", "bare_metal", null, null));
    }

    [Fact]
    public void RustRepeatedMissRemovalCommand_UsesMonolithicKeyScheme()
    {
        var operationId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var arguments = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove",
            "C:/logs",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "monolithic",
            operationId: operationId);

        Assert.Equal(
            "remove \"C:/logs\" \"C:/cache\" \"steam\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress --key-scheme monolithic --operation-id \"11111111-2222-3333-4444-555555555555\"",
            arguments);
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
            ["keyVerificationSkipped"] = 0,
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
        var skippedKeys = new Dictionary<string, object?>(valid) { ["keyVerificationSkipped"] = 1 };
        Assert.Throws<InvalidDataException>(() =>
            CacheController.ValidateStructuralRemovalCompletion(skippedKeys, 3));
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

    [Fact]
    public async Task PreparedRemovalCapturesEachRootReceiptPath()
    {
        using var fixture = new RemovalRun(CorruptionDetectionMethod.Structural, datasourceCount: 2);
        var selection = await fixture.Detection.GetRemovalSelectionAsync(fixture.ScanId, "steam");
        var operationId = Guid.NewGuid();
        var build = typeof(CacheController).GetMethod(
            "BuildCorruptionRepair",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var repair = Assert.IsType<OperationRepair>(build.Invoke(
            fixture.Controller,
            [
                operationId,
                DateTime.UtcNow,
                selection,
                fixture.Datasources,
                CacheController.CreateCorruptionRemovalMetadata(selection)
            ]));

        Assert.Equal("steam", repair.Target?.Service);
        Assert.Equal("service", repair.Removal?.EntityKind);
        Assert.Equal(2, repair.Sources.Count);
        Assert.All(repair.Sources, source => Assert.Equal(
            Path.Combine(source.CacheRoot!, $".lancache-repair-{operationId:N}.json"),
            source.ReceiptPath));
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Completed)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Failed)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Cancelled)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Completed)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Failed)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Cancelled)]
    public async Task Core_AwaitsTheWinningContributionAndRejectsItsLaterFailure(
        CorruptionDetectionMethod method, OperationStatus outcome)
    {
        using var fixture = new RemovalRun(method);
        var selection = await fixture.Detection.GetRemovalSelectionAsync(fixture.ScanId, "steam");
        var bulkType = typeof(CacheController).GetNestedType("BulkCorruptionRemovalState", BindingFlags.NonPublic)!;
        var bulk = Activator.CreateInstance(bulkType)!;
        bulkType.GetProperty("ServiceCount")!.SetValue(bulk, 2);
        bulkType.GetField("ServiceIndex")!.SetValue(bulk, 1);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid operationId = default;
        Action<Guid> registered = id =>
        {
            operationId = id;
            var operation = fixture.Tracker.GetOperation(id)!;
            var emit = operation.OnTerminalEmit!;
            operation.OnTerminalEmit = async terminal =>
            {
                reached.TrySetResult();
                await release.Task;
                await emit(terminal);
            };
            if (outcome == OperationStatus.Cancelled) fixture.Tracker.ForceKillOperation(id);
            fixture.Tracker.CompleteOperation(id, outcome == OperationStatus.Completed,
                error: outcome == OperationStatus.Failed ? "external failure" : null,
                cancelled: outcome == OperationStatus.Cancelled);
        };
        var core = typeof(CacheController).GetMethod("RunCorruptionRemovalCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var run = (Task<bool>)core.Invoke(fixture.Controller, [selection, fixture.Datasources, registered, bulk])!;
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(run.IsCompleted);
        Assert.Equal(0, bulkType.GetField("SucceededServices")!.GetValue(bulk));
        Assert.Equal(0, bulkType.GetField("FailedServices")!.GetValue(bulk));
        release.TrySetResult();
        if (outcome == OperationStatus.Cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        else
            Assert.Equal(outcome == OperationStatus.Completed, await run.WaitAsync(TimeSpan.FromSeconds(10)));

        fixture.Tracker.CompleteOperation(operationId, true);
        Assert.Equal(outcome, fixture.Tracker.GetOperation(operationId)!.Status);
        Assert.Equal(outcome == OperationStatus.Completed ? 1 : 0, bulkType.GetField("SucceededServices")!.GetValue(bulk));
        Assert.Equal(outcome == OperationStatus.Failed ? 1 : 0, bulkType.GetField("FailedServices")!.GetValue(bulk));
        Assert.Equal(outcome == OperationStatus.Cancelled, bulkType.GetField("Cancelled")!.GetValue(bulk));
        Assert.Equal(operationId, bulkType.GetField("LastOperationId")!.GetValue(bulk));
        Assert.Empty(fixture.Messages.Completions);
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Completed)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Failed)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Cancelled)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Completed)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Failed)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Cancelled)]
    public async Task AllServices_EmitsOneCapturedAggregateFromWinningOutcomes(
        CorruptionDetectionMethod method, OperationStatus outcome)
    {
        using var fixture = new RemovalRun(method);
        fixture.Messages.OnStarted = id =>
        {
            if (outcome == OperationStatus.Cancelled) fixture.Tracker.ForceKillOperation(id);
            fixture.Tracker.CompleteOperation(id, outcome == OperationStatus.Completed,
                error: outcome == OperationStatus.Failed ? "external failure" : null,
                cancelled: outcome == OperationStatus.Cancelled);
        };
        await fixture.Controller.RemoveAllCorruptedChunksAsync(CancellationToken.None, fixture.ScanId);
        var aggregate = await fixture.Messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("all", aggregate.Service);
        Assert.Equal(method.ToWireString(), aggregate.DetectionMethod);
        Assert.Equal(outcome == OperationStatus.Completed, aggregate.Success);
        Assert.Equal(outcome == OperationStatus.Cancelled, aggregate.Cancelled);
        Assert.Equal(fixture.Messages.Started.Last(), aggregate.OperationId);
        Assert.Equal(outcome == OperationStatus.Cancelled ? 1 : 2, fixture.Messages.Started.Count);
        Assert.Single(fixture.Messages.Completions);
        var savedContext = JsonSerializer.Serialize(aggregate.Context);
        var next = fixture.Tracker.RegisterOperation(OperationType.CorruptionRemoval, "next", new CancellationTokenSource());
        fixture.Messages.Resume.TrySetResult();
        Assert.Equal(savedContext, JsonSerializer.Serialize(aggregate.Context));
        Assert.NotEqual(next, aggregate.OperationId);
        Assert.False(fixture.Tracker.GetOperation(next)!.Status.IsTerminal());
        fixture.Tracker.CompleteOperation(next, false, cancelled: true);
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    public async Task AllServices_XThatTheCurrentServiceOutlivesStopsTheRemainingServicesAsync(CorruptionDetectionMethod method)
    {
        using var fixture = new RemovalRun(method);
        // X, then the service ends green: its last steps ignore the token (the nginx reopen at the end of a removal).
        fixture.Messages.OnStarted = id =>
        {
            if (fixture.Messages.Started.Count == 1)
            {
                fixture.Tracker.CancelOperation(id);
                fixture.Tracker.CompleteOperation(id, success: true);
            }
        };
        await fixture.Controller.RemoveAllCorruptedChunksAsync(CancellationToken.None, fixture.ScanId);
        var aggregate = await fixture.Messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(aggregate.Cancelled);
        Assert.Single(fixture.Messages.Started);
        fixture.Messages.Resume.TrySetResult();
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    public async Task AllServices_XThatTheCurrentServiceFailsAfterStopsTheRemainingServicesAsync(CorruptionDetectionMethod method)
    {
        using var fixture = new RemovalRun(method);
        // X, then the service ends red (a failed nginx reopen): Remove all stops, and the red card keeps the failures summary.
        fixture.Messages.OnStarted = id =>
        {
            if (fixture.Messages.Started.Count == 1)
            {
                fixture.Tracker.CancelOperation(id);
                fixture.Tracker.CompleteOperation(id, success: false, error: "Could not reopen nginx", cancelled: false);
            }
        };
        await fixture.Controller.RemoveAllCorruptedChunksAsync(CancellationToken.None, fixture.ScanId);
        var aggregate = await fixture.Messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(aggregate.Cancelled);
        Assert.False(aggregate.Success);
        Assert.Equal(method == CorruptionDetectionMethod.Structural
            ? "signalr.corruptionRemove.allCompleteWithFailuresStructural"
            : "signalr.corruptionRemove.allCompleteWithFailures", aggregate.StageKey);
        Assert.Equal(1, aggregate.Context!["failedCount"]);
        Assert.Single(fixture.Messages.Started);
        fixture.Messages.Resume.TrySetResult();
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    public async Task ProcessCompletion_AccumulatesBothDatasourcesAndPublishesAcceptedProgress(CorruptionDetectionMethod method)
    {
        await using var fixture = new RemovalRun(method, transport: true, datasourceCount: 2);
        foreach (var datasource in fixture.Datasources)
        {
            fixture.State.SetLogSourcePositions(datasource.Name, new Dictionary<string, long>
            {
                ["access.log"] = 20,
                ["steam-access.log"] = 7
            });
            fixture.State.SetLogTotalLines(datasource.Name, 30);
        }
        fixture.State.SetLogSourcePositions("untouched", new Dictionary<string, long>
        {
            ["access.log"] = 40,
            ["steam-access.log"] = 9
        });
        fixture.State.SetLogTotalLines("untouched", 70);
        var run = fixture.RunAsync();
        var evidence = await fixture.Pipe!.ConnectAsync();
        Assert.Equal("default", evidence.Datasource);
        Assert.Equal("steam", Assert.Single(evidence.Candidates).Service);
        await fixture.Pipe.SendAsync(Live("first", 25, 3, 20));
        var first = await fixture.Messages.WaitProgressAsync("first");
        Assert.Equal(12.5, first.PercentComplete);
        Assert.Equal("steam", first.Service);
        Assert.Equal(method.ToWireString(), first.DetectionMethod);
        Assert.Equal(3, Assert.IsType<RemovalMetrics>(fixture.Tracker.GetOperation(first.OperationId)!.Metadata).FilesProcessed);
        await CompleteDatasourceAsync(fixture.Pipe, method, second: false);
        evidence = await fixture.Pipe.ConnectAsync();
        Assert.Equal("secondary", evidence.Datasource);
        await fixture.Pipe.SendAsync(Live("second", 40, 8, 30));
        var second = await fixture.Messages.WaitProgressAsync("second");
        Assert.Equal(70, second.PercentComplete);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(8, second.FilesProcessed);
        Assert.Equal(30, second.TotalFiles);
        await CompleteDatasourceAsync(fixture.Pipe, method, second: true);
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = Assert.Single(fixture.Messages.Completions);
        Assert.True(complete.Success);
        Assert.Equal(method == CorruptionDetectionMethod.Structural
            ? "signalr.corruptionRemove.completeStructural" : "signalr.corruptionRemove.complete", complete.StageKey);
        Assert.Equal(first.OperationId, complete.OperationId);
        Assert.Equal(2L, complete.Context!["count"]);
        Assert.Equal(method == CorruptionDetectionMethod.Structural ? 2L : 9L, complete.Context["files"]);
        if (method == CorruptionDetectionMethod.Structural)
            Assert.Equal(5120L, complete.Context["bytesFreed"]);
        else
        {
            Assert.Equal(14L, complete.Context["logLines"]);
            Assert.Equal(17L, complete.Context["downloads"]);
            Assert.Equal(22L, complete.Context["logEntries"]);
        }
        var firstPositions = fixture.State.GetLogSourcePositions("default");
        var secondPositions = fixture.State.GetLogSourcePositions("secondary");
        if (method == CorruptionDetectionMethod.Structural)
        {
            Assert.Equal(20, firstPositions["access.log"]);
            Assert.Equal(7, firstPositions["steam-access.log"]);
            Assert.Equal(27, fixture.State.GetLogPosition("default"));
            Assert.Equal(30, fixture.State.GetLogTotalLines("default"));
            Assert.Equal(20, secondPositions["access.log"]);
            Assert.Equal(7, secondPositions["steam-access.log"]);
            Assert.Equal(27, fixture.State.GetLogPosition("secondary"));
            Assert.Equal(30, fixture.State.GetLogTotalLines("secondary"));
        }
        else
        {
            Assert.Equal(18, firstPositions["access.log"]);
            Assert.Equal(7, firstPositions["steam-access.log"]);
            Assert.Equal(25, fixture.State.GetLogPosition("default"));
            Assert.Equal(27, fixture.State.GetLogTotalLines("default"));
            Assert.Equal(10, secondPositions["access.log"]);
            Assert.Equal(7, secondPositions["steam-access.log"]);
            Assert.Equal(17, fixture.State.GetLogPosition("secondary"));
            Assert.Equal(19, fixture.State.GetLogTotalLines("secondary"));
        }
        var untouchedPositions = fixture.State.GetLogSourcePositions("untouched");
        Assert.Equal(40, untouchedPositions["access.log"]);
        Assert.Equal(9, untouchedPositions["steam-access.log"]);
        Assert.Equal(49, fixture.State.GetLogPosition("untouched"));
        Assert.Equal(70, fixture.State.GetLogTotalLines("untouched"));
        Assert.DoesNotContain(fixture.Messages.Progress, progress => progress.Status == "completed");
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Completed, false)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Failed, false)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Cancelled, false)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Completed, false)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Failed, false)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Cancelled, false)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Completed, true)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Failed, true)]
    [InlineData(CorruptionDetectionMethod.Structural, OperationStatus.Cancelled, true)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Completed, true)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Failed, true)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, OperationStatus.Cancelled, true)]
    public async Task ExternalWinner_FreezesNonzeroContributionBeforeLateProcessOutput(
        CorruptionDetectionMethod method, OperationStatus outcome, bool completedCheckpoint)
    {
        await using var fixture = new RemovalRun(method, transport: true, datasourceCount: 2);
        var bulkType = typeof(CacheController).GetNestedType("BulkCorruptionRemovalState", BindingFlags.NonPublic)!;
        var bulk = Activator.CreateInstance(bulkType)!;
        bulkType.GetProperty("ServiceCount")!.SetValue(bulk, 2);
        bulkType.GetField("ServiceIndex")!.SetValue(bulk, 1);
        var run = fixture.RunAsync(bulk: bulk);
        await fixture.Pipe!.ConnectAsync();
        await CompleteDatasourceAsync(fixture.Pipe, method, second: false);
        await fixture.Pipe.ConnectAsync();
        await fixture.Pipe.SendAsync(Live("accepted", 40, 8, 30));
        var accepted = await fixture.Messages.WaitProgressAsync("accepted");
        Assert.Equal(1, accepted.Context!["serviceIndex"]);
        Assert.Equal(2, accepted.Context["serviceCount"]);
        var operation = fixture.Tracker.GetOperation(accepted.OperationId)!;
        var metrics = Assert.IsType<RemovalMetrics>(operation.Metadata);
        fixture.Tracker.CompleteOperation(operation.Id, outcome == OperationStatus.Completed,
            error: outcome == OperationStatus.Failed ? "external failure" : null,
            cancelled: outcome == OperationStatus.Cancelled);
        var completedAt = operation.CompletedAt;
        var message = operation.Message;
        bulkType.GetField("ServiceIndex")!.SetValue(bulk, 2);
        if (completedCheckpoint)
            await CompleteDatasourceAsync(fixture.Pipe, method, second: true);
        else
            await fixture.Pipe.SendAsync(Live("late", 90, 99, 100), 0);
        if (outcome == OperationStatus.Cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        else
            Assert.Equal(outcome == OperationStatus.Completed, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(outcome, operation.Status);
        Assert.Equal(completedAt, operation.CompletedAt);
        Assert.Equal(message, operation.Message);
        Assert.Equal(70, operation.PercentComplete);
        Assert.Equal(8, metrics.FilesProcessed);
        Assert.Equal(30, metrics.TotalFiles);
        Assert.DoesNotContain(fixture.Messages.Progress, progress => progress.StageKey == "late");
        var totals = Assert.IsType<CorruptionRemovalCounts>(bulkType.GetProperty("Totals")!.GetValue(bulk));
        Assert.Equal(1L, totals.UrlsRemoved);
        Assert.Equal(method == CorruptionDetectionMethod.Structural ? 1L : 2L, totals.FilesDeleted);
        Assert.Equal(method == CorruptionDetectionMethod.Structural ? 1024L : 0L, totals.BytesFreed);
        if (method == CorruptionDetectionMethod.RepeatedMiss)
        {
            Assert.Equal(3L, totals.LogLinesRemoved);
            Assert.Equal(4L, totals.DownloadsDeleted);
            Assert.Equal(5L, totals.LogEntriesDeleted);
        }
        Assert.Equal(outcome == OperationStatus.Completed ? 1 : 0, bulkType.GetField("SucceededServices")!.GetValue(bulk));
        Assert.Equal(outcome == OperationStatus.Failed ? 1 : 0, bulkType.GetField("FailedServices")!.GetValue(bulk));
        Assert.Equal(outcome == OperationStatus.Cancelled, bulkType.GetField("Cancelled")!.GetValue(bulk));
        Assert.Empty(fixture.Messages.Completions);
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural, false)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, false)]
    [InlineData(CorruptionDetectionMethod.Structural, true)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss, true)]
    public async Task ProcessAggregate_CapturesNonzeroServiceContributionsAndFinalIdentity(
        CorruptionDetectionMethod method, bool failSecond)
    {
        await using var fixture = new RemovalRun(method, transport: true, datasourceCount: 2);
        await fixture.Controller.RemoveAllCorruptedChunksAsync(CancellationToken.None, fixture.ScanId);
        Guid lastId = default;
        for (var serviceIndex = 1; serviceIndex <= 2; serviceIndex++)
        {
            await fixture.Pipe!.ConnectAsync();
            await fixture.Pipe.SendAsync(Live("service", 25, 3, 20));
            var progress = await fixture.Messages.WaitProgressAsync("service");
            lastId = progress.OperationId;
            Assert.Equal(serviceIndex, progress.Context!["serviceIndex"]);
            Assert.Equal(2, progress.Context["serviceCount"]);
            await CompleteDatasourceAsync(fixture.Pipe, method, second: false);
            await fixture.Pipe.ConnectAsync();
            if (failSecond && serviceIndex == 2)
                fixture.Tracker.CompleteOperation(lastId, false, error: "external failure");
            await CompleteDatasourceAsync(fixture.Pipe, method, second: true);
        }
        var aggregate = await fixture.Messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(fixture.Messages.Completions);
        Assert.Equal("all", aggregate.Service);
        Assert.Equal(lastId, aggregate.OperationId);
        Assert.Equal(!failSecond, aggregate.Success);
        Assert.Equal(method.ToWireString(), aggregate.DetectionMethod);
        Assert.Equal(failSecond ? 3L : 4L, aggregate.Context!["count"]);
        Assert.Equal(method == CorruptionDetectionMethod.Structural
            ? (failSecond ? "signalr.corruptionRemove.allCompleteWithFailuresStructural" : "signalr.corruptionRemove.allCompleteStructural")
            : (failSecond ? "signalr.corruptionRemove.allCompleteWithFailures" : "signalr.corruptionRemove.allComplete"), aggregate.StageKey);
        Assert.Equal(2, aggregate.Context["serviceCount"]);
        if (failSecond) Assert.Equal(1, aggregate.Context["failedCount"]);
        if (method == CorruptionDetectionMethod.Structural)
        {
            Assert.Equal(failSecond ? 3L : 4L, aggregate.Context["files"]);
            Assert.Equal(failSecond ? 6144L : 10240L, aggregate.Context["bytesFreed"]);
        }
        else
        {
            Assert.Equal(failSecond ? 11L : 18L, aggregate.Context["files"]);
            Assert.Equal(failSecond ? 17L : 28L, aggregate.Context["logLines"]);
            Assert.Equal(failSecond ? 21L : 34L, aggregate.Context["downloads"]);
            Assert.Equal(failSecond ? 27L : 44L, aggregate.Context["logEntries"]);
        }
        var captured = JsonSerializer.Serialize(aggregate);
        var next = fixture.Tracker.RegisterOperation(OperationType.CorruptionRemoval, "next", new CancellationTokenSource());
        fixture.Messages.Resume.TrySetResult();
        Assert.Equal(captured, JsonSerializer.Serialize(aggregate));
        Assert.False(fixture.Tracker.GetOperation(next)!.Status.IsTerminal());
        fixture.Tracker.CompleteOperation(next, false, cancelled: true);
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    public async Task ProcessAggregate_AFailedFirstServiceTurnsTheSummaryCardAmber(CorruptionDetectionMethod method)
    {
        await using var fixture = new RemovalRun(method, transport: true, datasourceCount: 2);
        await fixture.Controller.RemoveAllCorruptedChunksAsync(CancellationToken.None, fixture.ScanId);
        Guid firstId = default;
        Guid lastId = default;
        for (var serviceIndex = 1; serviceIndex <= 2; serviceIndex++)
        {
            await fixture.Pipe!.ConnectAsync();
            await fixture.Pipe.SendAsync(Live("service", 25, 3, 20));
            var progress = await fixture.Messages.WaitProgressAsync("service");
            lastId = progress.OperationId;
            if (serviceIndex == 1) firstId = lastId;
            await CompleteDatasourceAsync(fixture.Pipe, method, second: false);
            await fixture.Pipe.ConnectAsync();
            if (serviceIndex == 1)
                fixture.Tracker.CompleteOperation(lastId, false, error: "external failure");
            await CompleteDatasourceAsync(fixture.Pipe, method, second: true);
        }

        var aggregate = await fixture.Messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(lastId, aggregate.OperationId);
        Assert.False(aggregate.Success);
        // The failed first service keeps its own red card; the summary's card, the second service's, is amber.
        Assert.Equal("failed", Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationId == firstId).Status);
        var row = Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationId == lastId);
        Assert.Equal("completed", row.Status);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.servicesFailed", warning.StageKey);
        Assert.Equal(1, warning.Context["failedCount"]);
        Assert.Equal(2, warning.Context["serviceCount"]);
        Assert.True(row.Retained);
        // The record keeps the same warning, so a restart before the card is dismissed ends it amber too.
        var repair = await fixture.WaitForCompletedRepairAsync(lastId);
        Assert.Equal("common.notifications.warnings.servicesFailed", Assert.Single(repair.Warnings!).StageKey);
        fixture.Messages.Resume.TrySetResult();
    }

    [Fact]
    public async Task ACorruptionRemovalThatFailedOnOneDatasourceEndsAmberAsync()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.Structural, transport: true, datasourceCount: 2);
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        await fixture.Pipe.SendAsync(Live("service", 25, 3, 20));
        var progress = await fixture.Messages.WaitProgressAsync("service");
        await fixture.Pipe.SendAsync(Completion(CorruptionDetectionMethod.Structural, second: false), 0);
        await fixture.Pipe.ConnectAsync();
        // The same failed checkpoint and exit code a removal child writes when its datasource cannot be cleaned.
        await fixture.Pipe.SendAsync("""{"status":"failed","stageKey":"failed","percentComplete":25,"filesProcessed":3,"totalFiles":20,"context":{"errorDetail":"disk unavailable"}}""", 7);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));

        var row = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationId == progress.OperationId);
        Assert.Equal("completed", row.Status);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.datasourcesFailed", warning.StageKey);
        Assert.Equal("secondary", warning.Context["datasources"]);
        var repair = await fixture.WaitForCompletedRepairAsync(progress.OperationId);
        Assert.Equal(OperationStatus.Completed, repair.Outcome);
        Assert.Equal("common.notifications.warnings.datasourcesFailed", Assert.Single(repair.Warnings!).StageKey);
        fixture.Messages.Resume.TrySetResult();
    }

    [Fact]
    public async Task AForceStopThatLandsDuringACorruptionRemovalsSaveEndsGreenWithItsCountsAsync()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.Structural, transport: true);
        var operationId = Guid.Empty;
        fixture.Messages.OnStarted = id => operationId = id;
        var cancellation = new OperationCancellationService(
            fixture.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            fixture.States,
            NullLogger<OperationCancellationService>.Instance);
        Task<bool>? stop = null;
        // The force stop starts inside the removal's own outcome save and waits for the repair gate behind it.
        fixture.State.OnRepairWrite = contents =>
        {
            if (stop is not null) return;
            var repairs = JsonSerializer.Deserialize<List<OperationRepair>>(contents)!;
            if (!repairs.Any(repair => repair.Id == operationId && repair.Outcome == OperationStatus.Completed)) return;
            stop = Task.Run(() => cancellation.ForceKillAsync(operationId));
            SpinWait.SpinUntil(() => fixture.Tracker.GetOperation(operationId)!.Cancelled, TimeSpan.FromSeconds(10));
        };
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        await fixture.Pipe.SendAsync(Live("running", 25, 3, 20));
        await fixture.Messages.WaitProgressAsync("running");
        await fixture.Pipe.SendAsync(Completion(CorruptionDetectionMethod.Structural, second: false), 0);

        // The force stop set the run's cancel mark, so the core reports the person's stop; the one-service caller ignores it.
        await Assert.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await stop!.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = await fixture.Messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(complete.Success);
        Assert.False(complete.Cancelled);
        Assert.Equal(1L, complete.Context!["count"]);
        Assert.Equal(1L, complete.Context["files"]);
        Assert.Equal(1024L, complete.Context["bytesFreed"]);
        Assert.Equal(OperationStatus.Completed, fixture.Tracker.GetOperation(operationId)!.Status);
        fixture.Messages.Resume.TrySetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XWhileACorruptionRemovalPreparesItsRepairEndsItCanceledAsync(bool removeAll)
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.Structural);
        // The repair owner admits one repair change at a time; holding its gate keeps the removal in its prepare step.
        var gate = (SemaphoreSlim)typeof(OperationStateService)
            .GetField("_admissionGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.States)!;
        await gate.WaitAsync();
        var gateHeld = true;
        try
        {
            var selection = await fixture.Detection.GetRemovalSelectionAsync(fixture.ScanId, "steam");
            var bulkType = typeof(CacheController).GetNestedType("BulkCorruptionRemovalState", BindingFlags.NonPublic)!;
            object? bulk = null;
            if (removeAll)
            {
                bulk = Activator.CreateInstance(bulkType)!;
                bulkType.GetProperty("ServiceCount")!.SetValue(bulk, 2);
                bulkType.GetField("ServiceIndex")!.SetValue(bulk, 1);
            }
            var registeredId = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<Guid> registered = id => registeredId.TrySetResult(id);
            var core = typeof(CacheController).GetMethod("RunCorruptionRemovalCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var run = (Task<bool>)core.Invoke(fixture.Controller, [selection, fixture.Datasources, registered, bulk])!;
            var operationId = await registeredId.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(Guid.Empty, operationId);

            fixture.Tracker.CancelOperation(operationId);
            gate.Release();
            gateHeld = false;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            var operation = fixture.Tracker.GetOperation(operationId)!;
            Assert.Equal(OperationStatus.Cancelled, operation.Status);
            Assert.True(operation.Cancelled);
            if (removeAll)
            {
                Assert.True((bool)bulkType.GetField("Cancelled")!.GetValue(bulk)!);
                Assert.Equal(operationId, bulkType.GetField("LastOperationId")!.GetValue(bulk));
            }
        }
        finally
        {
            if (gateHeld) gate.Release();
        }
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    public async Task RunningProcess_CancellationStopsTheChildAndCompletesOnce(CorruptionDetectionMethod method)
    {
        await using var fixture = new RemovalRun(method, transport: true);
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        // The cache step holds no log lock and prepares no reopen for the logs it never touches.
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.States
            .WaitForLogStepAsync(active: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Empty(Directory.EnumerateFiles(fixture.OperationsDirectory, "nginx_log_check_*"));
        await fixture.Pipe.SendAsync(Live("running", 25, 3, 20));
        var progress = await fixture.Messages.WaitProgressAsync("running");
        fixture.Tracker.CancelOperation(progress.OperationId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = Assert.Single(fixture.Messages.Completions);
        Assert.True(complete.Cancelled);
        Assert.Null(complete.Error);
        Assert.Equal(progress.OperationId, complete.OperationId);
        Assert.Single(fixture.Messages.Progress);
        await fixture.Pipe.WaitForExitAsync();
        var repair = await fixture.WaitForCompletedRepairAsync(progress.OperationId);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        var source = Assert.Single(repair.Sources);
        Assert.False(source.LogRewriteStarted);
        Assert.False(source.LogPositionsKept);
    }

    [Fact]
    public async Task RepeatedMissLogStep_WaitsBehindAnotherStepThenRewritesUnderTheLock()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.RepeatedMiss, transport: true);
        var other = fixture.Tracker.RegisterOperation(
            OperationType.EvictionRemoval,
            "Other removal",
            new CancellationTokenSource());
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        Assert.Equal("remove", fixture.Pipe.Command);
        var operationId = Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.CorruptionRemoval)).Id;
        var held = await fixture.States.LockLogFilesAsync(
            other,
            OperationType.EvictionRemoval,
            LogFileLockKind.Rewrite,
            CancellationToken.None);
        await fixture.Pipe.SendAsync(Completion(CorruptionDetectionMethod.RepeatedMiss, second: false), 0);

        // The log step waits for the other job's step and names it on the card.
        await WaitUntilAsync(() => fixture.Tracker.GetOperation(operationId)?.BlockedByName == "Other removal");
        await held.DisposeAsync();
        await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);
        await fixture.States.WaitForLogStepAsync(active: true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Pipe.SendAsync(LogCompletion(second: false), 0);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = Assert.Single(fixture.Messages.Completions);
        Assert.Equal(3L, complete.Context!["logLines"]);
        Assert.Equal(5L, complete.Context["logEntries"]);
        var repair = await fixture.WaitForCompletedRepairAsync(operationId);
        var source = Assert.Single(repair.Sources);
        Assert.True(source.NativeCompletionAccepted);
        Assert.True(source.LogRewriteStarted);
        Assert.True(source.LogPositionsKept);
        Assert.Equal(2L, source.CorruptionCounts!.FilesDeleted);
        Assert.Equal(3L, source.CorruptionCounts.LogLinesRemoved);
        Assert.Equal(4L, source.CorruptionCounts.DownloadsDeleted);
        Assert.Equal(5L, source.CorruptionCounts.LogEntriesDeleted);
        Assert.Equal(5UL, repair.Removal!.LogEntriesRemoved);
        Assert.False(File.Exists(CorruptionDetectionService.EvidenceFilePath(
            fixture.OperationsDirectory, operationId, "default")));
        fixture.Tracker.CompleteOperation(other, true);
    }

    [Fact]
    public async Task ForceStopInsideTheLogStep_IsRedoneFromTheKeptEvidence()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.RepeatedMiss, transport: true);
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        var operationId = Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.CorruptionRemoval)).Id;
        await fixture.Pipe.SendAsync(Completion(CorruptionDetectionMethod.RepeatedMiss, second: false), 0);
        await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);

        fixture.Tracker.ForceKillOperation(operationId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));

        // The repair finishes the step that started, from the same evidence file.
        var evidence = await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);
        Assert.Equal("default", evidence.Datasource);
        await fixture.Pipe.SendAsync(LogCompletion(second: false), 0);
        var repair = await fixture.WaitForCompletedRepairAsync(operationId);
        var source = Assert.Single(repair.Sources);
        Assert.True(source.LogRewriteStarted);
        Assert.True(source.LogPositionsKept);
        Assert.Equal(3L, source.CorruptionCounts!.LogLinesRemoved);
        Assert.Equal(5UL, repair.Removal!.LogEntriesRemoved);
        Assert.False(File.Exists(CorruptionDetectionService.EvidenceFilePath(
            fixture.OperationsDirectory, operationId, "default")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALogStepRedoneAfterTheLogLayoutChangedRunsWithTheLaunchSchemeAsync(bool mixed)
    {
        // `remove-logs` over files that hold no evidence line: nothing removed, no stem counted, no row
        // matched (cache_corruption.rs:1306-1324; a stem is counted only when it lost a line, log_purge.rs:785-795).
        const string noLineRemoved = """{"status":"completed","stageKey":"signalr.gameRemove.finalizing","context":{"service":"steam","logLines":0,"logLinesBySource":{},"logLinesBeforePositionBySource":{},"downloads":0,"logEntries":0},"percentComplete":100,"filesProcessed":1,"totalFiles":1}""";
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.RepeatedMiss, transport: true);
        var datasource = Assert.Single(fixture.Datasources);
        // Read from the log files, as a datasource with no configured scheme is: access.log alone reads monolithic.
        datasource.SchemeOverride = DatasourceSchemeOverride.Auto;
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        var operationId = Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.CorruptionRemoval)).Id;
        await fixture.Pipe.SendAsync(Completion(CorruptionDetectionMethod.RepeatedMiss, second: false), 0);
        await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);
        // While the log step runs, a per-service log appears beside access.log (both layouts) or the log
        // folder is emptied (no layout), and the job is force stopped inside its log step.
        if (mixed)
        {
            File.WriteAllText(Path.Combine(datasource.LogPath, "steam-access.log"), string.Empty);
        }
        else
        {
            File.Delete(Path.Combine(datasource.LogPath, "access.log"));
        }
        fixture.Tracker.ForceKillOperation(operationId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));

        // The repair redoes the step from the kept evidence with the scheme stored when the job was prepared.
        await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);
        await fixture.Pipe.SendAsync(noLineRemoved, 0);
        var repair = await fixture.WaitForCompletedRepairAsync(operationId);
        var source = Assert.Single(repair.Sources);
        Assert.Equal("monolithic", source.KeyScheme);
        Assert.True(source.LogRewriteStarted);
        Assert.True(source.LogPositionsKept);
        Assert.False(File.Exists(CorruptionDetectionService.EvidenceFilePath(
            fixture.OperationsDirectory, operationId, "default")));
    }

    [Fact]
    public async Task CancelDuringRemoveLogsIsStoredAsACancelAsync()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.RepeatedMiss, transport: true);
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        var operationId = Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.CorruptionRemoval)).Id;
        await fixture.Pipe.SendAsync(Completion(CorruptionDetectionMethod.RepeatedMiss, second: false), 0);
        await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);

        // The stopped child publishes no result for the log it was rewriting.
        fixture.Tracker.CancelOperation(operationId);
        var complete = await fixture.Messages.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(complete.Cancelled, complete.Error);
        Assert.Null(complete.Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));

        // The repair finishes the step that started, from the same evidence file.
        await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);
        await fixture.Pipe.SendAsync(LogCompletion(second: false), 0);
        var repair = await fixture.WaitForCompletedRepairAsync(operationId);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        var source = Assert.Single(repair.Sources);
        Assert.True(source.LogRewriteStarted);
        Assert.True(source.LogPositionsKept);
    }

    [Fact]
    public async Task FailureAfterTheCacheStep_RollsTheLogStepForwardOnce()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.RepeatedMiss, transport: true);
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        var operationId = Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.CorruptionRemoval)).Id;
        // The acceptance write fails, so the job fails after `remove` and before its log step.
        fixture.State.FailNextRepairWrite = true;
        await fixture.Pipe.SendAsync(Completion(CorruptionDetectionMethod.RepeatedMiss, second: false), 0);
        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));

        await fixture.Pipe.ConnectAsync();
        Assert.Equal("remove-logs", fixture.Pipe.Command);
        await fixture.Pipe.SendAsync(LogCompletion(second: false), 0);
        var repair = await fixture.WaitForCompletedRepairAsync(operationId);
        Assert.Equal(OperationStatus.Failed, repair.Outcome);
        var source = Assert.Single(repair.Sources);
        Assert.True(source.NativeCompletionAccepted);
        Assert.True(source.LogRewriteStarted);
        Assert.True(source.LogPositionsKept);
        Assert.Equal(1L, source.CorruptionCounts!.UrlsRemoved);
        Assert.Equal(2L, source.CorruptionCounts.FilesDeleted);
        Assert.Equal(3L, source.CorruptionCounts.LogLinesRemoved);
        Assert.Equal(5UL, repair.Removal!.LogEntriesRemoved);
        Assert.Equal("signalr.corruptionRemove.failed.generic", repair.Removal.StageKey);
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    public async Task ProcessFailure_PreservesStderrAndAcceptedFailureDetail(CorruptionDetectionMethod method)
    {
        await using var fixture = new RemovalRun(method, transport: true);
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        await fixture.Pipe.SendAsync("""{"status":"failed","stageKey":"failed","percentComplete":25,"filesProcessed":3,"totalFiles":20,"context":{"errorDetail":"disk unavailable"}}""", 7);
        var progress = await fixture.Messages.WaitProgressAsync("failed");
        Assert.Equal("disk unavailable", Assert.IsType<JsonElement>(progress.Context!["errorDetail"]).GetString());
        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = Assert.Single(fixture.Messages.Completions);
        Assert.False(complete.Success);
        Assert.False(complete.Cancelled);
        Assert.Contains("Commanded corruption process failure", complete.Error);
        await fixture.Pipe.WaitForExitAsync();
    }

    private static string Live(string stage, double percent, int files, int total) =>
        JsonSerializer.Serialize(new { status = "running", stageKey = stage, percentComplete = percent, filesProcessed = files, totalFiles = total });

    private static string Completion(CorruptionDetectionMethod method, bool second) => method == CorruptionDetectionMethod.Structural
        ? second
            ? """{"status":"completed","percentComplete":100,"context":{"detectionMethod":"structural","count":1,"files":1,"alreadyMissing":0,"healed":0,"keyVerificationSkipped":0,"bytesFreed":4096}}"""
            : """{"status":"completed","percentComplete":100,"context":{"detectionMethod":"structural","count":1,"files":1,"alreadyMissing":0,"healed":0,"keyVerificationSkipped":0,"bytesFreed":1024}}"""
        : second
            ? """{"status":"completed","percentComplete":100,"context":{"count":1,"files":7}}"""
            : """{"status":"completed","percentComplete":100,"context":{"count":1,"files":2}}""";

    // The final checkpoint of `remove-logs`: the log counts and the removed lines per stem.
    private static string LogCompletion(bool second) => second
        ? """{"status":"completed","percentComplete":100,"context":{"logLines":11,"downloads":13,"logEntries":17,"logLinesBySource":{"access.log":10,"steam-access.log":1},"logLinesBeforePositionBySource":{"access.log":10,"steam-access.log":0}}}"""
        : """{"status":"completed","percentComplete":100,"context":{"logLines":3,"downloads":4,"logEntries":5,"logLinesBySource":{"access.log":2,"steam-access.log":1},"logLinesBeforePositionBySource":{"access.log":2,"steam-access.log":0}}}""";

    // Ends one datasource: its `remove` and, for a repeated-miss removal, the `remove-logs` after it.
    private static async Task CompleteDatasourceAsync(RemovalPipe pipe, CorruptionDetectionMethod method, bool second)
    {
        await pipe.SendAsync(Completion(method, second), 0);
        if (method == CorruptionDetectionMethod.RepeatedMiss)
        {
            await pipe.ConnectAsync();
            Assert.Equal("remove-logs", pipe.Command);
            await pipe.SendAsync(LogCompletion(second), 0);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }
        Assert.True(condition());
    }

    private sealed class RemovalRun : IDisposable, IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "corruption-terminal-" + Guid.NewGuid().ToString("N"));
        public Guid ScanId { get; } = Guid.NewGuid();
        public UnifiedOperationTracker Tracker { get; }
        public CorruptionDetectionService Detection { get; }
        public CacheController Controller { get; }
        public RemovalMessages Messages { get; }
        public OperationStateService States => _operationStateService;
        public OperationRepairTests.FailingStateService State { get; }
        public string OperationsDirectory => Path.Combine(_root, "GetOperationsDirectory");
        public List<ResolvedDatasource> Datasources { get; }
        public RemovalPipe? Pipe { get; }
        private readonly List<Task> _runs = [];
        private readonly ServiceProvider _services;
        private readonly OperationStateService _operationStateService;

        public RemovalRun(CorruptionDetectionMethod method, bool transport = false, int datasourceCount = 1)
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "cache"));
            Directory.CreateDirectory(Path.Combine(_root, "logs"));
            Directory.CreateDirectory(Path.Combine(_root, "GetOperationsDirectory"));
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = _root;
            var settings = new Dictionary<string, string?> { ["NginxLogRotation:Enabled"] = "false" };
            for (var index = 0; index < datasourceCount; index++)
            {
                var name = index == 0 ? "default" : "secondary";
                var cachePath = Path.Combine(_root, name, "cache");
                var logPath = Path.Combine(_root, name, "logs");
                Directory.CreateDirectory(cachePath);
                Directory.CreateDirectory(logPath);
                File.WriteAllText(Path.Combine(logPath, "access.log"), "[steam] fixture\n");
                settings[$"LanCache:DataSources:{index}:Name"] = name;
                settings[$"LanCache:DataSources:{index}:CachePath"] = cachePath;
                settings[$"LanCache:DataSources:{index}:LogPath"] = logPath;
                settings[$"LanCache:DataSources:{index}:Enabled"] = "true";
                settings[$"LanCache:DataSources:{index}:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic;
            }
            if (transport)
            {
                Pipe = new RemovalPipe(Path.Combine(_root, "GetOperationsDirectory"));
                var executable = Path.Combine(AppContext.BaseDirectory, "corruption-process",
                    OperatingSystem.IsWindows() ? "CorruptionProcess.exe" : "CorruptionProcess");
                Assert.True(File.Exists(executable), $"Built corruption process is missing: {executable}");
                var forwarded = DispatchProxy.Create<IPathResolver, RemovalPaths>();
                ((RemovalPaths)(object)forwarded).Inner = paths;
                ((RemovalPaths)(object)forwarded).Executable = executable;
                paths = forwarded;
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var sources = new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);
            Datasources = sources.GetDatasources().ToList();
            var capability = new DatasourceCapabilityService(sources);
            var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase("corruption-terminal-" + ScanId)
                .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            using (var db = contexts.CreateDbContext())
            {
                var time = DateTime.Parse("2026-07-12T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal);
                db.CachedCorruptionScans.Add(new CachedCorruptionScan
                {
                    ScanId = ScanId, ContractVersion = 4, IsCurrent = true,
                    DetectionMode = method == CorruptionDetectionMethod.Structural ? CorruptionDetectionMode.Structural : CorruptionDetectionMode.RepeatedMiss,
                    Threshold = 3, LookbackDays = 30, StartedAtUtc = time, CompletedAtUtc = time
                });
                var projection = new CachedCorruptionProjection
                {
                    Settings = method == CorruptionDetectionMethod.Structural
                        ? new CorruptionScanSettings { MinimumStableAgeSeconds = 600, MaximumPrefixBytes = 65_535 }
                        : new CorruptionScanSettings { Threshold = 3, LookbackDays = 30 },
                    Coverage = method == CorruptionDetectionMethod.Structural ? new CorruptionScanCoverage { FilesSeen = 2 * datasourceCount, FilesChecked = 2 * datasourceCount } : null,
                    DetectionCounts = new Dictionary<string, long> { [method.ToWireString()] = 2 * datasourceCount }
                };
                db.CachedCorruptionDetections.Add(new CachedCorruptionDetection
                {
                    ScanId = ScanId, ServiceName = "__scan_projection__", DatasourceName = "__aggregate__",
                    CandidatesJson = JsonSerializer.Serialize(projection, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                });
                foreach (var service in new[] { "steam", "epicgames" })
                foreach (var datasource in Datasources)
                {
                    CorruptionEvidence evidence = method == CorruptionDetectionMethod.Structural
                        ? new StructuralCorruptionEvidence
                        {
                            Issues = [StructuralCorruptionIssue.EmptyCacheFile], CacheKeyEncoding = "hex",
                            CacheKeyMd5 = "d41d8cd98f00b204e9800998ecf8427e", CacheVersion = 5,
                            DetectedAtUtc = "2026-07-12T00:00:00Z"
                        }
                        : new RepeatedMissCorruptionEvidence
                        {
                            RawUrl = "/chunk", NormalizedUri = "/chunk", EvidenceCount = 3,
                            FirstSeen = "2026-07-12T00:00:00Z", LastSeen = "2026-07-12T00:00:00Z",
                            Observations = Enumerable.Range(0, 3).Select(_ => new CandidateObservation
                            {
                                RawUrl = "/chunk", Timestamp = "2026-07-12T00:00:00Z", ClientIp = "127.0.0.1",
                                Method = "GET", HttpStatus = 200, CacheStatus = "MISS"
                            }).ToList()
                        };
                    db.CachedCorruptionDetections.Add(new CachedCorruptionDetection
                    {
                        ScanId = ScanId, ServiceName = service, DatasourceName = datasource.Name, CorruptedChunkCount = 1, RemovalAllowed = true,
                        CandidatesJson = CorruptionDetectionService.SerializeCandidates([new CorruptionCandidate
                        {
                            CandidateId = datasource.Name + ":" + service, Datasource = datasource.Name, Service = service,
                            ExactPaths = [Path.Combine(datasource.CachePath, service)], Evidence = evidence
                        }])
                    });
                }
                db.SaveChanges();
            }
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            Tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RemovalMessages>();
            Messages = (RemovalMessages)(object)notifications;
            Messages.Tracker = Tracker;
            var rust = new RustProcessHelper(NullLogger<RustProcessHelper>.Instance, processManager, paths, Tracker);
            State = OperationRepairTests.CreateFailingStateService(_root);
            _services = new ServiceCollection().BuildServiceProvider();
            var operationStateService = new CorruptionRepairOwner(
                NullLogger<OperationStateService>.Instance,
                configuration,
                State,
                _services.GetRequiredService<IServiceScopeFactory>(),
                new HostLifetime(),
                processManager,
                Tracker);
            _operationStateService = operationStateService;
            _operationStateService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var nginx = new NginxLogRotationService(NullLogger<NginxLogRotationService>.Instance,
                configuration, processManager, paths);
            Detection = new CorruptionDetectionService(NullLogger<CorruptionDetectionService>.Instance,
                configuration, paths, rust, notifications, sources, contexts, _operationStateService, Tracker, capability, CacheScanGateHarness.Idle(),
                nginx, State);
            operationStateService.Service = Detection;
            var cache = new CacheManagementService(configuration, NullLogger<CacheManagementService>.Instance,
                paths, rust, nginx, sources, State, contexts, null!, Tracker, notifications,
                DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
                DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(), capability, CacheScanGateHarness.Idle(),
                _operationStateService);
            Controller = new CacheController(cache, null!, Detection, NullLogger<CacheController>.Instance,
                paths, notifications, rust, nginx, Tracker, sources, contexts, null!,
                DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(), null!, capability, CacheScanGateHarness.Idle(), null!, _operationStateService);
        }

        public async Task<bool> RunAsync(string service = "steam", object? bulk = null)
        {
            var selection = await Detection.GetRemovalSelectionAsync(ScanId, service);
            var core = typeof(CacheController).GetMethod("RunCorruptionRemovalCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var run = (Task<bool>)core.Invoke(Controller, [selection, Datasources, null, bulk])!;
            _runs.Add(run);
            return await run;
        }

        public async Task<OperationRepair> WaitForCompletedRepairAsync(Guid operationId)
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                var repair = State.LoadOperationRepairs().SingleOrDefault(repair => repair.Id == operationId);
                if (repair?.Phase == OperationRepairPhase.Completed)
                {
                    return repair;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"Corruption removal repair {operationId} did not complete.");
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            Messages.Resume.TrySetResult();
            foreach (var operation in Tracker.GetActiveOperations(OperationType.CorruptionRemoval))
                Tracker.ForceKillOperation(operation.Id);
            if (Pipe != null) await Pipe.DisposeAsync();
            foreach (var run in _runs)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (run.IsCompleted) { /* The test observes the operation outcome; teardown still drains it. */ }
            }
            await _operationStateService.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(_root)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The removal fixture directory is outside the temporary directory");
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class HostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class CorruptionRepairOwner : OperationStateService
    {
        public CorruptionRepairOwner(
            ILogger<OperationStateService> logger,
            IConfiguration configuration,
            StateService stateService,
            IServiceScopeFactory scopes,
            IHostApplicationLifetime applicationLifetime,
            ProcessManager processManager,
            IUnifiedOperationTracker operationTracker)
            : base(
                logger,
                configuration,
                stateService,
                scopes,
                applicationLifetime,
                processManager,
                operationTracker)
        {
        }

        public CorruptionDetectionService? Service { get; set; }

        // Production hands the owner the dispatch's copy, whose scheme the dispatch clears for a folder that
        // no longer proves its layout. Clearing it on every source makes a redo that reads the copy fail.
        protected override Task ApplyRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
        {
            var applied = JsonSerializer.Deserialize<OperationRepair>(JsonSerializer.Serialize(repair))!;
            foreach (var source in applied.Sources)
            {
                source.KeyScheme = null;
            }

            return (Service ?? throw new InvalidOperationException("The corruption detection service is not initialized."))
                .ResumeRepairAsync(applied, stoppingToken);
        }

        protected override Task RestoreOwnerAsync(OperationRepair repair, CancellationToken stoppingToken) =>
            (Service ?? throw new InvalidOperationException("The corruption detection service is not initialized."))
                .RestoreRepairAsync(repair, stoppingToken);
    }

    public class RemovalMessages : DispatchProxy
    {
        public ConcurrentQueue<Guid> Started { get; } = new();
        public ConcurrentQueue<SignalRNotifications.CorruptionRemovalComplete> Completions { get; } = new();
        public TaskCompletionSource<SignalRNotifications.CorruptionRemovalComplete> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<Guid>? OnStarted { get; set; }
        public UnifiedOperationTracker? Tracker { get; set; }
        public ConcurrentQueue<SignalRNotifications.CorruptionRemovalProgress> Progress { get; } = new();
        public Channel<SignalRNotifications.CorruptionRemovalProgress> Progressed { get; } = Channel.CreateUnbounded<SignalRNotifications.CorruptionRemovalProgress>();

        public async Task<SignalRNotifications.CorruptionRemovalProgress> WaitProgressAsync(string stage)
        {
            while (true)
            {
                var progress = await Progressed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                if (progress.StageKey == stage) return progress;
            }
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: >= 2 })
            {
                if (args[1] is SignalRNotifications.CorruptionRemovalProgress progress)
                {
                    var operation = Tracker?.GetOperation(progress.OperationId);
                    Assert.True(operation == null || !Monitor.IsEntered(operation));
                    Progress.Enqueue(progress);
                    Progressed.Writer.TryWrite(progress);
                }
                if (args[1] is SignalRNotifications.CorruptionRemovalStarted started)
                {
                    Started.Enqueue(started.OperationId);
                    OnStarted?.Invoke(started.OperationId);
                }
                if (args[1] is SignalRNotifications.CorruptionRemovalComplete complete)
                {
                    var operation = complete.OperationId.HasValue ? Tracker?.GetOperation(complete.OperationId.Value) : null;
                    Assert.True(operation == null || !Monitor.IsEntered(operation));
                    Completions.Enqueue(complete);
                    Completed.TrySetResult(complete);
                    return Resume.Task;
                }
            }
            if (targetMethod!.ReturnType == typeof(Task)) return Task.CompletedTask;
            return null;
        }
    }

    public class RemovalPaths : DispatchProxy
    {
        public IPathResolver Inner { get; set; } = null!;
        public string Executable { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IPathResolver.GetRustCorruptionManagerPath)
                ? Executable : targetMethod.Invoke(Inner, args);
    }

    private sealed class RemovalPipe : IAsyncDisposable
    {
        private readonly string _name = "corruption-" + Guid.NewGuid().ToString("N");
        private readonly string _directory;
        private readonly List<Process> _processes = [];
        private NamedPipeServerStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public RemovalPipe(string directory)
        {
            _directory = directory;
            File.WriteAllText(Path.Combine(directory, "corruption-pipe"), _name);
            _pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        /// <summary>The command of the child that connected last.</summary>
        public string? Command { get; private set; }

        public async Task<CorruptionRemovalEvidence> ConnectAsync()
        {
            if (_reader != null)
            {
                _reader.Dispose();
                _writer!.Dispose();
                _pipe!.Dispose();
                _pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            await _pipe!.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _reader = new StreamReader(_pipe, leaveOpen: true);
            _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var connection = JsonDocument.Parse(line!);
            Command = connection.RootElement.GetProperty("command").GetString();
            var process = Process.GetProcessById(connection.RootElement.GetProperty("processId").GetInt32());
            _ = process.Handle;
            _processes.Add(process);
            var evidencePath = connection.RootElement.GetProperty("evidencePath").GetString()!;
            return JsonSerializer.Deserialize<CorruptionRemovalEvidence>(await File.ReadAllTextAsync(evidencePath))!;
        }

        public async Task SendAsync(string checkpoint, int? exitCode = null)
        {
            if (exitCode == 0)
            {
                foreach (var checkPath in Directory.EnumerateFiles(_directory, "nginx_log_check_*.json"))
                {
                    var check = JsonSerializer.Deserialize<NginxPublicationCheckFile>(
                        await File.ReadAllTextAsync(checkPath),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    var result = new NginxPublicationResult(
                        check.Valid,
                        check.Files.Select(file => new NginxPublicationRecord(
                            file.TargetPath,
                            file.OriginalIdentity,
                            null,
                            file.OriginalIdentity,
                            Changed: false,
                            Deleted: false)).ToList());
                    var resultPath = Path.Combine(
                        _directory,
                        Path.GetFileName(checkPath).Replace(
                            "nginx_log_check_",
                            "nginx_log_result_",
                            StringComparison.Ordinal));
                    await File.WriteAllTextAsync(
                        resultPath,
                        JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                }
            }
            using var document = JsonDocument.Parse(checkpoint);
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(new { Checkpoint = document.RootElement, ExitCode = exitCode }))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task WaitForExitAsync()
        {
            foreach (var process in _processes)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(process.HasExited);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _reader?.Dispose();
            if (_writer != null) await _writer.DisposeAsync();
            if (_pipe != null) await _pipe.DisposeAsync();
            foreach (var process in _processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                process.Dispose();
            }
        }
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
