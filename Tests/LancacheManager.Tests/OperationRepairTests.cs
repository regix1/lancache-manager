using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
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

    [Fact]
    public async Task StartupOwnershipFailureKeepsAdmissionUnavailableAndCorrectedFileRecoversAsync()
    {
        var path = RepairFilePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = "not-json";
        File.WriteAllText(path, original);

        await using (var failed = await RepairHarness.CreateAsync(_root, start: false))
        {
            using var host = failed.CreateHost();
            await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(
                () => failed.Owner.WaitForRecoveryOwnershipAsync(CancellationToken.None));
            Assert.Equal(original, File.ReadAllText(path));
        }

        var unsupported = NewLogProcessingRepair();
        var unsupportedJson = JsonSerializer.SerializeToNode(new[] { unsupported })!.AsArray();
        unsupportedJson[0]![nameof(OperationRepair.Version)] = OperationRepair.CurrentVersion + 1;
        var unsupportedBytes = unsupportedJson.ToJsonString();
        File.WriteAllText(path, unsupportedBytes);

        await using (var failed = await RepairHarness.CreateAsync(_root, start: false))
        {
            using var host = failed.CreateHost();
            await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(
                () => failed.Owner.WaitForRecoveryOwnershipAsync(CancellationToken.None));
            Assert.Equal(unsupportedBytes, File.ReadAllText(path));
        }

        File.WriteAllText(path, JsonSerializer.Serialize(Array.Empty<OperationRepair>()));
        await using var corrected = await RepairHarness.CreateAsync(_root, start: false);
        using var correctedHost = corrected.CreateHost();
        await correctedHost.StartAsync(CancellationToken.None);
        await corrected.Owner.WaitForRecoveryOwnershipAsync(CancellationToken.None);
        Assert.Empty(corrected.Owner.GetPendingRepairs());
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
        await using var harness = await RepairHarness.CreateAsync(_root, stateService: state);
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

    [Theory]
    [InlineData(OperationType.CacheClearing, false)]
    [InlineData(OperationType.CacheClearing, true)]
    [InlineData(OperationType.LogRemoval, false)]
    [InlineData(OperationType.LogRemoval, true)]
    public async Task RootDependentRepairRejectsChangedOrMissingDatasourceBeforeMutationAsync(
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
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var retryStarted = new TaskCompletionSource<DateTime>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RepairOwner? owner = null;
        await using var harness = await RepairHarness.CreateAsync(
            stateRoot,
            start: false,
            stateService: state,
            apply: (pending, stoppingToken) => owner!.ApplyBaseAsync(pending, stoppingToken),
            now: () => now,
            waitUntil: async (retryAt, cancellationToken) =>
            {
                retryStarted.TrySetResult(retryAt);
                await releaseRetry.Task.WaitAsync(cancellationToken);
            },
            registrations: services => services
                .AddSingleton(datasources)
                .AddSingleton(capabilities));
        owner = Assert.IsType<RepairOwner>(harness.Owner);

        source.NativeLaunchAuthorized = true;
        await Assert.ThrowsAsync<InvalidDataException>(
            () => owner.ApplyBaseAsync(repair, CancellationToken.None));
        source.NativeLaunchAuthorized = false;

        await owner.StartAsync(CancellationToken.None);
        var terminalEvents = 0;
        void OnTerminal(OperationInfo _) => Interlocked.Increment(ref terminalEvents);
        harness.Tracker.OperationTerminal += OnTerminal;
        Task? finish = null;
        try
        {
            await owner.PrepareRepairAsync(repair, CancellationToken.None);
            await owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
            finish = owner.FinishRepairAsync(
                repair.Id,
                success: false,
                cancelled: true,
                error: "requested cancellation");
            var retryAt = await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(now.AddMinutes(1), retryAt);
            var stored = Assert.Single(state.LoadOperationRepairs());
            Assert.Equal(repair.Id, stored.Id);
            Assert.Equal(type, stored.Type);
            Assert.Equal(OperationRepairPhase.Repairing, stored.Phase);
            Assert.Equal(OperationStatus.Cancelled, stored.Outcome);
            Assert.Equal("requested cancellation", stored.Error);
            Assert.Equal(retryAt, stored.RetryAtUtc);
            var storedSource = Assert.Single(stored.Sources);
            Assert.Equal("alpha", storedSource.Datasource);
            Assert.Equal(source.CacheRoot, storedSource.CacheRoot);
            Assert.Equal(source.LogRoot, storedSource.LogRoot);
            Assert.True(storedSource.NativeLaunchAuthorized);
            Assert.False(finish.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref terminalEvents));
            Assert.Equal(73, state.GetLogPosition("alpha"));
            Assert.Equal(91, state.GetLogPosition("beta"));
            Assert.Equal("captured", File.ReadAllText(capturedSentinel));
            Assert.Equal("foreign", File.ReadAllText(foreignSentinel));
        }
        finally
        {
            harness.Lifetime.StopApplication();
            releaseRetry.TrySetResult(true);
            if (finish is not null)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => finish.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            await owner.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            harness.Tracker.OperationTerminal -= OnTerminal;
        }

        var pending = Assert.Single(state.LoadOperationRepairs());
        Assert.Equal(repair.Id, pending.Id);
        Assert.Equal(type, pending.Type);
        Assert.Equal(OperationRepairPhase.Repairing, pending.Phase);
        Assert.Equal(OperationStatus.Cancelled, pending.Outcome);
        Assert.NotNull(pending.RetryAtUtc);
        Assert.Equal(0, Volatile.Read(ref terminalEvents));
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

        var first = NewLogProcessingRepair();
        var second = NewLogProcessingRepair();
        second.Name = "Second log processing";
        await harness.Owner.PrepareRepairAsync(first, CancellationToken.None);
        await harness.Owner.PrepareRepairAsync(second, CancellationToken.None);
        await harness.Owner.StartWorkAsync(first.Id, "alpha", CancellationToken.None);
        await harness.Owner.StartWorkAsync(second.Id, "alpha", CancellationToken.None);

        var firstFinish = harness.Owner.FinishRepairAsync(first.Id, true, false, null);
        var secondFinish = harness.Owner.FinishRepairAsync(second.Id, false, true, "cancelled");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref active));
        releaseFirst.TrySetResult(true);
        await Task.WhenAll(firstFinish, secondFinish).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, calls);
        Assert.Equal(1, maximum);
        Assert.Empty(harness.Owner.GetPendingRepairs());
        Assert.Empty(harness.StateService.LoadOperationRepairs());
        Assert.Contains(OperationStatus.Completed, outcomes);
        Assert.Contains(OperationStatus.Cancelled, outcomes);
    }

    [Fact]
    public async Task DuplicateFinishCallersAwaitOneRepairTaskAsync()
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
        var repair = NewLogProcessingRepair();
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        var first = harness.Owner.FinishRepairAsync(repair.Id, true, false, null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = harness.Owner.FinishRepairAsync(repair.Id, false, true, "late cancellation");
        release.TrySetResult(true);
        await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, calls);
        Assert.Equal(OperationStatus.Completed, acceptedOutcome);
        Assert.Empty(harness.StateService.LoadOperationRepairs());
        await harness.Owner.FinishRepairAsync(repair.Id, false, true, "late cancellation");
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Owner.FinishRepairAsync(Guid.NewGuid(), true, false, null));
    }

    [Fact]
    public async Task FailedRepairStartWriteStaysOwnedAndRetriesAtStoredTimeAsync()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var state = CreateFailingStateService(_root);
        var firstWait = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWait = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waits = 0;
        var calls = 0;
        DateTime? acceptedRetryAt = null;
        await using var harness = await RepairHarness.CreateAsync(
            _root,
            stateService: state,
            now: () => now,
            waitUntil: async (expiry, cancellationToken) =>
            {
                var wait = Interlocked.Increment(ref waits);
                (wait == 1 ? firstWait : secondWait).TrySetResult(expiry);
                await (wait == 1 ? releaseFirst.Task : releaseSecond.Task)
                    .WaitAsync(cancellationToken);
                now = expiry;
            },
            apply: (accepted, _) =>
            {
                acceptedRetryAt = accepted.RetryAtUtc;
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });
        var repair = NewLogProcessingRepair();
        await harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
        await harness.Owner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);

        state.FailRepairStarts = 2;
        var finish = harness.Owner.FinishRepairAsync(repair.Id, true, false, null);
        var firstRetryAt = await firstWait.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(now.AddMinutes(1), firstRetryAt);
        Assert.False(finish.IsCompleted);
        var pending = Assert.Single(harness.Owner.GetPendingRepairs());
        Assert.Equal(OperationRepairPhase.Running, pending.Phase);
        Assert.Null(pending.RetryAtUtc);
        Assert.Equal(0, calls);

        var separate = NewLogProcessingRepair();
        separate.Name = "Separate log processing";
        await harness.Owner.PrepareRepairAsync(separate, CancellationToken.None);

        releaseFirst.TrySetResult(true);
        var secondRetryAt = await secondWait.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstRetryAt.AddMinutes(1), secondRetryAt);
        Assert.False(finish.IsCompleted);

        releaseSecond.TrySetResult(true);
        await finish.WaitAsync(TimeSpan.FromSeconds(5));

        var stored = Assert.Single(harness.StateService.LoadOperationRepairs());
        Assert.Equal(separate.Id, stored.Id);
        Assert.Equal(OperationRepairPhase.Prepared, stored.Phase);
        Assert.Equal(1, calls);
        Assert.Equal(secondRetryAt, acceptedRetryAt);
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

        var finish = harness.Owner.FinishRepairAsync(repair.Id, true, false, null);
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(receiptPath));
        Assert.Equal(
            OperationRepairPhase.Repairing,
            Assert.Single(harness.Owner.GetPendingRepairs()).Phase);

        releaseRetry.TrySetResult(true);
        await finish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(receiptPath));
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
            Assert.False(finish.IsCompleted);
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
                ["cache_steam_remove", "cache_eviction_scan"]),
            (NewRemovalRepair(OperationType.GameRemoval, new CacheRepairTarget { EpicGame = "fortnite" }),
                ["cache_epic_remove", "cache_eviction_scan"]),
            (NewRemovalRepair(
                    OperationType.GameRemoval,
                    new CacheRepairTarget { GameName = "Diablo IV", Service = "blizzard" }),
                ["cache_blizzard_remove", "cache_eviction_scan"]),
            (NewRemovalRepair(
                    OperationType.GameRemoval,
                    new CacheRepairTarget { GameName = "League of Legends", Service = "riot" }),
                ["cache_riot_remove", "cache_eviction_scan"]),
            (NewRemovalRepair(
                    OperationType.GameRemoval,
                    new CacheRepairTarget { GameName = "Halo Infinite", Service = "xbox" }),
                ["cache_xbox_remove", "cache_eviction_scan"]),
            (NewRemovalRepair(OperationType.ServiceRemoval, new CacheRepairTarget { Service = "steam" }),
                ["cache_service_remove", "cache_eviction_scan"]),
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
    public async Task ForceKillLeavesOwnerCompletionOperationLiveUntilOwnerPublishesAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var cancellation = new OperationCancellationService(
            tracker,
            processManager,
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
        var live = Assert.IsType<OperationInfo>(tracker.GetOperation(operationId));
        Assert.Equal(0, live.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelling, live.Status);
        Assert.False(emitted.Task.IsCompleted);

        tracker.CompleteOperation(operationId, success: false, error: "Force killed by user", cancelled: true);
        await emitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, live.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelled, live.Status);
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
        Assert.Equal(0, promoted.CompletedFlag);
        Assert.Equal(OperationStatus.Cancelling, promoted.Status);
        Assert.Equal(0, emitted);

        tracker.CompleteOperation(operationId, success: false, cancelled: true);
        Assert.Equal(1, emitted);
        Assert.Equal(1, promoted.CompletedFlag);
    }

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

    private static StateService CreateStateService(string root)
    {
        var parts = StateParts(root);
        return new StateService(
            NullLogger<StateService>.Instance,
            parts.Resolver,
            parts.Encryption,
            parts.SteamAuthStorage);
    }

    private static FailingStateService CreateFailingStateService(string root)
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

    private sealed class FailingStateService : StateService
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

        public WaitingProcessManager()
            : base(NullLogger<ProcessManager>.Instance)
        {
        }

        public override async Task WaitForProcessesExitAsync(
            IReadOnlyCollection<string> processNames,
            CancellationToken cancellationToken)
        {
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

        private RepairHarness(
            string root,
            StateService? stateService,
            ProcessManager? processManager,
            Func<OperationRepair, CancellationToken, Task>? apply,
            Func<DateTime>? now,
            Func<DateTime, CancellationToken, Task>? waitUntil,
            Action<Guid, CancellationToken>? onRestore,
            Action<IServiceCollection>? registrations)
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
            Owner = new RepairOwner(
                NullLogger<OperationStateService>.Instance,
                new ConfigurationBuilder().Build(),
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
            Action<IServiceCollection>? registrations = null)
        {
            var harness = new RepairHarness(
                root,
                stateService,
                processManager,
                apply,
                now,
                waitUntil,
                onRestore,
                registrations);
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
            if (!_operationTracker.TryRestoreOperation(
                    repair.Id,
                    repair.Type,
                    repair.Name,
                    source,
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
            return Task.CompletedTask;
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
}
