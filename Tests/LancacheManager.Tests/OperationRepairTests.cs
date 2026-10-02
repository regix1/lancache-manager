using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LancacheManager.Configuration;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class OperationRepairTests : IDisposable
{
    private readonly string _root;

    public OperationRepairTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-operation-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void StrictLoadRejectsMalformedJsonAndPreservesOriginalBytes()
    {
        var path = RepairFilePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = "[{not-json";
        File.WriteAllText(path, original);

        var service = CreateStateService(_root);

        Assert.ThrowsAny<JsonException>(() => service.LoadOperationRepairs());
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void StrictLoadRejectsUnknownVersionAndMissingMetrics()
    {
        var valid = NewLogProcessingRepair();
        var path = RepairFilePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var unknownVersion = JsonSerializer.SerializeToNode(new[] { valid })!.AsArray();
        unknownVersion[0]![nameof(OperationRepair.Version)] = OperationRepair.CurrentVersion + 1;
        File.WriteAllText(path, unknownVersion.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());

        var missingMetrics = JsonSerializer.SerializeToNode(new[] { valid })!.AsArray();
        missingMetrics[0]!.AsObject().Remove(nameof(OperationRepair.LogProcessing));
        File.WriteAllText(path, missingMetrics.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());

        var wrongMetrics = JsonSerializer.SerializeToNode(new[] { valid })!.AsArray();
        wrongMetrics[0]!.AsObject().Remove(nameof(OperationRepair.LogProcessing));
        wrongMetrics[0]![nameof(OperationRepair.EvictionScan)] = JsonSerializer.SerializeToNode(
            new EvictionScanRepair());
        File.WriteAllText(path, wrongMetrics.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());
    }

    [Fact]
    public void StrictLoadDropsOlderCompletedRowsAndRejectsOlderPendingRows()
    {
        var path = RepairFilePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var completed = NewLogProcessingRepair(phase: OperationRepairPhase.Completed);
        completed.Outcome = OperationStatus.Completed;
        completed.CompletedAt = DateTime.UtcNow;
        var current = NewLogProcessingRepair(phase: OperationRepairPhase.Running);

        var rows = JsonSerializer.SerializeToNode(new[] { completed, current })!.AsArray();
        rows[0]![nameof(OperationRepair.Version)] = OperationRepair.CurrentVersion - 1;
        File.WriteAllText(path, rows.ToJsonString());
        Assert.Equal(current.Id, Assert.Single(CreateStateService(_root).LoadOperationRepairs()).Id);

        var pending = JsonSerializer.SerializeToNode(new[] { current })!.AsArray();
        pending[0]![nameof(OperationRepair.Version)] = OperationRepair.CurrentVersion - 1;
        File.WriteAllText(path, pending.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());
    }

    [Fact]
    public void StrictLoadRejectsInvalidCountersTargetsAndCandidateTypes()
    {
        var path = RepairFilePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var negative = NewLogProcessingRepair(entries: -1, phase: OperationRepairPhase.Running);
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { negative }));
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());

        var mixedTarget = TypedRepairs().Single(repair => repair.Type == OperationType.GameRemoval);
        mixedTarget.Target!.EpicGame = "another-game";
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { mixedTarget }));
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());

        var numericCandidate = JsonSerializer.SerializeToNode(
            new[] { TypedRepairs().Single(repair => repair.Type == OperationType.CorruptionRemoval) })!.AsArray();
        numericCandidate[0]![nameof(OperationRepair.Sources)]![0]![nameof(OperationRepairSource.CorruptionCandidateIds)] =
            new JsonArray(7);
        File.WriteAllText(path, numericCandidate.ToJsonString());
        Assert.Throws<JsonException>(() => CreateStateService(_root).LoadOperationRepairs());

        var unacceptedCandidates = TypedRepairs()
            .Single(repair => repair.Type == OperationType.CorruptionRemoval);
        var unacceptedSource = unacceptedCandidates.Sources.Single();
        unacceptedSource.NativeCompletionAccepted = false;
        unacceptedSource.CorruptionCounts = null;
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { unacceptedCandidates }));
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());

        var missingDetectionLists = JsonSerializer.SerializeToNode(
            new[] { TypedRepairs().Single(repair => repair.Type == OperationType.GameDetection) })!.AsArray();
        missingDetectionLists[0]![nameof(OperationRepair.GameDetection)]![nameof(GameDetectionMetrics.Games)] = null;
        File.WriteAllText(path, missingDetectionLists.ToJsonString());
        Assert.Throws<InvalidDataException>(() => CreateStateService(_root).LoadOperationRepairs());
    }

    [Fact]
    public void StrictStorageRoundTripsEveryTypedMetricsFamily()
    {
        var repairs = TypedRepairs();
        using var noticeCancellation = new CancellationTokenSource();
        repairs[0].Notice!.Token = noticeCancellation.Token;
        var service = CreateStateService(_root);

        service.SaveOperationRepairs(repairs);
        var loaded = CreateStateService(_root).LoadOperationRepairs();

        Assert.Equal(
            JsonSerializer.Serialize(repairs),
            JsonSerializer.Serialize(loaded));
        Assert.DoesNotContain(nameof(RunNotice.Token), File.ReadAllText(RepairFilePath(_root)));
        Assert.All(loaded, repair =>
        {
            Assert.Equal(NotificationMode.All, repair.Notice!.Mode);
            Assert.Equal(RunTrigger.Manual, repair.Notice.Trigger);
            Assert.Equal(ScheduleActorKind.Account, repair.Notice.Actor!.Kind);
            Assert.True(repair.Notice.RestoredOrigin);
        });
        Assert.Equal(
            ulong.MaxValue - 7,
            loaded.Single(repair => repair.Type == OperationType.GameRemoval).Removal!.LogEntriesRemoved);
        Assert.Equal("alpha:MixedCase/7", loaded.Single(repair => repair.Corruption is not null)
            .Sources.Single().CorruptionCandidateIds.Single());
    }

    [Fact]
    public void StrictStorageAcceptsEachRemovalTargetForm()
    {
        var repairs = new[]
        {
            NewRemovalRepair(
                OperationType.GameRemoval,
                new CacheRepairTarget { SteamAppId = 480, SteamDepotIds = [481, 482] }),
            NewRemovalRepair(
                OperationType.GameRemoval,
                new CacheRepairTarget { EpicGame = "fortnite" }),
            NewRemovalRepair(
                OperationType.GameRemoval,
                new CacheRepairTarget { GameName = "Diablo IV", Service = "blizzard" }),
            NewRemovalRepair(
                OperationType.ServiceRemoval,
                new CacheRepairTarget { Service = "steam" })
        };
        var service = CreateStateService(_root);

        service.SaveOperationRepairs(repairs);
        var loaded = service.LoadOperationRepairs();

        Assert.Equal(
            repairs.Select(repair => JsonSerializer.Serialize(repair.Target)),
            loaded.Select(repair => JsonSerializer.Serialize(repair.Target)));
    }

    [Fact]
    public void StrictStorageRejectsEmptyDepotOnlyAndMixedRemovalTargets()
    {
        var invalidTargets = new[]
        {
            new CacheRepairTarget(),
            new CacheRepairTarget { SteamDepotIds = [481] },
            new CacheRepairTarget { SteamAppId = 480, EpicGame = "fortnite" }
        };
        var service = CreateStateService(_root);

        foreach (var target in invalidTargets)
        {
            Assert.Throws<InvalidDataException>(
                () => service.SaveOperationRepairs(
                    [NewRemovalRepair(OperationType.GameRemoval, target)]));
        }

        var evictionRemoval = TypedRepairs()
            .Single(repair => repair.Type == OperationType.EvictionRemoval);
        evictionRemoval.Target = new CacheRepairTarget { Service = "steam" };
        Assert.Throws<InvalidDataException>(() => service.SaveOperationRepairs([evictionRemoval]));
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("newer")]
    [InlineData("older-pending")]
    public async Task UnreadableRepairFileIsSetAsideAndEveryDatasourceGetsAFullRepairAsync(string file)
    {
        var root = Path.Combine(_root, "full-repair-" + file);
        await using var harness = await DispatchHarness.CreateAsync(root, unmappedDatasource: true, start: false);
        var path = RepairFilePath(Path.Combine(root, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = JsonSerializer.SerializeToNode(new[] { NewLogProcessingRepair() })!.AsArray();
        pending[0]![nameof(OperationRepair.Version)] = file == "newer"
            ? OperationRepair.CurrentVersion + 1
            : OperationRepair.CurrentVersion - 1;
        var original = file == "damaged" ? "not-json" : pending.ToJsonString();
        File.WriteAllText(path, original);
        await using (var seed = harness.CreateContext())
        {
            seed.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.5",
                Datasource = "alpha",
                StartTimeUtc = harness.Now.AddHours(-2),
                EndTimeUtc = harness.Now.AddHours(-1),
                CacheHitBytes = 4096
            });
            await seed.SaveChangesAsync();
        }
        harness.State.SetLogPosition("alpha", 73);
        harness.State.SetLogPosition("unmapped", 91);
        harness.HoldScans = true;

        using var host = harness.Repairs.CreateHost();
        await host.StartAsync(CancellationToken.None);

        var movedTo = Assert.Single(Directory.GetFiles(
            Path.GetDirectoryName(path)!,
            "operation_repairs.json.unreadable-*"));
        Assert.Equal(original, File.ReadAllText(movedTo));
        Assert.Contains(
            harness.Log.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains(movedTo, StringComparison.Ordinal));
        var fullRepairs = harness.Owner.GetPendingRepairs();
        Assert.Equal(["alpha", "unmapped"], fullRepairs.Select(repair => repair.Sources.Single().Datasource).Order());
        Assert.All(fullRepairs, repair =>
        {
            Assert.Equal(OperationType.CacheClearing, repair.Type);
            Assert.Equal("Full cache repair", repair.Name);
            Assert.Null(repair.Notice);
            Assert.Equal(OperationRepairPhase.Repairing, repair.Phase);
            Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
            Assert.True(repair.CacheClearing!.FullRepair);
            Assert.Null(repair.Sources.Single().ReceiptPath);
        });
        var alpha = fullRepairs.Single(repair => repair.Sources.Single().Datasource == "alpha");
        var unmapped = fullRepairs.Single(repair => repair.Sources.Single().Datasource == "unmapped");
        Assert.Equal(
            harness.Capabilities.GetKeySchemeWireValue(harness.Datasources.GetDatasource("alpha")!),
            alpha.Sources.Single().KeyScheme);
        Assert.Null(unmapped.Sources.Single().KeyScheme);

        // A job admitted right after startup queues behind the full repairs.
        var removal = harness.NewRemoval(OperationType.GameRemoval);
        await harness.Owner.PrepareRepairAsync(removal, CancellationToken.None);
        var admitted = harness.Owner.StartWorkAsync(removal.Id, "alpha", CancellationToken.None);
        await WaitForAsync(() => harness.Scans == 1);
        Assert.False(admitted.IsCompleted);
        var row = Assert.Single(harness.Repairs.Tracker.GetRuns().Runs, run => run.OperationId == alpha.Id);
        Assert.Equal("cancelled", row.Status);
        Assert.True(row.Repairing);
        Assert.True(row.FullRepair);
        Assert.Equal(RunVisibility.Card, row.Visibility);

        harness.ScanRelease.TrySetResult();
        await admitted.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.WaitForCompletedAsync(alpha.Id);
        await harness.WaitForCompletedAsync(unmapped.Id);
        // The datasource that cannot map cache files is not scanned but still gets its refreshes.
        Assert.Equal(1, harness.Scans);
        Assert.Equal(2, harness.CorruptionContexts.Opened);
        Assert.True(harness.DetectionRefreshes >= 1);
        Assert.Equal(0, harness.State.GetLogPosition("unmapped"));
        Assert.Equal(0, harness.State.GetLogPosition("alpha"));
        Assert.Equal(2, harness.DownloadsRefreshes);
        // The row ends without a red failure (a reaped row is gone, which also draws nothing red).
        await WaitForAsync(() => harness.Repairs.Tracker.GetRuns().Runs
            .SingleOrDefault(run => run.OperationId == alpha.Id)?.Repairing != true);
        Assert.Null(harness.Repairs.Tracker.GetRuns().Runs
            .SingleOrDefault(run => run.OperationId == alpha.Id)?.RepairError);
        await using (var check = harness.CreateContext())
        {
            // The clear's direct evict never runs for a full repair: it has no receipt to trust.
            Assert.False(Assert.Single(check.Downloads).IsEvicted);
        }

        // A cache clear starts once the full repairs are done.
        var clear = NewCacheClearingRepair(Path.Combine(root, "alpha-cache"));
        await harness.Owner.PrepareRepairAsync(clear, CancellationToken.None);
        await harness.Owner.StartWorkAsync(clear.Id, "alpha", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FullRepairFailsOutAndRetryRunsItAgainAsync()
    {
        var root = Path.Combine(_root, "full-repair-retry");
        await using var harness = await DispatchHarness.CreateAsync(root, start: false);
        var path = RepairFilePath(Path.Combine(root, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-json");
        harness.FailScans = 3;

        await harness.Owner.StartAsync(CancellationToken.None);
        var fullRepair = Assert.Single(harness.Owner.GetPendingRepairs());
        await WaitForAsync(() => harness.Repairs.Tracker.GetRuns().Runs
            .SingleOrDefault(run => run.OperationId == fullRepair.Id)?.RepairError is not null);

        var failed = harness.Repairs.Tracker.GetRuns().Runs.Single(run => run.OperationId == fullRepair.Id);
        Assert.False(failed.Repairing);
        Assert.True(failed.FullRepair);
        Assert.Equal("Injected cache scan failure.", failed.RepairError);
        Assert.Null(harness.Owner.GetBlockingRepair());

        harness.HoldScans = true;
        Assert.True(await harness.Owner.RetryRepairAsync(fullRepair.Id));
        var retried = harness.Repairs.Tracker.GetRuns().Runs.Single(run => run.OperationId == fullRepair.Id);
        Assert.True(retried.Repairing);
        Assert.Null(retried.RepairError);
        harness.ScanRelease.TrySetResult();

        await harness.WaitForCompletedAsync(fullRepair.Id);
        await WaitForAsync(() => harness.Repairs.Tracker.GetRuns().Runs
            .SingleOrDefault(run => run.OperationId == fullRepair.Id)?.Repairing != true);
        Assert.Equal(4, harness.Scans);
    }

    [Fact]
    public async Task OlderFileWithOnlyCompletedRowsStartsWithoutAFullRepairAsync()
    {
        var path = RepairFilePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var completed = NewLogProcessingRepair(phase: OperationRepairPhase.Completed);
        completed.Outcome = OperationStatus.Completed;
        completed.CompletedAt = DateTime.UtcNow;
        var rows = JsonSerializer.SerializeToNode(new[] { completed })!.AsArray();
        rows[0]![nameof(OperationRepair.Version)] = OperationRepair.CurrentVersion - 1;
        File.WriteAllText(path, rows.ToJsonString());

        await using var harness = await RepairHarness.CreateAsync(_root);

        Assert.Empty(harness.Owner.GetPendingRepairs());
        Assert.Empty(harness.StateService.LoadOperationRepairs());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "operation_repairs.json.unreadable-*"));
    }

    [Fact]
    public async Task FailedPreparationWritePublishesNoRepairOrWorkCheckpointAsync()
    {
        var state = CreateFailingStateService(_root);
        await using var harness = await RepairHarness.CreateAsync(_root, stateService: state);
        var repair = NewLogProcessingRepair();

        state.FailNextRepairWrite = true;
        await Assert.ThrowsAsync<IOException>(
            () => harness.Owner.PrepareRepairAsync(repair, CancellationToken.None));

        Assert.Empty(harness.Owner.GetPendingRepairs());
        Assert.False(File.Exists(RepairFilePath(_root)));

        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        var saved = Assert.Single(harness.Owner.GetPendingRepairs());
        Assert.Equal(OperationRepairPhase.Running, saved.Phase);
        Assert.True(saved.Sources.Single().NativeLaunchAuthorized);
    }

    [Fact]
    public async Task SequentialLogRepairsLeaveNoFullRowsAndKeepWriteSizeBoundedAsync()
    {
        var state = CreateFailingStateService(_root);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var applied = 0;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            apply: (_, _) =>
            {
                Interlocked.Increment(ref applied);
                return Task.CompletedTask;
            },
            registrations: services => services.AddSingleton<ISignalRNotificationService>(notifications));
        var writesPerPass = new List<int>();
        var largestWritePerPass = new List<int>();

        for (var index = 0; index < 4; index++)
        {
            var firstWrite = state.RepairWriteSizes.Count;
            var repair = NewLogProcessingRepair();
            await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
            await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
            await harness.Owner.FinishRepairAsync(repair.Id, true, false, null);

            var writes = state.RepairWriteSizes.Skip(firstWrite).ToList();
            writesPerPass.Add(writes.Count);
            largestWritePerPass.Add(writes.Max());
            Assert.Empty(state.LoadOperationRepairs());
            Assert.False(harness.Owner.OwnsRepair(repair.Id));
            await harness.Owner.FinishRepairAsync(repair.Id, false, true, "late cancellation");
        }

        Assert.All(writesPerPass, count => Assert.Equal(writesPerPass[0], count));
        Assert.InRange(largestWritePerPass.Max() - largestWritePerPass.Min(), 0, 64);
        Assert.Equal("[]", File.ReadAllText(RepairFilePath(_root)));
        Assert.Equal(0, applied);
        Assert.Equal(4, notifications.Events.Count(item => item.EventName == SignalREvents.DownloadsRefresh));
    }

    [Fact]
    public async Task StartupForgetsCompletedLogRowsWithoutRestoringTheirOwnersAsync()
    {
        var live = NewLogProcessingRepair(phase: OperationRepairPhase.Completed);
        live.Outcome = OperationStatus.Failed;
        live.Error = "live failure";
        live.CompletedAt = DateTime.UtcNow;
        live.Notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled);
        live.Sources.Single().NativeLaunchAuthorized = true;
        live.Sources.Single().NativeCompletionAccepted = true;

        var interactive = NewLogProcessingRepair(phase: OperationRepairPhase.Completed);
        interactive.Outcome = OperationStatus.Completed;
        interactive.CompletedAt = DateTime.UtcNow;
        interactive.Sources.Single().NativeLaunchAuthorized = true;
        interactive.Sources.Single().NativeCompletionAccepted = true;
        interactive.Sources.Single().RefreshDownloads = false;

        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        state.SaveOperationRepairs([live, interactive]);
        await using var harness = await RepairHarness.CreateAsync(_root, stateService: state);
        await harness.Owner.WaitForRecoveryOwnershipAsync(CancellationToken.None);

        Assert.Empty(state.LoadOperationRepairs());
        Assert.Equal(0, ((RepairOwner)harness.Owner).RestoreCalls);
        Assert.Empty(harness.Tracker.GetRuns().Runs);
        await harness.Owner.FinishRepairAsync(live.Id, false, false, "duplicate");
        await harness.Owner.FinishRepairAsync(interactive.Id, false, false, "duplicate");
    }

    [Fact]
    public async Task StartupSkipsCompletedOwnerAndRestoresUnfinishedOwnerOnceAsync()
    {
        var completed = NewCacheClearingRepair(Path.Combine(_root, "cache", "completed"));
        completed.Phase = OperationRepairPhase.Completed;
        completed.Outcome = OperationStatus.Failed;
        completed.Error = "original failure";
        completed.CompletedAt = DateTime.UtcNow;
        completed.Sources.Single().NativeLaunchAuthorized = true;

        var unfinished = NewLogProcessingRepair(phase: OperationRepairPhase.Running);
        unfinished.Sources.Single().NativeLaunchAuthorized = true;

        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        state.SaveOperationRepairs([completed, unfinished]);
        var entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using (var harness = await RepairHarness.CreateAsync(
            _root,
            start: false,
            stateService: state,
            onRestore: (operationId, stoppingToken) =>
            {
                entered.TrySetResult(operationId);
                release.Wait(stoppingToken);
            }))
        {
            Task? startup = null;
            Task? finish = null;
            Exception? assertionFailure = null;
            Exception? cleanupFailure = null;
            var terminal = new TaskCompletionSource<OperationInfo>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnTerminal(OperationInfo operation)
            {
                if (operation.Id == unfinished.Id)
                {
                    terminal.TrySetResult(operation);
                }
            }
            harness.Tracker.OperationTerminal += OnTerminal;
            try
            {
                startup = Task.Run(() => harness.Owner.StartAsync(CancellationToken.None));
                var restoring = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(unfinished.Id, restoring);

                var field = typeof(OperationStateService).GetField(
                    "_repairTasks",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var tasks = Assert.IsAssignableFrom<IDictionary>(field.GetValue(harness.Owner));
                Assert.True(tasks.Contains(unfinished.Id));

                finish = Task.Run(() => harness.Owner.FinishRepairAsync(
                    unfinished.Id,
                    false,
                    false,
                    "duplicate"));
            }
            catch (Exception ex)
            {
                assertionFailure = ex;
            }
            finally
            {
                release.Set();
                try
                {
                    if (finish is null)
                    {
                        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        finish = Task.Run(() => harness.Owner.FinishRepairAsync(
                            unfinished.Id,
                            false,
                            false,
                            "duplicate"));
                    }
                    if (startup is null)
                    {
                        await finish.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        await Task.WhenAll(startup, finish).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                finally
                {
                    harness.Tracker.OperationTerminal -= OnTerminal;
                }
            }
            if (assertionFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(assertionFailure).Throw();
            }
            if (cleanupFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }

            Assert.Equal(1, ((RepairOwner)harness.Owner).RestoreCalls);
            Assert.Null(harness.Tracker.GetOperation(completed.Id));
            var restored = Assert.IsType<OperationInfo>(harness.Tracker.GetOperation(unfinished.Id));
            Assert.Equal(OperationStatus.Failed, restored.Status);
            Assert.Equal("Operation interrupted by application restart", restored.Message);
        }

        await using var repeated = await RepairHarness.CreateAsync(
            _root,
            stateService: CreateStateService(_root));
        await repeated.Owner.WaitForRecoveryOwnershipAsync(CancellationToken.None);
        Assert.Equal(0, ((RepairOwner)repeated.Owner).RestoreCalls);
        Assert.Null(repeated.Tracker.GetOperation(completed.Id));
        var retained = Assert.Single(repeated.StateService.LoadOperationRepairs());
        Assert.Equal(completed.Id, retained.Id);
        Assert.Equal(OperationRepairPhase.Completed, retained.Phase);
    }

    [Fact]
    public async Task LogRefreshSatisfactionIsAcceptedOnlyAfterNativeCompletionAsync()
    {
        await using var harness = await RepairHarness.CreateAsync(_root);

        var accepted = NewLogProcessingRepair();
        await harness.Owner.PrepareRepairAsync(accepted, CancellationToken.None);
        await harness.Owner.StartWorkAsync(accepted.Id, "alpha", CancellationToken.None);
        await harness.Owner.SaveRepairAsync(
            accepted.Id,
            repair =>
            {
                var source = repair.Sources.Single();
                source.NativeCompletionAccepted = true;
                source.RefreshDownloads = false;
            },
            CancellationToken.None);
        Assert.False(Assert.Single(harness.Owner.GetPendingRepairs(), repair => repair.Id == accepted.Id)
            .Sources.Single().RefreshDownloads);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Owner.SaveRepairAsync(
                accepted.Id,
                repair => repair.Sources.Single().RefreshDownloads = true,
                CancellationToken.None));

        var early = NewLogProcessingRepair();
        await harness.Owner.PrepareRepairAsync(early, CancellationToken.None);
        await harness.Owner.StartWorkAsync(early.Id, "alpha", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Owner.SaveRepairAsync(
                early.Id,
                repair => repair.Sources.Single().RefreshDownloads = false,
                CancellationToken.None));

        var changed = NewLogProcessingRepair();
        await harness.Owner.PrepareRepairAsync(changed, CancellationToken.None);
        await harness.Owner.StartWorkAsync(changed.Id, "alpha", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Owner.SaveRepairAsync(
                changed.Id,
                repair =>
                {
                    var source = repair.Sources.Single();
                    source.NativeCompletionAccepted = true;
                    source.RefreshDownloads = false;
                    source.LogRoot = "logs/replaced";
                },
                CancellationToken.None));

        var otherFamily = NewCacheClearingRepair(Path.Combine(_root, "cache", "scope"));
        otherFamily.Sources.Single().RefreshDownloads = true;
        await harness.Owner.PrepareRepairAsync(otherFamily, CancellationToken.None);
        await harness.Owner.StartWorkAsync(otherFamily.Id, "alpha", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Owner.SaveRepairAsync(
                otherFamily.Id,
                repair =>
                {
                    var source = repair.Sources.Single();
                    source.NativeCompletionAccepted = true;
                    source.RefreshDownloads = false;
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task SourceCheckpointsAndDepotSetFollowOneWayRulesAsync()
    {
        await using var harness = await RepairHarness.CreateAsync(_root);
        var steam = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(steam, CancellationToken.None);
        await harness.Owner.StartWorkAsync(steam.Id, "alpha", CancellationToken.None);
        OperationRepairSource StoredSource(Guid operationId) =>
            harness.Owner.GetPendingRepairs().Single(item => item.Id == operationId).Sources.Single();

        await harness.Owner.MarkLogRewriteStartedAsync(steam.Id, "ALPHA");
        Assert.True(StoredSource(steam.Id).LogRewriteStarted);
        Assert.False(StoredSource(steam.Id).LogPositionsKept);
        await harness.Owner.MarkLogPositionsKeptAsync(steam.Id, "alpha");
        Assert.True(StoredSource(steam.Id).LogPositionsKept);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Owner.MarkLogRewriteStartedAsync(steam.Id, "beta"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Owner.SaveRepairAsync(
            steam.Id,
            next => next.Sources.Single().LogRewriteStarted = false,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Owner.SaveRepairAsync(
            steam.Id,
            next => next.Sources.Single().LogPositionsKept = false,
            CancellationToken.None));

        await harness.Owner.SaveRepairAsync(
            steam.Id,
            next => next.Target!.SteamDepotIds = [481, 482],
            CancellationToken.None);
        await harness.Owner.SaveRepairAsync(
            steam.Id,
            next => next.Target!.SteamDepotIds = [481, 482],
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Owner.SaveRepairAsync(
            steam.Id,
            next => next.Target!.SteamDepotIds = [481],
            CancellationToken.None));

        var corruption = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.CorruptionRemoval,
            Name = "Corruption removal",
            StartedAt = DateTime.UtcNow,
            Sources = [Source("alpha")],
            Target = new CacheRepairTarget { Service = "steam" },
            Corruption = new CorruptionRepair
            {
                ScanId = Guid.NewGuid(),
                ContractVersion = 1,
                DetectionMethod = CorruptionDetectionMethod.RepeatedMiss,
                Service = "steam"
            },
            Removal = new RemovalRepair
            {
                EntityKey = "steam",
                EntityName = "steam",
                EntityKind = "service",
                DetectionMethod = CorruptionDetectionMethod.RepeatedMiss
            }
        };
        await harness.Owner.PrepareRepairAsync(corruption, CancellationToken.None);
        await harness.Owner.StartWorkAsync(corruption.Id, "alpha", CancellationToken.None);
        await harness.Owner.SaveRepairAsync(
            corruption.Id,
            next =>
            {
                next.Sources.Single().NativeCompletionAccepted = true;
                next.Sources.Single().CorruptionCounts = new CorruptionRemovalCounts { UrlsRemoved = 2, FilesDeleted = 2 };
            },
            CancellationToken.None);
        await harness.Owner.SaveRepairAsync(
            corruption.Id,
            next =>
            {
                var counts = next.Sources.Single().CorruptionCounts!;
                counts.LogLinesRemoved = 5;
                counts.LogEntriesDeleted = 4;
                counts.DownloadsDeleted = 1;
            },
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Owner.SaveRepairAsync(
            corruption.Id,
            next => next.Sources.Single().CorruptionCounts!.FilesDeleted = 3,
            CancellationToken.None));

        await harness.Owner.MarkLogPositionsKeptAsync(corruption.Id, "alpha");
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Owner.SaveRepairAsync(
            corruption.Id,
            next => next.Sources.Single().CorruptionCounts!.LogLinesRemoved = 6,
            CancellationToken.None));
        Assert.Equal(5, StoredSource(corruption.Id).CorruptionCounts!.LogLinesRemoved);
    }

    [Theory]
    [InlineData(OperationType.CacheClearing, false)]
    [InlineData(OperationType.CacheClearing, true)]
    [InlineData(OperationType.LogRemoval, false)]
    [InlineData(OperationType.LogRemoval, true)]
    public async Task RootDependentRepairAbstainsForChangedOrMissingDatasourceAsync(
        OperationType type,
        bool datasourceMissing)
    {
        var rowRoot = Path.Combine(
            _root,
            $"root-binding-{type}-{datasourceMissing}-{Guid.NewGuid():N}");
        var stateRoot = Path.Combine(rowRoot, "state");
        var capturedCacheRoot = Path.Combine(rowRoot, "captured-cache");
        var capturedLogRoot = Path.Combine(rowRoot, "captured-logs");
        var configuredCacheRoot = Path.Combine(rowRoot, "configured-cache");
        var configuredLogRoot = Path.Combine(rowRoot, "configured-logs");
        Directory.CreateDirectory(capturedCacheRoot);
        Directory.CreateDirectory(capturedLogRoot);
        Directory.CreateDirectory(configuredCacheRoot);
        Directory.CreateDirectory(configuredLogRoot);
        var capturedSentinel = Path.Combine(
            type == OperationType.CacheClearing ? capturedCacheRoot : capturedLogRoot,
            "captured-sentinel");
        var foreignSentinel = Path.Combine(
            type == OperationType.CacheClearing ? configuredCacheRoot : configuredLogRoot,
            "foreign-sentinel");
        File.WriteAllText(capturedSentinel, "captured");
        File.WriteAllText(foreignSentinel, "foreign");

        var configuredName = datasourceMissing ? "beta" : "alpha";
        var settings = new Dictionary<string, string?>
        {
            ["LanCache:DataSources:0:Name"] = configuredName,
            ["LanCache:DataSources:0:CachePath"] = configuredCacheRoot,
            ["LanCache:DataSources:0:LogPath"] = configuredLogRoot,
            ["LanCache:DataSources:0:Enabled"] = "true"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        IPathResolver paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = rowRoot;
        var datasources = new DatasourceService(
            configuration,
            paths,
            NullLogger<DatasourceService>.Instance);
        var capabilities = new DatasourceCapabilityService(datasources);
        var state = CreateStateService(stateRoot);
        state.SetLogPosition("alpha", 73);
        state.SetLogPosition("beta", 91);

        var source = new OperationRepairSource
        {
            Datasource = "alpha",
            CacheRoot = type == OperationType.CacheClearing ? capturedCacheRoot : null,
            LogRoot = type == OperationType.LogRemoval ? capturedLogRoot : null
        };
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = type,
            Name = type == OperationType.CacheClearing ? "Cache clear" : "Log removal",
            StartedAt = DateTime.UtcNow,
            Sources = [source],
            CacheClearing = type == OperationType.CacheClearing
                ? new CacheClearingRepair { EntityKey = "bulk" }
                : null,
            LogRemoval = type == OperationType.LogRemoval
                ? new LogRemovalRepair { Service = "steam", Datasource = "alpha" }
                : null
        };
        var retries = 0;
        var log = new CapturingLogger<OperationStateService>();
        RepairOwner? owner = null;
        await using var harness = await RepairHarness.CreateAsync(
            stateRoot,
            start: false,
            stateService: state,
            apply: (pending, stoppingToken) => owner!.ApplyBaseAsync(pending, stoppingToken),
            waitUntil: (_, _) =>
            {
                Interlocked.Increment(ref retries);
                return Task.CompletedTask;
            },
            registrations: services => services
                .AddSingleton(datasources)
                .AddSingleton(capabilities)
                .AddSingleton((CacheClearingService)RuntimeHelpers.GetUninitializedObject(typeof(CacheClearingService)))
                .AddSingleton((CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(
                    typeof(CacheReconciliationService)))
                .AddSingleton((RustLogRemovalService)RuntimeHelpers.GetUninitializedObject(
                    typeof(RustLogRemovalService))),
            logger: log);
        owner = Assert.IsType<RepairOwner>(harness.Owner);

        await owner.StartAsync(CancellationToken.None);
        await owner.PrepareRepairAsync(repair, CancellationToken.None);
        await owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        await owner.FinishRepairAsync(
            repair.Id,
            success: false,
            cancelled: true,
            error: "requested cancellation").WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => state.LoadOperationRepairs().Single().Phase == OperationRepairPhase.Completed);

        // The changed or missing datasource abstains: nothing of it is touched, nothing fails and
        // nothing waits for a retry.
        var stored = Assert.Single(state.LoadOperationRepairs());
        Assert.Equal(OperationStatus.Cancelled, stored.Outcome);
        Assert.Null(stored.RetryAtUtc);
        Assert.True(Assert.Single(stored.Sources).NativeLaunchAuthorized);
        Assert.Equal(0, Volatile.Read(ref retries));
        Assert.Contains(
            log.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("alpha", StringComparison.Ordinal)
                && entry.Message.Contains(repair.Id.ToString(), StringComparison.Ordinal));
        Assert.Equal(73, state.GetLogPosition("alpha"));
        Assert.Equal(91, state.GetLogPosition("beta"));
        Assert.Equal("captured", File.ReadAllText(capturedSentinel));
        Assert.Equal("foreign", File.ReadAllText(foreignSentinel));
    }

    [Fact]
    public async Task CompletedClearOfAChangedCacheRootEvictsNothingAsync()
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "changed-clear"));
        var clearStarted = harness.Now;
        await using (var seed = harness.CreateContext())
        {
            seed.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.5",
                Datasource = "alpha",
                StartTimeUtc = clearStarted.AddHours(-2),
                EndTimeUtc = clearStarted.AddHours(-1),
                CacheHitBytes = 4096
            });
            await seed.SaveChangesAsync();
        }
        var clear = NewCacheClearingRepair(Path.Combine(_root, "changed-clear", "moved-cache"));
        clear.StartedAt = clearStarted;
        clear.Sources.Single().ReceiptPath = null;

        await harness.RunAsync(clear, OperationStatus.Completed);
        await harness.WaitForCompletedAsync(clear.Id);

        await using var check = harness.CreateContext();
        Assert.False(Assert.Single(check.Downloads).IsEvicted);
        Assert.Equal(0, harness.Scans);
    }

    [Fact]
    public async Task ConcurrentRepairsQuiesceAndRunSeriallyAsync()
    {
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var calls = 0;
        var outcomes = new List<OperationStatus?>();

        await using var harness = await RepairHarness.CreateAsync(
            _root,
            apply: async (repair, cancellationToken) =>
            {
                var running = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, running);
                lock (outcomes)
                {
                    outcomes.Add(repair.Outcome);
                }
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                Interlocked.Decrement(ref active);
            });

        var first = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        var second = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" });
        await harness.Owner.PrepareRepairAsync(first, CancellationToken.None);
        await harness.Owner.PrepareRepairAsync(second, CancellationToken.None);
        await harness.Owner.StartWorkAsync(first.Id, "alpha", CancellationToken.None);
        await harness.Owner.StartWorkAsync(second.Id, "alpha", CancellationToken.None);

        await harness.Owner.FinishRepairAsync(first.Id, true, false, null);
        await harness.Owner.FinishRepairAsync(second.Id, false, true, "cancelled");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref active));
        releaseFirst.TrySetResult(true);
        await WaitForAsync(() => harness.Owner.GetPendingRepairs().Count == 0);

        Assert.Equal(2, calls);
        Assert.Equal(1, maximum);
        Assert.All(
            harness.StateService.LoadOperationRepairs(),
            repair => Assert.Equal(OperationRepairPhase.Completed, repair.Phase));
        Assert.Contains(OperationStatus.Completed, outcomes);
        Assert.Contains(OperationStatus.Cancelled, outcomes);
    }

    [Fact]
    public async Task DuplicateFinishCallersReturnWhileOneRepairRunsAsync()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        OperationStatus? acceptedOutcome = null;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            apply: async (accepted, cancellationToken) =>
            {
                acceptedOutcome = accepted.Outcome;
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            });
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        await harness.Owner.FinishRepairAsync(repair.Id, true, false, null).WaitAsync(TimeSpan.FromSeconds(5));
        var stored = Assert.Single(harness.StateService.LoadOperationRepairs());
        Assert.Equal(OperationRepairPhase.Repairing, stored.Phase);
        Assert.Equal(OperationStatus.Completed, stored.Outcome);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Owner.FinishRepairAsync(repair.Id, false, true, "late cancellation")
            .WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult(true);
        await WaitForAsync(() => harness.StateService.LoadOperationRepairs().Single().Phase
            == OperationRepairPhase.Completed);

        Assert.Equal(1, calls);
        Assert.Equal(OperationStatus.Completed, acceptedOutcome);
        Assert.Equal(OperationStatus.Completed, harness.StateService.LoadOperationRepairs().Single().Outcome);
        await harness.Owner.FinishRepairAsync(repair.Id, false, true, "late cancellation");
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Owner.FinishRepairAsync(Guid.NewGuid(), true, false, null));
    }

    [Fact]
    public async Task FailedOutcomeSaveEndsOwnerAndLandsInBackgroundAsync()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var state = CreateFailingStateService(_root);
        var retryEntered = new SemaphoreSlim(0);
        var retryRelease = new SemaphoreSlim(0);
        var retryTimes = new List<DateTime>();
        var calls = 0;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            now: () => now,
            waitUntil: async (expiry, cancellationToken) =>
            {
                retryTimes.Add(expiry);
                retryEntered.Release();
                await retryRelease.WaitAsync(cancellationToken);
            },
            apply: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });
        var cleared = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.BlockerCleared += () => cleared.TrySetResult(true);
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        repair.Id = harness.Tracker.RegisterOperation(
            OperationType.GameRemoval,
            repair.Name,
            new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        state.FailRepairStarts = 3;
        await harness.Owner.FinishRepairAsync(repair.Id, false, false, "native failure")
            .WaitAsync(TimeSpan.FromSeconds(5));
        harness.Tracker.CompleteOperation(repair.Id, success: false, error: "native failure");

        var row = Assert.IsType<OperationInfo>(harness.Tracker.GetOperation(repair.Id));
        Assert.True(row.Repairing);
        Assert.Equal(OperationStatus.Failed, row.Status);
        Assert.Equal(repair.Id, harness.Owner.GetBlockingRepair()?.Id);
        Assert.Equal(OperationRepairPhase.Running, harness.Owner.GetBlockingRepair()?.Phase);

        var queued = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" });
        await harness.Owner.PrepareRepairAsync(queued, CancellationToken.None);
        var queuedStart = harness.Owner.StartWorkAsync(queued.Id, "alpha", CancellationToken.None);

        for (var failedRetry = 0; failedRetry < 2; failedRetry++)
        {
            Assert.True(await retryEntered.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(repair.Id, harness.Owner.GetBlockingRepair()?.Id);
            Assert.False(queuedStart.IsCompleted);
            Assert.False(cleared.Task.IsCompleted);
            Assert.Equal(0, calls);
            retryRelease.Release();
        }

        Assert.True(await retryEntered.WaitAsync(TimeSpan.FromSeconds(5)));
        retryRelease.Release();
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queuedStart.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([now.AddMinutes(1), now.AddMinutes(1), now.AddMinutes(1)], retryTimes);
        var stored = harness.StateService.LoadOperationRepairs().Single(item => item.Id == repair.Id);
        Assert.Equal(OperationRepairPhase.Completed, stored.Phase);
        Assert.Equal(OperationStatus.Failed, stored.Outcome);
        Assert.Equal("native failure", stored.Error);
        Assert.Equal(1, calls);
        Assert.Equal(
            OperationRepairPhase.Running,
            harness.StateService.LoadOperationRepairs().Single(item => item.Id == queued.Id).Phase);
        Assert.Null(harness.Owner.GetBlockingRepair());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceStopAndOwnerFailedSavesLandWithOwnerOutcomeAsync(bool forceStopFirst)
    {
        var state = CreateFailingStateService(_root);
        var retryEntered = new SemaphoreSlim(0);
        var retryRelease = new SemaphoreSlim(0);
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            waitUntil: async (_, cancellationToken) =>
            {
                retryEntered.Release();
                await retryRelease.WaitAsync(cancellationToken);
            });
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        state.FailRepairStarts = 2;
        Task FinishAsOwnerAsync() => harness.Owner.FinishRepairAsync(
            repair.Id,
            success: false,
            cancelled: false,
            error: "owner failure",
            update: next => next.Removal!.FilesDeleted = 3);
        if (forceStopFirst)
        {
            await harness.Owner.RecordForceStopAsync(repair.Id).WaitAsync(TimeSpan.FromSeconds(5));
            await FinishAsOwnerAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await FinishAsOwnerAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Owner.RecordForceStopAsync(repair.Id).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(OperationRepairPhase.Running, harness.Owner.GetBlockingRepair()?.Phase);

        Assert.True(await retryEntered.WaitAsync(TimeSpan.FromSeconds(5)));
        retryRelease.Release();
        await WaitForAsync(() => harness.StateService.LoadOperationRepairs().Single().Phase
            == OperationRepairPhase.Completed);

        var stored = harness.StateService.LoadOperationRepairs().Single();
        Assert.Equal(OperationStatus.Failed, stored.Outcome);
        Assert.Equal("owner failure", stored.Error);
        Assert.Equal(3, stored.Removal!.FilesDeleted);
        Assert.Empty(PrivateDictionary(harness.Owner, "_forceStoppedOwners"));
        await WaitForAsync(() => harness.Owner.GetBlockingRepair() is null);
    }

    [Fact]
    public async Task ForceStopSkipsPreparedRecordAndHoldsRunningRepairForOwnerAsync()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            apply: async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                await release.Task.WaitAsync(cancellationToken);
            });
        var prepared = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(prepared, CancellationToken.None);

        await harness.Owner.RecordForceStopAsync(prepared.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Owner.FinishRepairAsync(prepared.Id, false, true, null).WaitAsync(TimeSpan.FromSeconds(5));
        var skipped = Assert.Single(harness.StateService.LoadOperationRepairs());
        Assert.Equal(OperationRepairPhase.Completed, skipped.Phase);
        Assert.Equal(OperationStatus.Cancelled, skipped.Outcome);
        Assert.NotNull(skipped.CompletedAt);

        var running = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" });
        running.Id = harness.Tracker.RegisterOperation(
            OperationType.ServiceRemoval,
            running.Name,
            new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(running, CancellationToken.None);
        await harness.Owner.StartWorkAsync(running.Id, "alpha", CancellationToken.None);
        var forceStopped = PrivateDictionary(harness.Owner, "_forceStoppedOwners");

        await harness.Owner.RecordForceStopAsync(running.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var stored = harness.StateService.LoadOperationRepairs().Single(item => item.Id == running.Id);
        Assert.Equal(OperationRepairPhase.Repairing, stored.Phase);
        Assert.Equal(OperationStatus.Cancelled, stored.Outcome);
        Assert.True(forceStopped.Contains(running.Id));
        Assert.True(Assert.IsType<OperationInfo>(harness.Tracker.GetOperation(running.Id)).Repairing);

        await harness.Owner.RecordForceStopAsync(running.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(forceStopped.Contains(running.Id));

        await harness.Owner.FinishRepairAsync(running.Id, false, false, "owner failure")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(forceStopped.Contains(running.Id));
        release.TrySetResult(true);
        await WaitForAsync(() => harness.StateService.LoadOperationRepairs()
            .Single(item => item.Id == running.Id).Phase == OperationRepairPhase.Completed);
        Assert.Equal(
            OperationStatus.Cancelled,
            harness.StateService.LoadOperationRepairs().Single(item => item.Id == running.Id).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedCheckpointWriteIsCarriedByOutcomeSaveAsync()
    {
        var state = CreateFailingStateService(_root);
        OperationRepair? applied = null;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            apply: (accepted, _) =>
            {
                applied = accepted;
                return Task.CompletedTask;
            });
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        state.FailNextRepairWrite = true;
        await Assert.ThrowsAsync<IOException>(() => harness.Owner.SaveRepairAsync(
            repair.Id,
            next =>
            {
                next.Sources.Single().NativeCompletionAccepted = true;
                next.Target!.SteamDepotIds = [481];
                next.Removal!.FilesDeleted = 4;
            },
            CancellationToken.None));
        Assert.False(Assert.Single(state.LoadOperationRepairs()).Sources.Single().NativeCompletionAccepted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Owner.SaveRepairAsync(
            repair.Id,
            next => next.Sources.Single().LogRoot = "logs/replaced",
            CancellationToken.None));

        await harness.Owner.FinishRepairAsync(repair.Id, false, false, "native failure");
        var stored = Assert.Single(state.LoadOperationRepairs());
        Assert.Equal(OperationStatus.Failed, stored.Outcome);
        Assert.True(stored.Sources.Single().NativeCompletionAccepted);
        Assert.Equal("logs/alpha", stored.Sources.Single().LogRoot);
        Assert.Equal([481u], stored.Target!.SteamDepotIds);
        Assert.Equal(4, stored.Removal!.FilesDeleted);

        await WaitForAsync(() => applied is not null);
        Assert.Equal(OperationStatus.Failed, applied!.Outcome);
        Assert.True(applied.Sources.Single().NativeCompletionAccepted);
        Assert.False(applied.Sources.Single().LogRewriteStarted);
        Assert.Empty(PrivateDictionary(harness.Owner, "_unsavedChanges"));
    }

    [Fact]
    public async Task PendingLogPassOutcomeHoldsConflictingStartUntilItLandsAsync()
    {
        var state = CreateFailingStateService(_root);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var retryEntered = new SemaphoreSlim(0);
        var retryRelease = new SemaphoreSlim(0);
        var calls = 0;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            waitUntil: async (_, cancellationToken) =>
            {
                retryEntered.Release();
                await retryRelease.WaitAsync(cancellationToken);
            },
            apply: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            registrations: services => services.AddSingleton<ISignalRNotificationService>(notifications));
        var pass = NewLogProcessingRepair();
        pass.Id = harness.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            pass.Name,
            new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(pass, CancellationToken.None);
        await harness.Owner.StartWorkAsync(pass.Id, "alpha", CancellationToken.None);

        state.FailNextRepairWrite = true;
        await harness.Owner.FinishRepairAsync(pass.Id, true, false, null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(pass.Id, harness.Owner.GetBlockingRepair()?.Id);
        var row = Assert.IsType<OperationInfo>(harness.Tracker.GetOperation(pass.Id));
        Assert.True(row.Repairing);

        var removal = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(removal, CancellationToken.None);
        var removalStart = harness.Owner.StartWorkAsync(removal.Id, "alpha", CancellationToken.None);

        Assert.True(await retryEntered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(removalStart.IsCompleted);
        retryRelease.Release();
        await removalStart.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(harness.Owner.OwnsRepair(pass.Id));
        Assert.Equal(
            OperationRepairPhase.Running,
            Assert.Single(state.LoadOperationRepairs()).Phase);
        Assert.Equal(0, calls);
        Assert.Equal(1, notifications.Events.Count(item => item.EventName == SignalREvents.DownloadsRefresh));
        await WaitForAsync(() => !row.Repairing);
    }

    [Fact]
    public async Task RootReceiptRemainsUntilCompletedRepairIsPersistedAsync()
    {
        var state = CreateFailingStateService(_root);
        var retryStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedCompletionWrite = 0;
        var now = new DateTime(2026, 9, 30, 13, 0, 0, DateTimeKind.Utc);
        var cacheRoot = Path.Combine(_root, "cache", "alpha");
        var repair = NewCacheClearingRepair(cacheRoot);
        var receiptPath = repair.Sources.Single().ReceiptPath!;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            now: () => now,
            apply: (_, _) =>
            {
                if (Interlocked.Exchange(ref failedCompletionWrite, 1) == 0)
                {
                    state.FailNextRepairWrite = true;
                }
                return Task.CompletedTask;
            },
            waitUntil: async (retryAt, cancellationToken) =>
            {
                retryStarted.TrySetResult(true);
                await releaseRetry.Task.WaitAsync(cancellationToken);
                now = retryAt;
            });
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        WriteRootReceipt(receiptPath, repair.Id, cacheRoot);

        await harness.Owner.FinishRepairAsync(repair.Id, true, false, null);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(receiptPath));
        Assert.Equal(
            OperationRepairPhase.Repairing,
            Assert.Single(harness.Owner.GetPendingRepairs()).Phase);

        releaseRetry.TrySetResult(true);
        await WaitForAsync(() => !File.Exists(receiptPath));

        Assert.Equal(
            OperationRepairPhase.Completed,
            Assert.Single(harness.StateService.LoadOperationRepairs()).Phase);
    }

    [Fact]
    public async Task RootReceiptCleanupRetainsMismatchedProofAsync()
    {
        var cacheRoot = Path.Combine(_root, "cache", "alpha");
        var repair = NewCacheClearingRepair(cacheRoot);
        var receiptPath = repair.Sources.Single().ReceiptPath!;
        await using var harness = await RepairHarness.CreateAsync(_root);
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        WriteRootReceipt(receiptPath, Guid.NewGuid(), cacheRoot);

        await harness.Owner.FinishRepairAsync(repair.Id, true, false, null)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => harness.Owner.GetPendingRepairs().Count == 0);

        Assert.True(File.Exists(receiptPath));
        Assert.Equal(
            OperationRepairPhase.Completed,
            Assert.Single(harness.StateService.LoadOperationRepairs()).Phase);
    }

    [Fact]
    public async Task EvictionScanAttemptPointerCanAdvanceWithoutChangingRepairIdentityAsync()
    {
        await using var harness = await RepairHarness.CreateAsync(_root);
        var scanRepair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.EvictionScan,
            Name = "Eviction scan",
            StartedAt = DateTime.UtcNow,
            Sources = [Source("alpha")],
            EvictionScan = new EvictionScanRepair()
        };
        await harness.Owner.PrepareRepairAsync(scanRepair, CancellationToken.None);
        await harness.Owner.SaveRepairAsync(
            scanRepair.Id,
            saved => saved.EvictionScanId = scanRepair.Id,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Owner.SaveRepairAsync(
                scanRepair.Id,
                saved => saved.EvictionScanId = Guid.NewGuid(),
                CancellationToken.None));

        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.CacheClearing,
            Name = "Cache clear",
            StartedAt = DateTime.UtcNow,
            Sources = [Source("alpha")],
            CacheClearing = new CacheClearingRepair { EntityKey = "bulk" }
        };
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        var firstAttempt = Guid.NewGuid();
        var secondAttempt = Guid.NewGuid();

        await harness.Owner.SaveRepairAsync(
            repair.Id,
            saved => saved.EvictionScanId = firstAttempt,
            CancellationToken.None);
        await harness.Owner.SaveRepairAsync(
            repair.Id,
            saved => saved.EvictionScanId = secondAttempt,
            CancellationToken.None);

        var stored = harness.Owner.GetPendingRepairs().Single(item => item.Id == repair.Id);
        Assert.Equal(repair.Id, stored.Id);
        Assert.Equal(secondAttempt, stored.EvictionScanId);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Owner.SaveRepairAsync(
                repair.Id,
                saved => saved.EvictionScanId = repair.Id,
                CancellationToken.None));

        var legacy = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.CacheClearing,
            Name = "Legacy cache clear",
            StartedAt = DateTime.UtcNow,
            Sources = [Source("beta")],
            CacheClearing = new CacheClearingRepair { EntityKey = "bulk" }
        };
        legacy.EvictionScanId = legacy.Id;
        await harness.Owner.PrepareRepairAsync(legacy, CancellationToken.None);
        var replacementAttempt = Guid.NewGuid();
        await harness.Owner.SaveRepairAsync(
            legacy.Id,
            saved => saved.EvictionScanId = replacementAttempt,
            CancellationToken.None);
        Assert.Equal(
            replacementAttempt,
            harness.Owner.GetPendingRepairs().Single(item => item.Id == legacy.Id).EvictionScanId);
    }

    [Fact]
    public async Task RestartWaitsForPriorProcessAndPreservesInterruptedOutcomeAsync()
    {
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        var repair = NewLogProcessingRepair();
        repair.Phase = OperationRepairPhase.Running;
        repair.Sources.Single().NativeLaunchAuthorized = true;
        state.SaveOperationRepairs([repair]);

        var processManager = new WaitingProcessManager();
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: CreateStateService(_root),
            processManager: processManager,
            apply: (_, _) =>
            {
                applied.TrySetResult(true);
                return Task.CompletedTask;
            });

        Task? finish = null;
        Exception? assertionFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            await processManager.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(applied.Task.IsCompleted);
            Assert.Contains("log_processor", processManager.ProcessNames);

            var pending = Assert.Single(harness.StateService.LoadOperationRepairs());
            Assert.Equal(repair.Id, pending.Id);
            Assert.Equal(OperationRepairPhase.Repairing, pending.Phase);
            Assert.Equal(OperationStatus.Failed, pending.Outcome);
            Assert.Equal("Operation interrupted by application restart", pending.Error);

            finish = harness.Owner.FinishRepairAsync(repair.Id, false, false, "ignored duplicate");
            await finish.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(applied.Task.IsCompleted);
        }
        catch (Exception ex)
        {
            assertionFailure = ex;
        }
        finally
        {
            processManager.Release.TrySetResult(true);
            try
            {
                finish ??= harness.Owner.FinishRepairAsync(repair.Id, false, false, "ignored duplicate");
                await finish.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
        }
        if (assertionFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(assertionFailure).Throw();
        }
        if (cleanupFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        Assert.True(await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await WaitForAsync(() => !harness.Owner.OwnsRepair(repair.Id));
        var tracked = Assert.IsType<OperationInfo>(harness.Tracker.GetOperation(repair.Id));
        Assert.Equal(OperationStatus.Failed, tracked.Status);
        Assert.Equal("Operation interrupted by application restart", tracked.Message);
        Assert.Empty(harness.StateService.LoadOperationRepairs());
        Assert.False(harness.Owner.OwnsRepair(repair.Id));
    }

    [Fact]
    public void StartupProcessWaitCoversEveryAffectedExecutable()
    {
        var repairs = TypedRepairs();
        var waits = new List<(OperationRepair Repair, string[] Names)>
        {
            (repairs.Single(repair => repair.Type == OperationType.CacheClearing),
                ["cache_clear", "rsync", "cache_eviction_scan"]),
            (repairs.Single(repair => repair.Type == OperationType.LogProcessing),
                ["log_processor"]),
            (repairs.Single(repair => repair.Type == OperationType.LogRemoval),
                ["log_service_manager"]),
            (repairs.Single(repair => repair.Type == OperationType.GameRemoval),
                ["cache_steam_remove", "cache_purge_log_entries", "cache_eviction_scan"]),
            (NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { EpicGame = "fortnite" }),
                ["cache_epic_remove", "cache_purge_log_entries", "cache_eviction_scan"]),
            (NewRemovalRepair(
                    OperationType.GameRemoval,
                    new CacheRepairTarget { GameName = "Diablo IV", Service = "blizzard" }),
                ["cache_blizzard_remove", "cache_purge_log_entries", "cache_eviction_scan"]),
            (NewRemovalRepair(
                    OperationType.GameRemoval,
                    new CacheRepairTarget { GameName = "League of Legends", Service = "riot" }),
                ["cache_riot_remove", "cache_purge_log_entries", "cache_eviction_scan"]),
            (NewRemovalRepair(
                    OperationType.GameRemoval,
                    new CacheRepairTarget { GameName = "Halo Infinite", Service = "xbox" }),
                ["cache_xbox_remove", "cache_purge_log_entries", "cache_eviction_scan"]),
            (NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" }),
                ["cache_service_remove", "cache_purge_log_entries", "cache_eviction_scan"]),
            (repairs.Single(repair => repair.Type == OperationType.CorruptionRemoval),
                ["cache_corruption", "cache_eviction_scan"]),
            (repairs.Single(repair => repair.Type == OperationType.GameDetection),
                ["cache_game_detect"]),
            (repairs.Single(repair => repair.Type == OperationType.EvictionScan),
                ["cache_game_detect", "cache_eviction_scan"]),
            (repairs.Single(repair => repair.Type == OperationType.EvictionRemoval),
                ["cache_purge_log_entries", "cache_eviction_scan"])
        };

        foreach (var (repair, names) in waits)
        {
            Assert.Equal(names, OperationStateService.ProcessNames(repair));
        }
    }

    [Fact]
    public async Task ProcessDiscoveryErrorKeepsStartupRepairPendingAsync()
    {
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        var repair = NewLogProcessingRepair();
        repair.Phase = OperationRepairPhase.Running;
        repair.Sources.Single().NativeLaunchAuthorized = true;
        state.SaveOperationRepairs([repair]);
        var waitStarted = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: CreateStateService(_root),
            processManager: new ThrowingProcessManager(),
            waitUntil: async (expiry, cancellationToken) =>
            {
                waitStarted.TrySetResult(expiry);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            apply: (_, _) =>
            {
                applied = true;
                return Task.CompletedTask;
            });

        var retryAt = await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stored = Assert.Single(harness.StateService.LoadOperationRepairs());

        Assert.Equal(OperationRepairPhase.Repairing, stored.Phase);
        Assert.Equal(retryAt, stored.RetryAtUtc);
        Assert.False(applied);
    }

    [Fact]
    public void SharedRefreshUsesRetainedBoundaryAndPreservesExistingProducerOwnership()
    {
        var cacheClear = TypedRepairs().Single(repair => repair.Type == OperationType.CacheClearing);
        cacheClear.Sources.Single().NativeLaunchAuthorized = true;
        Assert.True(OperationStateService.NeedsDownloadsRefresh(cacheClear));

        var logProcessing = NewLogProcessingRepair();
        logProcessing.Phase = OperationRepairPhase.Repairing;
        logProcessing.Outcome = OperationStatus.Completed;
        logProcessing.Sources.Single().NativeLaunchAuthorized = true;
        Assert.True(OperationStateService.NeedsDownloadsRefresh(logProcessing));

        logProcessing.Sources.Single().RefreshDownloads = false;
        Assert.False(OperationStateService.NeedsDownloadsRefresh(logProcessing));

        var evictionScan = TypedRepairs().Single(repair => repair.Type == OperationType.EvictionScan);
        evictionScan.Sources.Single().NativeLaunchAuthorized = true;
        evictionScan.Sources.Single().RefreshDownloads = true;
        Assert.False(OperationStateService.NeedsDownloadsRefresh(evictionScan));

        var evictionRemoval = TypedRepairs().Single(repair => repair.Type == OperationType.EvictionRemoval);
        evictionRemoval.DatabaseWriteStarted = true;
        evictionRemoval.Sources = [Source("alpha")];
        evictionRemoval.Sources.Single().RefreshDownloads = true;
        Assert.True(OperationStateService.NeedsDownloadsRefresh(evictionRemoval));

        // A cache clear that wrote the database refreshes the Downloads views even when its
        // native step never launched for the source.
        var clearWrote = TypedRepairs().Single(repair => repair.Type == OperationType.CacheClearing);
        clearWrote.DatabaseWriteStarted = true;
        clearWrote.Sources.Single().NativeLaunchAuthorized = false;
        clearWrote.Sources.Single().RefreshDownloads = true;
        Assert.True(OperationStateService.NeedsDownloadsRefresh(clearWrote));
    }

    [Fact]
    public async Task StoredRetryTimeIsSharedAndApplicationStopLeavesRepairPendingAsync()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var retryAt = now.AddMinutes(1);
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        var repair = NewLogProcessingRepair();
        repair.Phase = OperationRepairPhase.Repairing;
        repair.Outcome = OperationStatus.Failed;
        repair.Error = "interrupted";
        repair.RetryAtUtc = retryAt;
        state.SaveOperationRepairs([repair]);

        var waitStarted = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: CreateStateService(_root),
            now: () => now,
            waitUntil: async (expiry, cancellationToken) =>
            {
                waitStarted.TrySetResult(expiry);
                await release.Task.WaitAsync(cancellationToken);
            },
            apply: (_, _) =>
            {
                applied = true;
                return Task.CompletedTask;
            });

        Assert.Equal(retryAt, await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(applied);
        harness.Lifetime.StopApplication();
        await harness.Owner.StopAsync(CancellationToken.None);

        var stored = Assert.Single(harness.StateService.LoadOperationRepairs());
        Assert.Equal(OperationRepairPhase.Repairing, stored.Phase);
        Assert.Equal(retryAt, stored.RetryAtUtc);
        Assert.False(applied);
    }

    [Fact]
    public async Task ForceKillEndsOwnerCompletionOperationAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var cancellation = new OperationCancellationService(
            tracker,
            processManager,
            OperationConflictTestServices.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var emitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationId = tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log processing",
            new CancellationTokenSource(),
            onTerminalEmit: _ =>
            {
                emitted.TrySetResult(true);
                return Task.CompletedTask;
            },
            ownerCompletes: true);

        Assert.True(await cancellation.ForceKillAsync(operationId));
        var ended = Assert.IsType<OperationInfo>(tracker.GetOperation(operationId));
        await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, ended.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelled, ended.Status);

        // The owner's own late completion changes nothing.
        tracker.CompleteOperation(operationId, success: true);
        Assert.Equal(OperationStatus.Cancelled, ended.Status);
    }

    [Fact]
    public async Task QueuedPromotionKeepsOperationIdentityAndTransfersTerminalOwnershipAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var cancellation = new OperationCancellationService(
            tracker,
            processManager,
            OperationConflictTestServices.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var emitted = 0;
        var operationId = tracker.RegisterOperation(
            OperationType.EvictionScan,
            "Eviction scan",
            new CancellationTokenSource(),
            initialStatus: OperationStatus.Waiting);

        Assert.True(tracker.BeginQueuedOperation(
            operationId,
            state: null,
            onTerminalCleanup: null,
            onTerminalEmit: _ =>
            {
                Interlocked.Increment(ref emitted);
                return Task.CompletedTask;
            },
            ownerCompletes: true));

        var promoted = Assert.IsType<OperationInfo>(tracker.GetOperation(operationId));
        Assert.Equal(operationId, promoted.Id);
        Assert.True(promoted.OwnerCompletes);
        Assert.Equal(OperationStatus.Running, promoted.Status);

        Assert.True(await cancellation.ForceKillAsync(operationId));
        Assert.Equal(1, promoted.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelled, promoted.Status);
        Assert.Equal(1, emitted);

        tracker.CompleteOperation(operationId, success: false, cancelled: true);
        Assert.Equal(1, emitted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadsRefreshOnlyLogPassCompletesWithoutARepairAsync(bool refreshSatisfied)
    {
        var state = CreateFailingStateService(_root);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var applied = 0;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            apply: (_, _) =>
            {
                Interlocked.Increment(ref applied);
                return Task.CompletedTask;
            },
            registrations: services => services.AddSingleton<ISignalRNotificationService>(notifications));
        var operationId = Guid.NewGuid();
        var pass = Assert.IsType<OperationRepair>(typeof(RustLogProcessorService)
            .GetMethod("BuildRepair", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [operationId, null, new[] { new OperationRepairSource
            {
                Datasource = "alpha",
                LogRoot = "logs/alpha",
                RefreshDownloads = true
            } }]));
        await harness.Owner.PrepareRepairAsync(pass, CancellationToken.None);
        await harness.Owner.StartWorkAsync(operationId, "alpha", CancellationToken.None);
        if (refreshSatisfied)
        {
            await harness.Owner.SaveRepairAsync(
                operationId,
                repair =>
                {
                    repair.Sources.Single().NativeCompletionAccepted = true;
                    repair.Sources.Single().RefreshDownloads = false;
                },
                CancellationToken.None);
        }

        // Any write of a Repairing record throws, so the pass must never write one.
        state.FailRepairStarts = int.MaxValue;
        await harness.Owner.FinishRepairAsync(operationId, true, false, null).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(state.LoadOperationRepairs());
        Assert.False(harness.Owner.OwnsRepair(operationId));
        Assert.True(PrivateDictionary(harness.Owner, "_completedRepairs").Contains(operationId));
        Assert.Empty(PrivateDictionary(harness.Owner, "_pendingOutcomes"));
        Assert.Empty(PrivateDictionary(harness.Owner, "_repairTasks"));
        Assert.Equal(0, applied);
        Assert.Equal(
            refreshSatisfied ? 0 : 1,
            notifications.Events.Count(item => item.EventName == SignalREvents.DownloadsRefresh));
    }

    [Fact]
    public async Task RepairAppliesBesideALiveLogPassAsync()
    {
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            apply: (_, _) =>
            {
                applied.TrySetResult(true);
                return Task.CompletedTask;
            },
            registrations: services => services.AddSingleton<ISignalRNotificationService>(
                new ScheduledRunReporterTests.CapturingNotificationService()));
        var pass = NewLogProcessingRepair();
        await harness.Owner.PrepareRepairAsync(pass, CancellationToken.None);
        await harness.Owner.StartWorkAsync(pass.Id, "alpha", CancellationToken.None);

        var removal = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(removal, CancellationToken.None);
        await harness.Owner.StartWorkAsync(removal.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(removal.Id, false, true, null);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationRepairPhase.Running, harness.Owner.GetPendingRepairs().Single(item => item.Id == pass.Id).Phase);

        var detection = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.GameDetection,
            Name = "Game Detection",
            StartedAt = DateTime.UtcNow,
            GameDetection = new GameDetectionMetrics { StartTime = DateTime.UtcNow }
        };
        await harness.Owner.PrepareRepairAsync(detection, CancellationToken.None);
        await harness.Owner.StartWorkAsync(detection.Id, null, CancellationToken.None);
        await harness.Owner.FinishRepairAsync(pass.Id, true, false, null).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PreparedStartsQueueOnlyBehindRepairsThatStillRunAsync()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Guid failing = Guid.Empty;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            now: () => now,
            waitUntil: (retryAt, _) =>
            {
                now = retryAt > now ? retryAt : now;
                return Task.CompletedTask;
            },
            apply: async (repair, cancellationToken) =>
            {
                if (repair.Id == failing)
                {
                    throw new IOException("Injected repair failure.");
                }
                await release.Task.WaitAsync(cancellationToken);
            },
            registrations: services => services.AddSingleton<ISignalRNotificationService>(
                new ScheduledRunReporterTests.CapturingNotificationService()));
        var repairing = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(repairing, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repairing.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(repairing.Id, false, true, null);
        await using var step = await harness.Owner.LockLogFilesAsync(
            Guid.NewGuid(),
            OperationType.GameRemoval,
            LogFileLockKind.Rows,
            CancellationToken.None);

        var pass = NewLogProcessingRepair();
        await harness.Owner.PrepareRepairAsync(pass, CancellationToken.None);
        await harness.Owner.StartWorkAsync(pass.Id, "alpha", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var queued = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" });
        await harness.Owner.PrepareRepairAsync(queued, CancellationToken.None);
        var queuedStart = harness.Owner.StartWorkAsync(queued.Id, "alpha", CancellationToken.None);
        Assert.False(queuedStart.IsCompleted);
        release.TrySetResult(true);
        await queuedStart.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Owner.FinishRepairAsync(pass.Id, true, false, null);
        await harness.Owner.FinishRepairAsync(queued.Id, true, false, null);
        await WaitForAsync(() => harness.Owner.GetBlockingRepair() is null);

        var failed = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 570 });
        failing = failed.Id;
        await harness.Owner.PrepareRepairAsync(failed, CancellationToken.None);
        await harness.Owner.StartWorkAsync(failed.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(failed.Id, false, true, null);
        await WaitForAsync(() => harness.Owner.GetBlockingRepair() is null
            && !PrivateDictionary(harness.Owner, "_repairTasks").Contains(failed.Id));
        var next = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "epicgames" });
        await harness.Owner.PrepareRepairAsync(next, CancellationToken.None);
        await harness.Owner.StartWorkAsync(next.Id, "alpha", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RepairThatFailsThreeTimesFailsOutUntilRetriedAsync()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var failures = 3;
        var applies = 0;
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        await using (var harness = await RepairHarness.CreateAsync(
                         _root,
                         stateService: state,
                         now: () => now,
                         waitUntil: (retryAt, _) =>
                         {
                             now = retryAt > now ? retryAt : now;
                             return Task.CompletedTask;
                         },
                         apply: (_, _) =>
                         {
                             Interlocked.Increment(ref applies);
                             return Volatile.Read(ref failures) > 0 && Interlocked.Decrement(ref failures) >= 0
                                 ? Task.FromException(new IOException("Injected repair failure."))
                                 : Task.CompletedTask;
                         }))
        {
            var owner = (RepairOwner)harness.Owner;
            var controller = new OperationsController(
                harness.Tracker,
                new OperationCancellationService(
                    harness.Tracker,
                    new ProcessManager(NullLogger<ProcessManager>.Instance),
                    owner,
                    NullLogger<OperationCancellationService>.Instance),
                owner);
            var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
            repair.Id = harness.Tracker.RegisterOperation(
                OperationType.GameRemoval,
                repair.Name,
                new CancellationTokenSource(),
                ownerCompletes: true);
            await owner.PrepareRepairAsync(repair, CancellationToken.None);
            await owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(await controller.RetryRepairAsync(repair.Id));

            await owner.FinishRepairAsync(repair.Id, false, true, null);
            harness.Tracker.CompleteOperation(repair.Id, success: false, cancelled: true);
            await WaitForAsync(() => harness.Tracker.GetOperation(repair.Id)?.RepairError is not null);

            Assert.Equal("Injected repair failure.", harness.Tracker.GetOperation(repair.Id)!.RepairError);
            Assert.False(harness.Tracker.GetOperation(repair.Id)!.Repairing);
            Assert.False(PrivateDictionary(owner, "_repairTasks").Contains(repair.Id));
            Assert.Null(owner.GetBlockingRepair());
            Assert.Equal(OperationRepairPhase.Repairing, Assert.Single(state.LoadOperationRepairs()).Phase);
            await owner.RunMaintenanceAsync(CancellationToken.None);
            await owner.FinishRepairAsync(repair.Id, false, true, null);
            Assert.Equal(3, applies);

            Assert.False(await owner.RetryRepairAsync(Guid.NewGuid()));
            var tasks = PrivateDictionary(owner, "_repairTasks");
            tasks[repair.Id] = new Lazy<Task>(Task.CompletedTask);
            Assert.False(await owner.RetryRepairAsync(repair.Id));
            tasks.Remove(repair.Id);
        }

        // A new owner over the same file runs a failed-out repair at start.
        var restarted = 0;
        await using (var next = await RepairHarness.CreateAsync(
                         _root,
                         stateService: CreateStateService(_root),
                         apply: (_, _) =>
                         {
                             Interlocked.Increment(ref restarted);
                             return Task.CompletedTask;
                         }))
        {
            await WaitForAsync(() => next.StateService.LoadOperationRepairs().Single().Phase
                == OperationRepairPhase.Completed);
            Assert.Equal(1, restarted);
        }

        // Retry in the same process runs the repair again.
        var retried = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" });
        failures = 3;
        await using var retryHarness = await RepairHarness.CreateAsync(
            Path.Combine(_root, "retry"),
            now: () => now,
            waitUntil: (retryAt, _) =>
            {
                now = retryAt > now ? retryAt : now;
                return Task.CompletedTask;
            },
            apply: (_, _) =>
            {
                Interlocked.Increment(ref applies);
                return Volatile.Read(ref failures) > 0 && Interlocked.Decrement(ref failures) >= 0
                    ? Task.FromException(new IOException("Injected repair failure."))
                    : Task.CompletedTask;
            });
        var retryController = new OperationsController(
            retryHarness.Tracker,
            new OperationCancellationService(
                retryHarness.Tracker,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                retryHarness.Owner,
                NullLogger<OperationCancellationService>.Instance),
            retryHarness.Owner);
        retried.Id = retryHarness.Tracker.RegisterOperation(
            OperationType.ServiceRemoval,
            retried.Name,
            new CancellationTokenSource(),
            ownerCompletes: true);
        await retryHarness.Owner.PrepareRepairAsync(retried, CancellationToken.None);
        await retryHarness.Owner.StartWorkAsync(retried.Id, "alpha", CancellationToken.None);
        await retryHarness.Owner.FinishRepairAsync(retried.Id, false, true, null);
        retryHarness.Tracker.CompleteOperation(retried.Id, success: false, cancelled: true);
        await WaitForAsync(() => retryHarness.Tracker.GetOperation(retried.Id)?.RepairError is not null);

        Assert.IsType<NoContentResult>(await retryController.RetryRepairAsync(retried.Id));
        Assert.Null(retryHarness.Tracker.GetOperation(retried.Id)!.RepairError);
        await WaitForAsync(() => retryHarness.StateService.LoadOperationRepairs().Single().Phase
            == OperationRepairPhase.Completed);
        await WaitForAsync(() => !retryHarness.Tracker.GetOperation(retried.Id)?.Repairing ?? true);
        Assert.IsType<NotFoundObjectResult>(await retryController.RetryRepairAsync(retried.Id));
        Assert.Equal(7, applies);
    }

    [Fact]
    public async Task RestoredRepairShowsRepairingUntilItEndsAsync()
    {
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        repair.Phase = OperationRepairPhase.Running;
        state.SaveOperationRepairs([repair]);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: CreateStateService(_root),
            apply: async (_, cancellationToken) =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var row = Assert.Single(harness.Tracker.GetRuns().Runs, run => run.OperationId == repair.Id);
        Assert.Equal("failed", row.Status);
        Assert.True(row.Repairing);
        Assert.False(row.FullRepair);

        release.TrySetResult(true);
        await WaitForAsync(() => !harness.Tracker.GetRuns().Runs.Single(run => run.OperationId == repair.Id).Repairing);
        Assert.Null(harness.Tracker.GetRuns().Runs.Single(run => run.OperationId == repair.Id).RepairError);
    }

    [Fact]
    public async Task MaintenanceSkipsTheDatabaseWhileSetupIsPendingAsync()
    {
        await using var harness = await RepairHarness.CreateAsync(_root, start: false);
        harness.Configuration["Runtime:DatabaseSetupPending"] = "true";
        var owner = (RepairOwner)harness.Owner;
        await owner.StartAsync(CancellationToken.None);

        await owner.RunMaintenanceAsync(CancellationToken.None);
        Assert.Equal(0, owner.PruneCalls);

        harness.Configuration["Runtime:DatabaseSetupPending"] = "false";
        await owner.RunMaintenanceAsync(CancellationToken.None);
        Assert.True(owner.PruneCalls > 0);
    }

    [Theory]
    [InlineData(OperationStatus.Completed)]
    [InlineData(OperationStatus.Failed)]
    [InlineData(OperationStatus.Cancelled)]
    public async Task GameDetectionRepairRefreshesOnlyAnUnfinishedRunAsync(OperationStatus outcome)
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "detection-" + outcome));
        var detection = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.GameDetection,
            Name = "Game Detection",
            StartedAt = DateTime.UtcNow,
            GameDetection = new GameDetectionMetrics { StartTime = DateTime.UtcNow, ScanType = DetectionScanType.Incremental }
        };
        await harness.Owner.PrepareRepairAsync(detection, CancellationToken.None);
        await harness.Owner.StartWorkAsync(detection.Id, null, CancellationToken.None);
        var refreshesBefore = harness.DetectionRefreshes;
        await harness.Owner.FinishRepairAsync(
            detection.Id,
            outcome == OperationStatus.Completed,
            outcome == OperationStatus.Cancelled,
            outcome == OperationStatus.Failed ? "failed" : null);
        await harness.WaitForCompletedAsync(detection.Id);

        var refreshed = outcome != OperationStatus.Completed;
        Assert.Equal(refreshed ? refreshesBefore + 1 : refreshesBefore, harness.DetectionRefreshes);
        Assert.Equal(refreshed ? 1 : 0, harness.DownloadsRefreshes);
        // A detection never clears the cache-file scan the Cache Files card shows.
        Assert.True(File.Exists(harness.CachedScanFile));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task ResetFollowsTheLogStepCheckpointsReadUnderTheLockAsync(
        bool logStepStarted,
        bool keptWhileWaiting,
        bool forceStopped)
    {
        await using var harness = await DispatchHarness.CreateAsync(
            Path.Combine(_root, $"reset-{logStepStarted}-{keptWhileWaiting}-{forceStopped}"));
        harness.State.SetLogPosition("alpha", 73);
        var removal = harness.NewRemoval(OperationType.GameRemoval);
        await harness.Owner.PrepareRepairAsync(removal, CancellationToken.None);
        await harness.Owner.StartWorkAsync(removal.Id, "alpha", CancellationToken.None);
        if (logStepStarted)
        {
            await harness.Owner.MarkLogRewriteStartedAsync(removal.Id, "alpha");
        }

        if (forceStopped)
        {
            await harness.Owner.RecordForceStopAsync(removal.Id);
            await harness.Owner.FinishRepairAsync(removal.Id, false, true, null);
        }
        else
        {
            var step = await harness.Owner.LockLogFilesAsync(
                Guid.NewGuid(),
                OperationType.GameRemoval,
                LogFileLockKind.Rows,
                CancellationToken.None);
            await harness.Owner.FinishRepairAsync(removal.Id, false, true, null);
            // The repair queues at the lock this step holds.
            await WaitForAsync(() => (int)typeof(OperationStateService)
                .GetField("_logStepWaiters", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(harness.Owner)! == 1);
            if (keptWhileWaiting)
            {
                await harness.Owner.MarkLogPositionsKeptAsync(removal.Id, "alpha");
            }
            await step.DisposeAsync();
        }

        await harness.WaitForCompletedAsync(removal.Id);
        Assert.Equal(logStepStarted && !keptWhileWaiting ? 0 : 73, harness.State.GetLogPosition("alpha"));
    }

    [Fact]
    public async Task CleanRepairNeverWaitsForTheLogLockAsync()
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "clean-no-lock"));
        await using var import = await harness.Owner.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        var removal = harness.NewRemoval(OperationType.ServiceRemoval);

        await harness.RunAsync(removal, OperationStatus.Completed, async operationId =>
        {
            await harness.Owner.SaveRepairAsync(
                operationId,
                repair => repair.Sources.Single().NativeCompletionAccepted = true,
                CancellationToken.None);
            await harness.Owner.MarkLogRewriteStartedAsync(operationId, "alpha");
            await harness.Owner.MarkLogPositionsKeptAsync(operationId, "alpha");
        });

        await harness.WaitForCompletedAsync(removal.Id);
        Assert.Equal(0, harness.Scans);
    }

    [Theory]
    [InlineData(OperationType.GameRemoval)]
    [InlineData(OperationType.ServiceRemoval)]
    [InlineData(OperationType.CorruptionRemoval)]
    public async Task CleanRemovalSkipsTheCacheScanAsync(OperationType type)
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "clean-" + type));
        var removal = CleanRemoval(harness, type);
        var refreshesBefore = harness.DetectionRefreshes;

        await harness.RunAsync(removal, OperationStatus.Completed, operationId => KeepEveryStepAsync(harness, operationId));
        var completed = await harness.WaitForCompletedAsync(removal.Id);

        Assert.Equal(OperationStatus.Completed, completed.Outcome);
        Assert.Equal(0, harness.Scans);
        Assert.Equal(refreshesBefore + 1, harness.DetectionRefreshes);
        Assert.Equal(1, harness.DownloadsRefreshes);
        Assert.True(harness.CorruptionContexts.Opened >= 2);
        Assert.False(File.Exists(harness.CachedScanFile));

        // A removal queued behind the repair starts once it ends.
        var queued = harness.NewRemoval(OperationType.GameRemoval);
        await harness.Owner.PrepareRepairAsync(queued, CancellationToken.None);
        await harness.Owner.StartWorkAsync(queued.Id, "alpha", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("source not accepted")]
    [InlineData("log step not kept")]
    [InlineData("structural")]
    public async Task UncleanRemovalStillScansOnceAsync(string variant)
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "unclean-" + variant.Replace(' ', '-')));
        var removal = CleanRemoval(
            harness,
            variant == "structural" ? OperationType.CorruptionRemoval : OperationType.GameRemoval,
            variant == "structural" ? CorruptionDetectionMethod.Structural : CorruptionDetectionMethod.RepeatedMiss);
        var outcome = variant switch
        {
            "cancelled" => OperationStatus.Cancelled,
            "failed" => OperationStatus.Failed,
            _ => OperationStatus.Completed
        };

        await harness.RunAsync(removal, outcome, async operationId =>
        {
            if (variant != "source not accepted")
            {
                await AcceptSourceAsync(harness, operationId);
            }
            await harness.Owner.MarkLogRewriteStartedAsync(operationId, "alpha");
            if (variant != "log step not kept")
            {
                await harness.Owner.MarkLogPositionsKeptAsync(operationId, "alpha");
            }
        });
        await harness.WaitForCompletedAsync(removal.Id);

        Assert.Equal(1, harness.Scans);
    }

    [Fact]
    public async Task RestoredCleanRemovalStillScansOnceAsync()
    {
        var root = Path.Combine(_root, "restored-clean");
        await using var harness = await DispatchHarness.CreateAsync(root, start: false);
        var removal = CleanRemoval(harness, OperationType.GameRemoval);
        removal.Phase = OperationRepairPhase.Repairing;
        removal.Outcome = OperationStatus.Completed;
        var source = removal.Sources.Single();
        source.NativeLaunchAuthorized = true;
        source.NativeCompletionAccepted = true;
        source.LogRewriteStarted = true;
        source.LogPositionsKept = true;
        harness.State.SaveOperationRepairs([removal]);

        await harness.Owner.StartAsync(CancellationToken.None);
        await harness.WaitForCompletedAsync(removal.Id);

        Assert.Equal(1, harness.Scans);
    }

    [Theory]
    [InlineData("no traffic", 0, true)]
    [InlineData("ended after the clear", 1, false)]
    [InlineData("running zero-byte", 1, false)]
    [InlineData("ended zero-byte", 0, true)]
    [InlineData("other datasource", 0, true)]
    public async Task CleanClearSkipsTheScanOnlyWithoutTrafficAsync(string traffic, int scans, bool evictsDirectly)
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "clear-" + traffic.Replace(' ', '-')));
        var clearStarted = harness.Now;
        await using (var seed = harness.CreateContext())
        {
            // Rows of the cleared datasource that ended before the clear, one of them stored with
            // the name in another case.
            seed.Downloads.Add(ClearDownload("Alpha", clearStarted.AddHours(-1), 4096));
            seed.Downloads.Add(ClearDownload("alpha", clearStarted.AddHours(-2), 2048));
            switch (traffic)
            {
                case "ended after the clear":
                    seed.Downloads.Add(ClearDownload("ALPHA", clearStarted.AddMinutes(1), 1024));
                    break;
                case "running zero-byte":
                    var running = ClearDownload("alpha", clearStarted.AddMinutes(1), 0);
                    running.IsActive = true;
                    seed.Downloads.Add(running);
                    break;
                case "ended zero-byte":
                    seed.Downloads.Add(ClearDownload("alpha", clearStarted.AddMinutes(1), 0));
                    break;
                case "other datasource":
                    seed.Downloads.Add(ClearDownload("beta", clearStarted.AddMinutes(1), 1024));
                    break;
            }
            await seed.SaveChangesAsync();
        }
        var clear = NewCacheClearingRepair(Path.Combine(harness.Root, "alpha-cache"));
        clear.StartedAt = clearStarted;
        var source = clear.Sources.Single();
        source.LogRoot = Path.Combine(harness.Root, "alpha-logs");
        source.KeyScheme = harness.Capabilities.GetKeySchemeWireValue(harness.Datasources.GetDatasource("alpha")!);
        source.ReceiptPath = Path.Combine(harness.Root, "alpha-cache", $".lancache-repair-{clear.Id:N}.json");
        WriteRootReceipt(source.ReceiptPath, clear.Id, source.CacheRoot!);
        source.RefreshDownloads = true;

        await harness.RunAsync(clear, OperationStatus.Completed, operationId => harness.Owner.SaveRepairAsync(
            operationId,
            repair => repair.Sources.Single().NativeCompletionAccepted = true,
            CancellationToken.None));
        await harness.WaitForCompletedAsync(clear.Id);

        Assert.Equal(scans, harness.Scans);
        await using var check = harness.CreateContext();
        var before = await check.Downloads
            .Where(download => download.EndTimeUtc < clearStarted)
            .ToListAsync();
        Assert.Equal(2, before.Count);
        // With no traffic the clear's own evict marks every older row of the source, even one
        // whose source has a receipt and a key scheme; otherwise the scan decides them.
        Assert.All(before, download => Assert.Equal(evictsDirectly, download.IsEvicted));
    }

    [Fact]
    public async Task CancelledClearWithoutTrafficStillScansAsync()
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "cancelled-clear"));
        var clear = NewCacheClearingRepair(Path.Combine(harness.Root, "alpha-cache"));
        var source = clear.Sources.Single();
        source.LogRoot = Path.Combine(harness.Root, "alpha-logs");
        source.KeyScheme = harness.Capabilities.GetKeySchemeWireValue(harness.Datasources.GetDatasource("alpha")!);
        source.ReceiptPath = null;

        await harness.RunAsync(clear, OperationStatus.Cancelled);
        await harness.WaitForCompletedAsync(clear.Id);

        Assert.Equal(1, harness.Scans);
    }

    [Fact]
    public async Task ShutdownLeavesTheRepairToTheNextStartAsync()
    {
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await using (var harness = await RepairHarness.CreateAsync(
                         _root,
                         stateService: state,
                         apply: async (_, cancellationToken) =>
                         {
                             entered.TrySetResult(true);
                             await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                         }))
        {
            await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
            await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
            await harness.Owner.FinishRepairAsync(repair.Id, true, false, null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(OperationRepairPhase.Repairing, Assert.Single(CreateStateService(_root).LoadOperationRepairs()).Phase);
        OperationRepair? rerun = null;
        await using var restarted = await RepairHarness.CreateAsync(
            _root,
            stateService: CreateStateService(_root),
            apply: (accepted, _) =>
            {
                rerun = accepted;
                return Task.CompletedTask;
            });
        await WaitForAsync(() => restarted.StateService.LoadOperationRepairs().Single().Phase
            == OperationRepairPhase.Completed);
        Assert.Equal(OperationStatus.Completed, rerun!.Outcome);
    }

    [Fact]
    public async Task ForceStoppedRepairWaitsForTheOwnersFinishAsync()
    {
        OperationRepair? applied = null;
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            apply: (accepted, _) =>
            {
                if (accepted.Id == repair.Id)
                {
                    applied = accepted;
                }
                return Task.CompletedTask;
            });
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        var later = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" });
        await harness.Owner.PrepareRepairAsync(later, CancellationToken.None);
        await harness.Owner.StartWorkAsync(later.Id, "alpha", CancellationToken.None);

        await harness.Owner.RecordForceStopAsync(repair.Id);
        await harness.Owner.RecordForceStopAsync(repair.Id);
        Assert.True(PrivateDictionary(harness.Owner, "_repairTasks").Contains(repair.Id));
        // A repair claimed after the stop runs and ends while the stopped job's repair still waits.
        await harness.Owner.FinishRepairAsync(later.Id, true, false, null);
        await WaitForAsync(() => harness.StateService.LoadOperationRepairs()
            .Single(item => item.Id == later.Id).Phase == OperationRepairPhase.Completed);
        // The owner's own cleanup still writes after the stop, and the repair sees it.
        await harness.Owner.SaveRepairAsync(repair.Id, next => next.Removal!.FilesDeleted = 5, CancellationToken.None);
        Assert.Null(applied);

        await harness.Owner.FinishRepairAsync(repair.Id, false, true, null);
        await WaitForAsync(() => applied is not null);
        Assert.Equal(5, applied!.Removal!.FilesDeleted);
        Assert.Equal(OperationStatus.Cancelled, applied.Outcome);
    }

    [Fact]
    public void CleanupKeepsRepairStateAndCorruptionEvidence()
    {
        var resolver = new AuthCredentialFormatTests.TempDirPathResolver(_root);
        var operations = resolver.GetOperationsDirectory();
        var old = DateTime.UtcNow.AddHours(-48);
        string[] kept =
        [
            Path.Combine(operations, "operation_repairs.json"),
            Path.Combine(operations, $"corruption_evidence_{Guid.NewGuid()}_default.json")
        ];
        var removed = Path.Combine(operations, $"corruption_removal_{Guid.NewGuid()}_default.json");
        foreach (var file in kept.Append(removed))
        {
            File.WriteAllText(file, "[]");
            File.SetLastWriteTimeUtc(file, old);
        }

        Assert.Equal(1, resolver.CleanupOperationFiles(24));

        Assert.All(kept, file => Assert.True(File.Exists(file)));
        Assert.False(File.Exists(removed));
    }

    [Fact]
    public async Task RepairWaitsForANamedProcessInsideTheGateAsync()
    {
        var processes = new WaitingProcessManager();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<Guid>();
        Guid first = Guid.Empty;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            processManager: processes,
            apply: async (repair, cancellationToken) =>
            {
                lock (applied)
                {
                    applied.Add(repair.Id);
                }
                if (repair.Id == first)
                {
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
            });
        // A log pass owes a repair only when it must reset positions; it runs beside the removal.
        var pass = NewLogProcessingRepair();
        pass.Sources.Single().ResetLogPositions = true;
        await harness.Owner.PrepareRepairAsync(pass, CancellationToken.None);
        await harness.Owner.StartWorkAsync(pass.Id, "alpha", CancellationToken.None);
        var removal = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        first = removal.Id;
        await harness.Owner.PrepareRepairAsync(removal, CancellationToken.None);
        await harness.Owner.StartWorkAsync(removal.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(removal.Id, false, true, null);

        // The removal's binary is still running, so the in-session repair does not apply yet.
        await processes.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["cache_steam_remove", "cache_purge_log_entries", "cache_eviction_scan"], processes.ProcessNames);
        Assert.Empty(applied);
        processes.Release.TrySetResult(true);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A second repair whose attempt begins while the first holds the gate waits for the gate
        // before it looks for processes, so it never sees the first repair's own children.
        await harness.Owner.FinishRepairAsync(pass.Id, false, true, null);
        await WaitForAsync(() => PrivateDictionary(harness.Owner, "_repairTasks").Contains(pass.Id));
        Assert.Equal(1, processes.Calls);

        releaseFirst.TrySetResult(true);
        await WaitForAsync(() => harness.StateService.LoadOperationRepairs().All(
            repair => repair.Phase == OperationRepairPhase.Completed));
        Assert.Equal(2, processes.Calls);
        Assert.Equal(["log_processor"], processes.ProcessNames);
        Assert.False(PrivateDictionary(harness.Owner, "_repairFailures").Contains(pass.Id));
        Assert.Equal([removal.Id, pass.Id], applied);
    }

    [Fact]
    public async Task InSessionClearRepairDoesNotWaitForRsyncAsync()
    {
        var processes = new WaitingProcessManager();
        processes.Release.TrySetResult(true);
        await using var harness = await RepairHarness.CreateAsync(_root, processManager: processes);
        var clear = NewCacheClearingRepair(Path.Combine(_root, "cache"));
        await harness.Owner.PrepareRepairAsync(clear, CancellationToken.None);
        await harness.Owner.StartWorkAsync(clear.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(clear.Id, false, true, null);

        await WaitForAsync(() => harness.StateService.LoadOperationRepairs().Single().Phase
            == OperationRepairPhase.Completed);
        // rsync is not this app's binary; on a host-pid install the name matches host processes.
        Assert.Equal(["cache_clear", "cache_eviction_scan"], processes.ProcessNames);
    }

    [Fact]
    public async Task ProcessThatNeverExitsFailsTheRepairOutAsync()
    {
        var processes = new WaitingProcessManager();
        // Every wait ends the way the one-minute bound ends it.
        processes.Release.TrySetCanceled();
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var applied = 0;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            processManager: processes,
            now: () => now,
            waitUntil: (retryAt, _) =>
            {
                now = retryAt > now ? retryAt : now;
                return Task.CompletedTask;
            },
            apply: (_, _) =>
            {
                Interlocked.Increment(ref applied);
                return Task.CompletedTask;
            });
        var repair = NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" });
        repair.Id = harness.Tracker.RegisterOperation(
            OperationType.ServiceRemoval,
            repair.Name,
            new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(repair.Id, false, true, null);
        harness.Tracker.CompleteOperation(repair.Id, success: false, cancelled: true);

        await WaitForAsync(() => harness.Tracker.GetOperation(repair.Id)?.RepairError is not null);
        Assert.Contains("did not exit", harness.Tracker.GetOperation(repair.Id)!.RepairError, StringComparison.Ordinal);
        Assert.Equal(3, processes.Calls);
        Assert.Equal(0, applied);
        Assert.Null(harness.Owner.GetBlockingRepair());
    }

    [Fact]
    public async Task StartupClearRepairWaitsForRsyncAsync()
    {
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        var clear = NewCacheClearingRepair(Path.Combine(_root, "cache"));
        clear.Phase = OperationRepairPhase.Running;
        clear.Sources.Single().NativeLaunchAuthorized = true;
        state.SaveOperationRepairs([clear]);
        var processes = new WaitingProcessManager();
        processes.Release.TrySetResult(true);

        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: CreateStateService(_root),
            processManager: processes);
        await WaitForAsync(() => harness.StateService.LoadOperationRepairs().Single().Phase
            == OperationRepairPhase.Completed);

        Assert.Equal(["cache_clear", "rsync", "cache_eviction_scan"], processes.ProcessNames);
    }

    [Fact]
    public async Task FailedOutcomeSaveKeepsTheRowUntilTheOutcomeLandsAsync()
    {
        var state = CreateFailingStateService(_root);
        var retryEntered = new SemaphoreSlim(0);
        var retryRelease = new SemaphoreSlim(0);
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            waitUntil: async (_, cancellationToken) =>
            {
                retryEntered.Release();
                await retryRelease.WaitAsync(cancellationToken);
            });
        var prepared = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        prepared.Id = harness.Tracker.RegisterOperation(
            OperationType.GameRemoval,
            prepared.Name,
            new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(prepared, CancellationToken.None);

        state.FailNextRepairWrite = true;
        await harness.Owner.FinishRepairAsync(prepared.Id, false, true, null).WaitAsync(TimeSpan.FromSeconds(5));
        harness.Tracker.CompleteOperation(prepared.Id, success: false, cancelled: true);

        // The reaper leaves a row whose outcome has not landed.
        OperationWaitingBlockerTests.Reap(harness.Tracker, prepared.Id);
        var kept = Assert.IsType<OperationInfo>(harness.Tracker.GetOperation(prepared.Id));
        Assert.True(kept.Repairing);
        Assert.Equal(prepared.Id, harness.Owner.GetBlockingRepair()?.Id);

        Assert.True(await retryEntered.WaitAsync(TimeSpan.FromSeconds(5)));
        retryRelease.Release();
        await WaitForAsync(() => harness.Owner.GetBlockingRepair() is null);
        await WaitForAsync(() => !harness.Tracker.GetOperation(prepared.Id)?.Repairing ?? true);
        Assert.Equal(OperationRepairPhase.Completed, Assert.Single(state.LoadOperationRepairs()).Phase);
    }

    [Fact]
    public async Task FullRepairResetsLogPositionsOnceAcrossFailedAttemptsAsync()
    {
        var root = Path.Combine(_root, "full-repair-reset-once");
        await using var harness = await DispatchHarness.CreateAsync(root, start: false);
        var path = RepairFilePath(Path.Combine(root, "state"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-json");
        harness.State.SetLogPosition("alpha", 73);
        harness.HoldScans = true;
        harness.FailScans = 1;

        await harness.Owner.StartAsync(CancellationToken.None);
        var fullRepair = Assert.Single(harness.Owner.GetPendingRepairs());
        await WaitForAsync(() => harness.Scans == 1);
        Assert.Equal(0, harness.State.GetLogPosition("alpha"));
        Assert.True(Assert.Single(harness.Owner.GetPendingRepairs()).Sources.Single().LogPositionsKept);

        // The import reads the log again before the failed attempt is retried.
        harness.State.SetLogPosition("alpha", 55);
        harness.ScanRelease.TrySetResult();
        await harness.WaitForCompletedAsync(fullRepair.Id);

        Assert.Equal(2, harness.Scans);
        Assert.Equal(55, harness.State.GetLogPosition("alpha"));
    }

    [Fact]
    public async Task OwnerSaveAfterAFailedForceStopSaveUnblocksAtOnceAsync()
    {
        var state = CreateFailingStateService(_root);
        var retryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            // The force stop's retry never comes due, so only the owner's own save lands.
            waitUntil: async (_, cancellationToken) =>
            {
                retryEntered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            });
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        state.FailRepairStarts = 1;
        await harness.Owner.RecordForceStopAsync(repair.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Owner.FinishRepairAsync(repair.Id, false, true, null).WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => harness.Owner.GetPendingRepairs().Count == 0);

        Assert.Null(harness.Owner.GetBlockingRepair());
        Assert.Empty(PrivateDictionary(harness.Owner, "_pendingOutcomes"));
        Assert.Equal(OperationStatus.Cancelled, Assert.Single(state.LoadOperationRepairs()).Outcome);
    }

    [Fact]
    public async Task OwnerMetricsAfterAForceStopAreKeptAsync()
    {
        OperationRepair? applied = null;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            apply: (accepted, _) =>
            {
                applied = accepted;
                return Task.CompletedTask;
            });
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        await harness.Owner.RecordForceStopAsync(repair.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Owner.FinishRepairAsync(
                repair.Id,
                success: false,
                cancelled: true,
                error: null,
                update: next => next.Removal!.FilesDeleted = 7)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => harness.StateService.LoadOperationRepairs().Single().Phase
            == OperationRepairPhase.Completed);

        Assert.Equal(7, applied!.Removal!.FilesDeleted);
        Assert.Equal(7, harness.StateService.LoadOperationRepairs().Single().Removal!.FilesDeleted);
    }

    [Fact]
    public async Task ServiceCountsAreInvalidatedAfterTheLogStepRedoAsync()
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "service-counts-after-redo"));
        int Waiters(string field) => (int)typeof(OperationStateService)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness.Owner)!;
        harness.State.SetLogPosition("alpha", 73);
        var removal = harness.NewRemoval(OperationType.GameRemoval);
        await harness.Owner.PrepareRepairAsync(removal, CancellationToken.None);
        await harness.Owner.StartWorkAsync(removal.Id, "alpha", CancellationToken.None);
        await harness.Owner.MarkLogRewriteStartedAsync(removal.Id, "alpha");

        // The repair's reset queues behind a running import; a second import that queues while the
        // reset runs takes the logs next, so the redo waits behind it.
        var import = await harness.Owner.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        await harness.Owner.FinishRepairAsync(removal.Id, false, true, null);
        await WaitForAsync(() => Waiters("_logStepWaiters") == 1);
        var nextImport = harness.Owner.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        await WaitForAsync(() => Waiters("_ingestWaiters") == 1);
        await import.DisposeAsync();
        var heldImport = await nextImport.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => Waiters("_logStepWaiters") == 1);

        Assert.Equal(0, harness.State.GetLogPosition("alpha"));
        Assert.DoesNotContain(
            harness.Notifications.Events,
            item => item.EventName == SignalREvents.ServiceCountsChanged);

        await heldImport.DisposeAsync();
        await harness.WaitForCompletedAsync(removal.Id);
        Assert.Single(
            harness.Notifications.Events,
            item => item.EventName == SignalREvents.ServiceCountsChanged);
    }

    [Fact]
    public async Task ServiceCountsAreInvalidatedWhenTheRedoFailsAsync()
    {
        await using var harness = await DispatchHarness.CreateAsync(Path.Combine(_root, "service-counts-after-failed-redo"));
        int ServiceCountEvents() => harness.Notifications.Events
            .Count(item => item.EventName == SignalREvents.ServiceCountsChanged);
        var countsAtFailure = new List<int>();
        harness.Log.OnLogged = entry =>
        {
            if (entry.Message.StartsWith("Required repair failed", StringComparison.Ordinal))
            {
                lock (countsAtFailure)
                {
                    countsAtFailure.Add(ServiceCountEvents());
                }
            }
        };
        harness.State.SetLogPosition("alpha", 73);
        var removal = harness.NewRemoval(OperationType.GameRemoval);
        await harness.Owner.PrepareRepairAsync(removal, CancellationToken.None);
        await harness.Owner.StartWorkAsync(removal.Id, "alpha", CancellationToken.None);
        await harness.Owner.MarkLogRewriteStartedAsync(removal.Id, "alpha");

        // The repair's reset waits behind a running import, so every save before the redo has
        // landed and the redo's closing save is the write that fails.
        var import = await harness.Owner.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        await harness.Owner.FinishRepairAsync(removal.Id, false, true, null);
        await WaitForAsync(() => (int)typeof(OperationStateService)
            .GetField("_logStepWaiters", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(harness.Owner)! == 1);
        harness.State.FailNextRepairWrite = true;
        await import.DisposeAsync();
        await harness.WaitForCompletedAsync(removal.Id);

        var failure = Assert.Single(
            harness.Log.Entries,
            entry => entry.Message.StartsWith("Required repair failed", StringComparison.Ordinal));
        Assert.Equal("Injected operation repair write failure.", Assert.IsType<IOException>(failure.Exception).Message);
        // One invalidation from the failed attempt before its failure was counted, one from the retry.
        Assert.Equal([1], countsAtFailure);
        Assert.Equal(2, ServiceCountEvents());
    }

    [Fact]
    public async Task RetryAfterTheCardWasClosedIsRefusedAsync()
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            now: () => now,
            waitUntil: (retryAt, _) =>
            {
                now = retryAt > now ? retryAt : now;
                return Task.CompletedTask;
            },
            apply: (_, _) => Task.FromException(new IOException("Injected repair failure.")));
        var repair = NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { SteamAppId = 480 });
        repair.Id = harness.Tracker.RegisterOperation(
            OperationType.GameRemoval,
            repair.Name,
            new CancellationTokenSource(),
            ownerCompletes: true);
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        await harness.Owner.FinishRepairAsync(repair.Id, false, true, null);
        harness.Tracker.CompleteOperation(repair.Id, success: false, cancelled: true);
        await WaitForAsync(() => harness.Tracker.GetOperation(repair.Id)?.RepairError is not null);

        Assert.True(harness.Tracker.CloseRun(repair.Id));

        Assert.False(await harness.Owner.RetryRepairAsync(repair.Id));
        Assert.Equal(3, PrivateDictionary(harness.Owner, "_repairFailures")[repair.Id]);
    }

    private static OperationRepair CleanRemoval(
        DispatchHarness harness,
        OperationType type,
        CorruptionDetectionMethod method = CorruptionDetectionMethod.RepeatedMiss)
    {
        var removal = harness.NewRemoval(type == OperationType.CorruptionRemoval ? OperationType.ServiceRemoval : type);
        if (type == OperationType.CorruptionRemoval)
        {
            removal.Type = OperationType.CorruptionRemoval;
            removal.Corruption = new CorruptionRepair
            {
                ScanId = Guid.NewGuid(),
                ContractVersion = 1,
                DetectionMethod = method,
                Service = "steam"
            };
            removal.Removal!.EntityKind = "service";
            removal.Removal.DetectionMethod = method;
            removal.Sources.Single().ApplyCorruptionCandidates = true;
        }
        return removal;
    }

    private static async Task KeepEveryStepAsync(DispatchHarness harness, Guid operationId)
    {
        await AcceptSourceAsync(harness, operationId);
        await harness.Owner.MarkLogRewriteStartedAsync(operationId, "alpha");
        await harness.Owner.MarkLogPositionsKeptAsync(operationId, "alpha");
    }

    private static Task AcceptSourceAsync(DispatchHarness harness, Guid operationId)
    {
        return harness.Owner.SaveRepairAsync(
            operationId,
            repair =>
            {
                var source = repair.Sources.Single();
                source.NativeCompletionAccepted = true;
                if (repair.Type == OperationType.CorruptionRemoval)
                {
                    source.CorruptionCounts = new CorruptionRemovalCounts { UrlsRemoved = 1, FilesDeleted = 1 };
                }
            },
            CancellationToken.None);
    }

    private static Download ClearDownload(string datasource, DateTime endedAt, long bytes) => new()
    {
        Service = "steam",
        ClientIp = "10.0.0.5",
        Datasource = datasource,
        StartTimeUtc = endedAt.AddMinutes(-5),
        EndTimeUtc = endedAt,
        CacheHitBytes = bytes
    };

    private static List<OperationRepair> TypedRepairs()
    {
        var started = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        List<OperationRepair> repairs =
        [
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.CacheClearing,
                Name = "Cache clear",
                StartedAt = started,
                Phase = OperationRepairPhase.Running,
                Sources = [Source("alpha")],
                CacheClearing = new CacheClearingRepair
                {
                    EntityKey = "bulk",
                    DirectoriesProcessed = 2,
                    TotalDirectories = 3,
                    BytesDeleted = 4,
                    FilesDeleted = 5,
                    DatasourcesCleared = 1,
                    Duration = 2.5
                }
            },
            NewLogProcessingRepair(started, entries: 11, phase: OperationRepairPhase.Running),
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.LogRemoval,
                Name = "Log removal",
                StartedAt = started,
                Phase = OperationRepairPhase.Running,
                Sources = [Source("alpha")],
                LogRemoval = new LogRemovalRepair
                {
                    Service = "steam",
                    Datasource = "alpha",
                    FilesProcessed = 2,
                    LinesProcessed = 3,
                    LinesRemoved = 4,
                    DatabaseRecordsDeleted = 5,
                    StageKey = "logs.finalizing"
                }
            },
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.GameRemoval,
                Name = "Game removal",
                StartedAt = started,
                Phase = OperationRepairPhase.Running,
                Sources = [Source("alpha")],
                Target = new CacheRepairTarget { SteamAppId = 480, SteamDepotIds = [481] },
                Removal = new RemovalRepair
                {
                    EntityKey = "480",
                    EntityName = "Spacewar",
                    EntityKind = "steam",
                    FilesDeleted = 6,
                    BytesFreed = 7,
                    FilesProcessed = 8,
                    TotalFiles = 9,
                    LogEntriesRemoved = ulong.MaxValue - 7,
                    StageKey = "cache.finalizing"
                }
            },
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.GameDetection,
                Name = "Game detection",
                StartedAt = started,
                Phase = OperationRepairPhase.Running,
                DatabaseWriteStarted = true,
                GameDetection = new GameDetectionMetrics
                {
                    TotalGamesDetected = 3,
                    TotalServicesDetected = 2,
                    StartTime = started
                }
            },
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.EvictionScan,
                Name = "Eviction scan",
                StartedAt = started,
                Phase = OperationRepairPhase.Running,
                Sources = [Source("alpha")],
                EvictionScanId = Guid.NewGuid(),
                EvictionScan = new EvictionScanRepair { Processed = 10, Evicted = 4, UnEvicted = 2 }
            },
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.EvictionRemoval,
                Name = "Eviction removal",
                StartedAt = started,
                Phase = OperationRepairPhase.Running,
                DatabaseWriteStarted = true,
                EvictionRemoval = new EvictionRemovalRepair
                {
                    Selection = new EvictionRemovalMetadata(),
                    StageKey = "eviction.finalizing",
                    DownloadsRemoved = 12,
                    LogEntriesRemoved = 13
                }
            },
            new OperationRepair
            {
                Id = Guid.NewGuid(),
                Type = OperationType.CorruptionRemoval,
                Name = "Corruption removal",
                StartedAt = started,
                Phase = OperationRepairPhase.Running,
                Target = new CacheRepairTarget { Service = "steam" },
                Corruption = new CorruptionRepair
                {
                    ScanId = Guid.NewGuid(),
                    ContractVersion = 1,
                    DetectionMethod = CorruptionDetectionMethod.RepeatedMiss,
                    Service = "steam"
                },
                Sources =
                [
                    new OperationRepairSource
                    {
                        Datasource = "alpha",
                        LogRoot = "logs/alpha",
                        CacheRoot = "cache/alpha",
                        KeyScheme = "steam",
                        NativeLaunchAuthorized = true,
                        NativeCompletionAccepted = true,
                        ApplyCorruptionCandidates = true,
                        CorruptionCandidateIds = ["alpha:MixedCase/7"],
                        CorruptionCounts = new CorruptionRemovalCounts
                        {
                            UrlsRemoved = 1,
                            FilesDeleted = 2,
                            LogLinesRemoved = 3,
                            DownloadsDeleted = 4,
                            LogEntriesDeleted = 5,
                            AlreadyMissing = 6,
                            Healed = 7,
                            BytesFreed = 8
                        }
                    }
                ],
                Removal = new RemovalRepair
                {
                    EntityKey = "steam",
                    EntityName = "steam",
                    EntityKind = "service",
                    FilesDeleted = 2,
                    BytesFreed = 8,
                    FilesProcessed = 2,
                    TotalFiles = 2,
                    DetectionMethod = CorruptionDetectionMethod.RepeatedMiss,
                    StageKey = "corruption.finalizing"
                }
            }
        ];

        foreach (var repair in repairs)
        {
            repair.Notice = new RunNotice(
                NotificationMode.All,
                RunTrigger.Manual,
                new ScheduleActor(
                    ScheduleActorKind.Account,
                    Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    "admin"),
                restoredOrigin: true);
            repair.Phase = OperationRepairPhase.Repairing;
            repair.Outcome = OperationStatus.Completed;
        }

        repairs[0].CacheClearing!.DatasourceName = "alpha";
        repairs[1].LogProcessing!.Elapsed = 2.75;
        repairs[1].LogProcessing!.Message = "Processed accepted rows";
        repairs[1].LogProcessing!.StageKey = "logs.finalizing";
        repairs[5].EvictionScan!.DetectionError = "Probe abstained";
        return repairs;
    }

    private static OperationRepair NewRemovalRepair(OperationType type, CacheRepairTarget target)
    {
        return new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = type,
            Name = "Cache removal",
            StartedAt = DateTime.UtcNow,
            Sources = [Source("alpha")],
            Target = target,
            Removal = new RemovalRepair
            {
                EntityKey = "selected",
                EntityName = "Selected cache",
                EntityKind = type == OperationType.ServiceRemoval ? "service" : "game"
            }
        };
    }

    private static OperationRepair NewCacheClearingRepair(string cacheRoot)
    {
        var operationId = Guid.NewGuid();
        return new OperationRepair
        {
            Id = operationId,
            Type = OperationType.CacheClearing,
            Name = "Cache clear",
            StartedAt = DateTime.UtcNow,
            Sources =
            [
                new OperationRepairSource
                {
                    Datasource = "alpha",
                    LogRoot = Path.Combine(cacheRoot, "logs"),
                    CacheRoot = cacheRoot,
                    KeyScheme = "steam",
                    ReceiptPath = Path.Combine(
                        cacheRoot,
                        $".lancache-repair-{operationId:N}.json"),
                    ReconcileCache = true
                }
            ],
            CacheClearing = new CacheClearingRepair { EntityKey = "bulk" }
        };
    }

    private static void WriteRootReceipt(string receiptPath, Guid operationId, string cacheRoot)
    {
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(
            receiptPath,
            JsonSerializer.Serialize(new
            {
                version = 1,
                operationId,
                cachePath = new DirectoryInfo(cacheRoot).FullName,
                hadCacheFiles = true
            }));
    }

    private static OperationRepair NewLogProcessingRepair(
        DateTime? startedAt = null,
        long entries = 0,
        OperationRepairPhase phase = OperationRepairPhase.Prepared)
    {
        return new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogProcessing,
            Name = "Log processing",
            StartedAt = startedAt ?? DateTime.UtcNow,
            Phase = phase,
            Sources = [Source("alpha")],
            LogProcessing = new LogProcessingRepair { EntriesProcessed = entries }
        };
    }

    private static OperationRepairSource Source(string datasource) => new()
    {
        Datasource = datasource,
        LogRoot = "logs/" + datasource,
        CacheRoot = "cache/" + datasource,
        KeyScheme = "steam",
        RefreshDownloads = true
    };

    private static string RepairFilePath(string root) => Path.Combine(
        root,
        nameof(IPathResolver.GetOperationsDirectory),
        "operation_repairs.json");

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("The repair state did not settle.");
    }

    private static IDictionary PrivateDictionary(OperationStateService owner, string field)
    {
        return Assert.IsAssignableFrom<IDictionary>(typeof(OperationStateService)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner));
    }

    private static StateService CreateStateService(string root)
    {
        var parts = StateParts(root);
        return new StateService(
            NullLogger<StateService>.Instance,
            parts.Resolver,
            parts.Encryption,
            parts.SteamAuthStorage);
    }

    internal static FailingStateService CreateFailingStateService(string root)
    {
        var parts = StateParts(root);
        return new FailingStateService(
            NullLogger<StateService>.Instance,
            parts.Resolver,
            parts.Encryption,
            parts.SteamAuthStorage);
    }

    private static (IPathResolver Resolver, SecureStateEncryptionService Encryption,
        SteamAuthStorageService SteamAuthStorage) StateParts(string root)
    {
        var configuration = new ConfigurationBuilder().Build();
        var resolver = DispatchProxy.Create<IPathResolver, RepairPathResolver>();
        ((RepairPathResolver)(object)resolver).Root = root;
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys")));
        var apiKeys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, resolver);
        var encryption = new SecureStateEncryptionService(
            protection,
            apiKeys,
            NullLogger<SecureStateEncryptionService>.Instance);
        var steam = new SteamAuthStorageService(
            NullLogger<SteamAuthStorageService>.Instance,
            resolver,
            encryption);
        return (resolver, encryption, steam);
    }

    internal sealed class FailingStateService : StateService
    {
        public bool FailNextRepairWrite { get; set; }
        public int FailRepairStarts { get; set; }
        public List<int> RepairWriteSizes { get; } = [];

        public FailingStateService(
            ILogger<StateService> logger,
            IPathResolver pathResolver,
            SecureStateEncryptionService encryption,
            SteamAuthStorageService steamAuthStorage)
            : base(logger, pathResolver, encryption, steamAuthStorage)
        {
        }

        protected override void WriteOperationRepairs(string contents)
        {
            if (FailNextRepairWrite)
            {
                FailNextRepairWrite = false;
                throw new IOException("Injected operation repair write failure.");
            }
            if (FailRepairStarts > 0)
            {
                var repairs = JsonSerializer.Deserialize<List<OperationRepair>>(contents) ?? [];
                if (repairs.Any(repair => repair.Phase == OperationRepairPhase.Repairing))
                {
                    FailRepairStarts--;
                    throw new IOException("Injected repair-start write failure.");
                }
            }
            base.WriteOperationRepairs(contents);
            RepairWriteSizes.Add(contents.Length);
        }
    }

    private class RepairPathResolver : DispatchProxy
    {
        public required string Root { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(string))
            {
                return Path.Combine(Root, targetMethod.Name);
            }
            if (targetMethod?.ReturnType == typeof(bool))
            {
                return false;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class WaitingProcessManager : ProcessManager
    {
        public TaskCompletionSource<bool> WaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<string> ProcessNames { get; private set; } = [];
        public int Calls { get; private set; }

        public WaitingProcessManager()
            : base(NullLogger<ProcessManager>.Instance)
        {
        }

        public override async Task WaitForProcessesExitAsync(
            IReadOnlyCollection<string> processNames,
            CancellationToken cancellationToken)
        {
            Calls++;
            ProcessNames = processNames;
            WaitStarted.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ThrowingProcessManager : ProcessManager
    {
        public ThrowingProcessManager()
            : base(NullLogger<ProcessManager>.Instance)
        {
        }

        public override Task WaitForProcessesExitAsync(
            IReadOnlyCollection<string> processNames,
            CancellationToken cancellationToken)
        {
            throw new IOException("Injected process discovery error.");
        }
    }

    internal sealed class RepairHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private bool _stopped;

        public StateService StateService { get; }
        public OperationStateService Owner { get; }
        public IHostApplicationLifetime Lifetime { get; }
        public UnifiedOperationTracker Tracker { get; }
        public IConfiguration Configuration { get; }

        private RepairHarness(
            string root,
            StateService? stateService,
            ProcessManager? processManager,
            Func<OperationRepair, CancellationToken, Task>? apply,
            Func<DateTime>? now,
            Func<DateTime, CancellationToken, Task>? waitUntil,
            Action<Guid, CancellationToken>? onRestore,
            Action<IServiceCollection>? registrations,
            ILogger<OperationStateService>? logger)
        {
            StateService = stateService ?? CreateStateService(root);
            StateService.SetSetupCompleted(true);
            Lifetime = new HostLifetime();
            var services = new ServiceCollection();
            registrations?.Invoke(services);
            _services = services.BuildServiceProvider();
            var processes = processManager ?? new ProcessManager(NullLogger<ProcessManager>.Instance);
            Tracker = new UnifiedOperationTracker(
                processes,
                NullLogger<UnifiedOperationTracker>.Instance);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Owner = new RepairOwner(
                logger ?? NullLogger<OperationStateService>.Instance,
                Configuration,
                StateService,
                _services.GetRequiredService<IServiceScopeFactory>(),
                Lifetime,
                processes,
                Tracker,
                apply,
                now,
                waitUntil,
                onRestore);
        }

        internal static async Task<RepairHarness> CreateAsync(
            string root,
            bool start = true,
            StateService? stateService = null,
            ProcessManager? processManager = null,
            Func<OperationRepair, CancellationToken, Task>? apply = null,
            Func<DateTime>? now = null,
            Func<DateTime, CancellationToken, Task>? waitUntil = null,
            Action<Guid, CancellationToken>? onRestore = null,
            Action<IServiceCollection>? registrations = null,
            ILogger<OperationStateService>? logger = null)
        {
            var harness = new RepairHarness(
                root,
                stateService,
                processManager,
                apply,
                now,
                waitUntil,
                onRestore,
                registrations,
                logger);
            if (start)
            {
                await harness.Owner.StartAsync(CancellationToken.None);
            }
            return harness;
        }

        internal IHost CreateHost()
        {
            return new HostBuilder()
                .ConfigureServices(services => services.AddSingleton<IHostedService>(Owner))
                .Build();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                _stopped = true;
                Lifetime.StopApplication();
                await Owner.StopAsync(CancellationToken.None);
            }
            await _services.DisposeAsync();
        }
    }

    private sealed class RepairOwner : OperationStateService
    {
        private readonly Func<OperationRepair, CancellationToken, Task> _apply;
        private readonly Func<DateTime> _now;
        private readonly Func<DateTime, CancellationToken, Task>? _waitUntil;
        private readonly Action<Guid, CancellationToken>? _onRestore;
        private readonly IUnifiedOperationTracker _operationTracker;

        public int RestoreCalls { get; private set; }
        public int PruneCalls { get; private set; }

        protected override TimeSpan StartupDelay => TimeSpan.Zero;

        public RepairOwner(
            ILogger<OperationStateService> logger,
            IConfiguration configuration,
            StateService stateService,
            IServiceScopeFactory scopes,
            IHostApplicationLifetime applicationLifetime,
            ProcessManager processManager,
            IUnifiedOperationTracker operationTracker,
            Func<OperationRepair, CancellationToken, Task>? apply,
            Func<DateTime>? now,
            Func<DateTime, CancellationToken, Task>? waitUntil,
            Action<Guid, CancellationToken>? onRestore)
            : base(
                logger,
                configuration,
                stateService,
                scopes,
                applicationLifetime,
                processManager,
                operationTracker)
        {
            _apply = apply ?? ((_, _) => Task.CompletedTask);
            _now = now ?? (() => DateTime.UtcNow);
            _waitUntil = waitUntil;
            _onRestore = onRestore;
            _operationTracker = operationTracker;
        }

        protected override DateTime UtcNow => _now();

        protected override Task RestoreOwnerAsync(OperationRepair repair, CancellationToken stoppingToken)
        {
            stoppingToken.ThrowIfCancellationRequested();
            RestoreCalls++;
            _onRestore?.Invoke(repair.Id, stoppingToken);
            var source = new CancellationTokenSource();
            // A cache clear restores its retained metrics as the row's metadata, as the real
            // owner does, so the row can tell a full repair from a clear.
            if (!_operationTracker.TryRestoreOperation(
                    repair.Id,
                    repair.Type,
                    repair.Name,
                    source,
                    metadata: repair.CacheClearing,
                    startedAt: repair.StartedAt,
                    notice: repair.Notice,
                    ownerCompletes: true))
            {
                source.Dispose();
            }
            return Task.CompletedTask;
        }

        protected override Task ClearOrphanedCorruptionPresentationAsync(CancellationToken stoppingToken)
        {
            return Task.CompletedTask;
        }

        protected override Task PruneEvictionScanCheckpointsAsync(
            DateTime cutoff,
            IReadOnlySet<Guid> retainedScanIds,
            CancellationToken stoppingToken)
        {
            PruneCalls++;
            return Task.CompletedTask;
        }

        internal Task RunMaintenanceAsync(CancellationToken stoppingToken)
        {
            return ExecuteWorkAsync(stoppingToken);
        }

        protected override Task ApplyRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
        {
            return _apply(repair, stoppingToken);
        }

        internal Task ApplyBaseAsync(OperationRepair repair, CancellationToken stoppingToken)
        {
            return base.ApplyRepairAsync(repair, stoppingToken);
        }

        protected override Task WaitUntilAsync(DateTime retryAtUtc, CancellationToken stoppingToken)
        {
            return _waitUntil?.Invoke(retryAtUtc, stoppingToken)
                ?? base.WaitUntilAsync(retryAtUtc, stoppingToken);
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

        public void StopApplication()
        {
            _stopping.Cancel();
        }
    }

    // Drives the real DispatchRepairAsync over a test database. The full cache scan is counted
    // instead of run, and a test can hold it or make it fail.
    internal sealed class DispatchHarness : IAsyncDisposable
    {
        private readonly TestDatabase _database;
        private readonly TaskCompletionSource<RepairOwner> _owner =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GameCacheDetectionDataService _detectionStore;
        private readonly GameCacheDetectionService _detection;
        private readonly CacheManagementService _management;
        private int _scans;

        private DispatchHarness(string root, TestDatabase database, bool unmappedDatasource)
        {
            _database = database;
            Root = root;
            var settings = new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "alpha-cache"),
                ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "alpha-logs"),
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            };
            if (unmappedDatasource)
            {
                settings["LanCache:DataSources:1:Name"] = "unmapped";
                settings["LanCache:DataSources:1:CachePath"] = Path.Combine(root, "unmapped-cache");
                settings["LanCache:DataSources:1:LogPath"] = Path.Combine(root, "unmapped-logs");
                settings["LanCache:DataSources:1:Enabled"] = "true";
            }
            foreach (var path in settings.Where(item => item.Key.EndsWith("Path", StringComparison.Ordinal)))
            {
                Directory.CreateDirectory(path.Value!);
            }
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            Paths = paths;
            Directory.CreateDirectory(paths.GetOperationsDirectory());
            Datasources = new DatasourceService(Configuration, paths, NullLogger<DatasourceService>.Instance);
            Capabilities = new DatasourceCapabilityService(Datasources);
            State = CreateFailingStateService(Path.Combine(root, "state"));
            CorruptionContexts = new CountingContexts(database.Factory);
            _detectionStore = new GameCacheDetectionDataService(
                database.Factory,
                NullLogger<GameCacheDetectionDataService>.Instance);
            _detection = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                paths,
                operationStateService: null!,
                database.Factory,
                _detectionStore,
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                rustProcessHelper: null!,
                Notifications,
                Datasources,
                Capabilities,
                operationTracker: null!,
                CacheScanGateHarness.Idle());
            _management = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
            CachedScanFile = Path.Combine(root, "cached_cache_scan.json");
            File.WriteAllText(CachedScanFile, "{}");
            foreach (var (field, value) in new (string, object)[]
                     {
                         ("_pathResolver", paths),
                         ("_notifications", Notifications),
                         ("_logger", NullLogger<CacheManagementService>.Instance),
                         ("_cachedScanFilePath", CachedScanFile)
                     })
            {
                typeof(CacheManagementService)
                    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(_management, value);
            }
            // The repair redoes a removal's unfinished log step, and its reopen check needs a log file.
            File.WriteAllText(Path.Combine(root, "alpha-logs", "access.log"), "GET /alpha HTTP/1.1\n");
        }

        public string Root { get; }
        public IConfiguration Configuration { get; }
        public IPathResolver Paths { get; }
        public DatasourceService Datasources { get; }
        public DatasourceCapabilityService Capabilities { get; }
        public FailingStateService State { get; }
        public RepairHarness Repairs { get; private set; } = null!;
        public OperationStateService Owner => Repairs.Owner;
        public ScheduledRunReporterTests.CapturingNotificationService Notifications { get; } = new();
        public CapturingLogger<OperationStateService> Log { get; } = new();
        public CountingContexts CorruptionContexts { get; }
        public string CachedScanFile { get; }
        public DateTime Now { get; set; } = DateTime.UtcNow;
        public bool HoldScans { get; set; }
        public TaskCompletionSource ScanRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FailScans { get; set; }
        public int Scans => Volatile.Read(ref _scans);
        public int DownloadsRefreshes => Notifications.Events.Count(item => item.EventName == SignalREvents.DownloadsRefresh);
        public long DetectionRefreshes => (long)typeof(GameCacheDetectionService)
            .GetField("_detectionRevision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_detection)!;

        public static async Task<DispatchHarness> CreateAsync(
            string root,
            bool unmappedDatasource = false,
            bool start = true)
        {
            var harness = new DispatchHarness(root, await TestDatabase.CreateAsync(), unmappedDatasource);
            harness.Repairs = await RepairHarness.CreateAsync(
                Path.Combine(root, "state"),
                start: start,
                stateService: harness.State,
                apply: async (repair, stoppingToken) =>
                    await (await harness._owner.Task).ApplyBaseAsync(repair, stoppingToken),
                now: () => harness.Now,
                waitUntil: (retryAt, _) =>
                {
                    harness.Now = retryAt > harness.Now ? retryAt : harness.Now;
                    return Task.CompletedTask;
                },
                registrations: harness.Register,
                logger: harness.Log);
            harness._owner.TrySetResult((RepairOwner)harness.Repairs.Owner);
            // What the redone log step uses; the owner exists only once the repair harness does.
            foreach (var (field, value) in new (string, object)[]
                     {
                         ("_operationStateService", harness.Owner),
                         ("_datasourceService", harness.Datasources),
                         ("_dbContextFactory", harness._database.Factory),
                         ("_stateService", harness.State),
                         ("_nginxLogRotationService", new NginxLogRotationService(
                             NullLogger<NginxLogRotationService>.Instance,
                             harness.Configuration,
                             new ProcessManager(NullLogger<ProcessManager>.Instance),
                             harness.Paths,
                             TimeProvider.System)),
                         ("_rustProcessHelper", new RemovalRepairHarness.RemovalRustProcessHelper(
                             root,
                             OperationType.GameRemoval,
                             harness.Paths,
                             harness.Repairs.Tracker,
                             harness.Owner,
                             harness.State,
                             harness._database.Factory))
                     })
            {
                typeof(CacheManagementService)
                    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(harness._management, value);
            }
            return harness;
        }

        public AppDbContext CreateContext() => new(_database.Options);

        public OperationRepair NewRemoval(OperationType type)
        {
            var repair = NewRemovalRepair(
                type,
                type == OperationType.ServiceRemoval
                    ? new CacheRepairTarget { Service = "steam" }
                    : new CacheRepairTarget { SteamAppId = 570 });
            repair.Sources = [RemovalSource()];
            return repair;
        }

        public OperationRepairSource RemovalSource()
        {
            var alpha = Datasources.GetDatasource("alpha")!;
            return new OperationRepairSource
            {
                Datasource = alpha.Name,
                LogRoot = alpha.LogPath,
                CacheRoot = alpha.CachePath,
                KeyScheme = Capabilities.GetKeySchemeWireValue(alpha),
                ResetLogPositions = true,
                RefreshDownloads = true,
                ReconcileCache = true,
                RefreshDetection = true,
                InvalidateCorruption = true
            };
        }

        // Prepares and starts the record, lets the test change it while it runs, then records the outcome.
        public async Task RunAsync(OperationRepair repair, OperationStatus outcome, Func<Guid, Task>? whileRunning = null)
        {
            await Owner.PrepareRepairAsync(repair, CancellationToken.None);
            foreach (var source in repair.Sources)
            {
                await Owner.StartWorkAsync(repair.Id, source.Datasource, CancellationToken.None);
            }
            if (whileRunning is not null)
            {
                await whileRunning(repair.Id);
            }
            await Owner.FinishRepairAsync(
                repair.Id,
                outcome == OperationStatus.Completed,
                outcome == OperationStatus.Cancelled,
                outcome == OperationStatus.Failed ? "failed" : null);
        }

        public async Task<OperationRepair> WaitForCompletedAsync(Guid operationId)
        {
            OperationRepair? completed = null;
            await WaitForAsync(() =>
            {
                completed = State.LoadOperationRepairs().SingleOrDefault(
                    repair => repair.Id == operationId && repair.Phase == OperationRepairPhase.Completed);
                return completed is not null;
            });
            return completed!;
        }

        private void Register(IServiceCollection services)
        {
            services.AddScoped(_ => CreateContext());
            services.AddSingleton(Datasources);
            services.AddSingleton(Capabilities);
            services.AddSingleton<ISignalRNotificationService>(Notifications);
            services.AddSingleton(_detection);
            services.AddSingleton(_management);
            services.AddSingleton((CacheClearingService)RuntimeHelpers.GetUninitializedObject(typeof(CacheClearingService)));
            services.AddSingleton((RustLogRemovalService)RuntimeHelpers.GetUninitializedObject(typeof(RustLogRemovalService)));
            services.AddSingleton(new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                Paths,
                Notifications,
                State,
                serviceProvider: null!,
                rustProcessHelper: null!,
                Datasources,
                operationTracker: null!));
            services.AddSingleton(_ => new CorruptionDetectionService(
                NullLogger<CorruptionDetectionService>.Instance,
                Configuration,
                Paths,
                new RustProcessHelper(
                    NullLogger<RustProcessHelper>.Instance,
                    new ProcessManager(NullLogger<ProcessManager>.Instance),
                    Paths,
                    operationTracker: null!),
                Notifications,
                Datasources,
                CorruptionContexts,
                Owner,
                operationTracker: null!,
                capabilityService: null!,
                CacheScanGateHarness.Idle(),
                nginxLogRotationService: null!,
                stateService: null!));
            services.AddSingleton<CacheReconciliationService>(resolver => new CountingReconciliation(this, resolver));
        }

        public async ValueTask DisposeAsync()
        {
            ScanRelease.TrySetResult();
            await Repairs.DisposeAsync();
            _detection.Dispose();
            await _database.DisposeAsync();
        }

        private sealed class CountingReconciliation(DispatchHarness harness, IServiceProvider services)
            : CacheReconciliationService(
                services,
                NullLogger<CacheReconciliationService>.Instance,
                harness.Configuration,
                harness.Datasources,
                harness.State,
                harness.Notifications,
                operationTracker: null!,
                rustProcessHelper: null!,
                nginxLogRotationService: null!,
                harness.Paths,
                harness._detectionStore,
                harness._detection,
                evictedDetectionPreservationService: null!,
                operationQueue: null!,
                harness.Repairs.Lifetime,
                harness.Capabilities,
                CacheScanGateHarness.Idle())
        {
            public override async Task ReconcileRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
            {
                Interlocked.Increment(ref harness._scans);
                if (harness.HoldScans)
                {
                    await harness.ScanRelease.Task.WaitAsync(stoppingToken);
                }
                if (harness.FailScans > 0)
                {
                    harness.FailScans--;
                    throw new IOException("Injected cache scan failure.");
                }
            }
        }
    }

    // Counts the contexts a service opens, so a test can tell whether a database step ran.
    internal sealed class CountingContexts(TestDbContextFactory inner) : IDbContextFactory<AppDbContext>
    {
        private int _opened;

        public int Opened => Volatile.Read(ref _opened);

        public AppDbContext CreateDbContext()
        {
            Interlocked.Increment(ref _opened);
            return inner.CreateDbContext();
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _opened);
            return inner.CreateDbContextAsync(cancellationToken);
        }
    }
}
