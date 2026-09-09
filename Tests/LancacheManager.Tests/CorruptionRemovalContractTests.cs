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
using Microsoft.Extensions.Logging.Abstractions;
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
    public void ReadContextStemCounts_reads_the_map_a_rust_checkpoint_round_trips()
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            "{\"logLines\":41,\"logLinesBySource\":{\"access.log\":40,\"steam-access.log\":1},\"logLinesBeforePositionBySource\":{\"access.log\":39}}")!;

        var counts = CacheController.ReadContextStemCounts(json, "logLinesBySource");

        Assert.Equal(2, counts.Count);
        Assert.Equal(40, counts["access.log"]);
        Assert.Equal(1, counts["steam-access.log"]);

        var before = CacheController.ReadContextStemCounts(json, "logLinesBeforePositionBySource");
        Assert.Equal(39, before["access.log"]);
    }

    [Fact]
    public void ReadContextStemCounts_is_empty_for_a_missing_or_malformed_map()
    {
        Assert.Empty(CacheController.ReadContextStemCounts(null, "logLinesBySource"));
        Assert.Empty(CacheController.ReadContextStemCounts(
            new Dictionary<string, object?>(), "logLinesBySource"));
        var notAMap = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            "{\"logLinesBySource\":\"41\"}")!;
        Assert.Empty(CacheController.ReadContextStemCounts(notAMap, "logLinesBySource"));
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
        var arguments = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-structural",
            "C:/logs-secret",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "bare_metal");

        Assert.Equal(
            "remove-structural \"C:/cache\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress --key-scheme bare_metal",
            arguments);
        Assert.DoesNotContain("logs-secret", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("steam", arguments, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-structural", "", "", null, "evidence", "progress", "bare_metal"));
    }

    [Fact]
    public void RustRepeatedMissRemovalCommand_UsesMonolithicKeyScheme()
    {
        var arguments = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove",
            "C:/logs",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "monolithic");

        Assert.Equal(
            "remove \"C:/logs\" \"C:/cache\" \"steam\" \"C:/ops/progress.json\" --evidence-file \"C:/ops/evidence.json\" --progress --key-scheme monolithic",
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
    public async Task ProcessCompletion_AccumulatesBothDatasourcesAndPublishesAcceptedProgress(CorruptionDetectionMethod method)
    {
        await using var fixture = new RemovalRun(method, transport: true, datasourceCount: 2);
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
        await fixture.Pipe.SendAsync(Completion(method, second: false), 0);
        evidence = await fixture.Pipe.ConnectAsync();
        Assert.Equal("secondary", evidence.Datasource);
        await fixture.Pipe.SendAsync(Live("second", 40, 8, 30));
        var second = await fixture.Messages.WaitProgressAsync("second");
        Assert.Equal(70, second.PercentComplete);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(8, second.FilesProcessed);
        Assert.Equal(30, second.TotalFiles);
        await fixture.Pipe.SendAsync(Completion(method, second: true), 0);
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
        await fixture.Pipe.SendAsync(Completion(method, second: false), 0);
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
        await fixture.Pipe.SendAsync(completedCheckpoint ? Completion(method, second: true) : Live("late", 90, 99, 100), 0);
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
        var totals = bulkType.GetProperty("Totals")!.GetValue(bulk)!;
        var context = (Dictionary<string, object?>)totals.GetType().GetMethod("ToContext")!.Invoke(totals, ["steam"])!;
        Assert.Equal(1L, context["count"]);
        Assert.Equal(method == CorruptionDetectionMethod.Structural ? 1L : 2L, context["files"]);
        Assert.Equal(method == CorruptionDetectionMethod.Structural ? 1024L : 0L, context["bytesFreed"]);
        if (method == CorruptionDetectionMethod.RepeatedMiss)
        {
            Assert.Equal(3L, context["logLines"]);
            Assert.Equal(4L, context["downloads"]);
            Assert.Equal(5L, context["logEntries"]);
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
            await fixture.Pipe.SendAsync(Completion(method, second: false), 0);
            await fixture.Pipe.ConnectAsync();
            if (failSecond && serviceIndex == 2)
                fixture.Tracker.CompleteOperation(lastId, false, error: "external failure");
            await fixture.Pipe.SendAsync(Completion(method, second: true), 0);
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

    [Fact]
    public async Task RunningProcess_CancellationStopsTheChildAndCompletesOnce()
    {
        await using var fixture = new RemovalRun(CorruptionDetectionMethod.Structural, transport: true);
        var run = fixture.RunAsync();
        await fixture.Pipe!.ConnectAsync();
        await fixture.Pipe.SendAsync(Live("running", 25, 3, 20));
        var progress = await fixture.Messages.WaitProgressAsync("running");
        fixture.Tracker.CancelOperation(progress.OperationId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = Assert.Single(fixture.Messages.Completions);
        Assert.True(complete.Cancelled);
        Assert.Equal(progress.OperationId, complete.OperationId);
        Assert.Single(fixture.Messages.Progress);
        await fixture.Pipe.WaitForExitAsync();
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
            ? """{"status":"completed","percentComplete":100,"context":{"detectionMethod":"structural","count":1,"files":1,"alreadyMissing":0,"healed":0,"bytesFreed":4096}}"""
            : """{"status":"completed","percentComplete":100,"context":{"detectionMethod":"structural","count":1,"files":1,"alreadyMissing":0,"healed":0,"bytesFreed":1024}}"""
        : second
            ? """{"status":"completed","percentComplete":100,"context":{"count":1,"files":7,"logLines":11,"downloads":13,"logEntries":17,"logLinesBySource":{"access.log":11},"logLinesBeforePositionBySource":{"access.log":10}}}"""
            : """{"status":"completed","percentComplete":100,"context":{"count":1,"files":2,"logLines":3,"downloads":4,"logEntries":5,"logLinesBySource":{"access.log":3},"logLinesBeforePositionBySource":{"access.log":2}}}""";

    private sealed class RemovalRun : IDisposable, IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "corruption-terminal-" + Guid.NewGuid().ToString("N"));
        public Guid ScanId { get; } = Guid.NewGuid();
        public UnifiedOperationTracker Tracker { get; }
        public CorruptionDetectionService Detection { get; }
        public CacheController Controller { get; }
        public RemovalMessages Messages { get; }
        public List<ResolvedDatasource> Datasources { get; }
        public RemovalPipe? Pipe { get; }
        private readonly List<Task> _runs = [];

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
            Tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RemovalMessages>();
            Messages = (RemovalMessages)(object)notifications;
            Messages.Tracker = Tracker;
            var rust = new RustProcessHelper(NullLogger<RustProcessHelper>.Instance, new ProcessManager(NullLogger<ProcessManager>.Instance), paths, Tracker);
            var state = DispatchProxy.Create<IStateService, NullReturningProxy>();
            Detection = new CorruptionDetectionService(NullLogger<CorruptionDetectionService>.Instance,
                configuration, paths, rust, notifications, sources, contexts, null!, Tracker, capability, CacheScanGateHarness.Idle());
            var nginx = new NginxLogRotationService(NullLogger<NginxLogRotationService>.Instance,
                configuration, new ProcessManager(NullLogger<ProcessManager>.Instance), paths);
            var cache = new CacheManagementService(configuration, NullLogger<CacheManagementService>.Instance,
                paths, rust, nginx, sources, state, contexts, null!, Tracker, notifications,
                DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
                DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(), capability, CacheScanGateHarness.Idle());
            Controller = new CacheController(cache, null!, Detection, NullLogger<CacheController>.Instance,
                paths, notifications, rust, nginx, Tracker, sources, contexts, null!,
                DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(), null!, capability, state, CacheScanGateHarness.Idle());
        }

        public async Task<bool> RunAsync(string service = "steam", object? bulk = null)
        {
            var selection = await Detection.GetRemovalSelectionAsync(ScanId, service);
            var core = typeof(CacheController).GetMethod("RunCorruptionRemovalCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var run = (Task<bool>)core.Invoke(Controller, [selection, Datasources, null, bulk])!;
            _runs.Add(run);
            return await run;
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
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(_root)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The removal fixture directory is outside the temporary directory");
            Directory.Delete(_root, recursive: true);
        }
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
        private readonly List<Process> _processes = [];
        private NamedPipeServerStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public RemovalPipe(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "corruption-pipe"), _name);
            _pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

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
            var process = Process.GetProcessById(connection.RootElement.GetProperty("processId").GetInt32());
            _ = process.Handle;
            _processes.Add(process);
            var evidencePath = connection.RootElement.GetProperty("evidencePath").GetString()!;
            return JsonSerializer.Deserialize<CorruptionRemovalEvidence>(await File.ReadAllTextAsync(evidencePath))!;
        }

        public async Task SendAsync(string checkpoint, int? exitCode = null)
        {
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
