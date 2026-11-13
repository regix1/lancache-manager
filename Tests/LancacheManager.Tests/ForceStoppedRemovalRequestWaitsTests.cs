using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A game or service removal has no run hold of its own: its saved repair keeps a second job out after a force stop.
/// A user force stops a removal while its work is held in a step that does not see the stop (a file delete in
/// flight), then asks for the same removal again, or for a job that conflicts with a live removal, the way the
/// controllers ask through the operation queue. The answer must be a waiting card, never a start beside the stopped
/// removal. One case force stops a removal before its repair exists.
/// </summary>
public sealed class ForceStoppedRemovalRequestWaitsTests
{
    private const long AppId = 480;
    private const string Game = "Spacewar";
    private const string ServiceName = "steam";

    [Fact]
    public async Task AGameRemovalAskedForWhileAForceStoppedRemovalOfTheSameGameStillDeletes_WaitsOnACard()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var removals = new Removals(host);
        var held = new HeldWork();
        try
        {
            var stopped = await removals.StartAndForceStopWhileWorkingAsync(Removals.GameRemoval, held);

            var (outcome, _) = await host.AskThroughTheQueueAsync(
                OperationType.GameRemoval, ConflictScope.SteamGame(AppId), Removals.GameRemoval.Label,
                async () => await removals.StartAsync(Removals.GameRemoval, new HeldWork()));
            AssertWaits("game removal of the same game", "game removal", outcome, removals, stopped);
        }
        finally
        {
            held.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task AServiceRemovalAskedForWhileAForceStoppedRemovalOfTheSameServiceStillDeletes_WaitsOnACard()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var removals = new Removals(host);
        var held = new HeldWork();
        try
        {
            var stopped = await removals.StartAndForceStopWhileWorkingAsync(Removals.ServiceRemoval, held);

            var (outcome, _) = await host.AskThroughTheQueueAsync(
                OperationType.ServiceRemoval, ConflictScope.Service(ServiceName), Removals.ServiceRemoval.Label,
                async () => await removals.StartAsync(Removals.ServiceRemoval, new HeldWork()));
            AssertWaits("service removal of the same service", "service removal", outcome, removals, stopped);
        }
        finally
        {
            held.Release.TrySetResult();
        }
    }

    /// <summary>
    /// Edge: a per-entity cache file count is not one of the types a pending repair blocks
    /// (OperationConflictChecker.RepairBlocks), yet it conflicts with a live removal of the same game.
    /// </summary>
    [Fact]
    public async Task AFileCountForTheSameGameAskedForWhileAForceStoppedGameRemovalStillDeletes_WaitsOnACard()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var removals = new Removals(host);
        var held = new HeldWork();
        try
        {
            var stopped = await removals.StartAndForceStopWhileWorkingAsync(Removals.GameRemoval, held);

            var (outcome, _) = await host.AskThroughTheQueueAsync(
                OperationType.CacheFileCount, ConflictScope.SteamGame(AppId), $"Cache File Count ({Game})",
                () => Task.FromResult<Guid?>(host.Tracker.RegisterOperation(
                    OperationType.CacheFileCount, $"Cache File Count ({Game})", new CancellationTokenSource())));
            AssertWaits("cache file count of the same game", "game removal", outcome, removals, stopped);
        }
        finally
        {
            held.Release.TrySetResult();
        }
    }

    /// <summary>
    /// Edge: the removal is force stopped while its start, which the controller runs inside the queue, is held in its
    /// started send, before its worker makes the repair, so the force stop finds no repair and ends the card at once.
    /// A second removal of the same game asked for then must not start while that start is held, and the stopped
    /// removal must never reach its work or leave a repair once it goes on.
    /// </summary>
    [Fact]
    public async Task AGameRemovalForceStoppedBeforeItsRepairExists_DoesNoWorkAndTheNextRemovalStartsOnlyAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var removals = new Removals(host);
        var first = new HeldWork();
        var next = new HeldWork();
        try
        {
            host.Notifications.Hold(SignalREvents.GameRemovalStarted);
            var firstAnswer = host.Queue.EnqueueAsync(
                OperationType.GameRemoval, ConflictScope.SteamGame(AppId), Removals.GameRemoval.Label,
                async () => await removals.StartAsync(Removals.GameRemoval, first),
                CancellationToken.None, OperationRequestKey.Plain, target: Game);
            await host.Notifications.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
            var stopped = Assert.Single(host.Tracker.GetActiveOperations(OperationType.GameRemoval)).Id;
            Assert.DoesNotContain(removals.Repairs.GetPendingRepairs(), repair => repair.Id == stopped);
            await host.ForceStopAsync(stopped);

            var nextCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextAnswer = host.Queue.EnqueueAsync(
                OperationType.GameRemoval, ConflictScope.SteamGame(AppId), Removals.GameRemoval.Label,
                async () =>
                {
                    nextCalled.TrySetResult();
                    return await removals.StartAsync(Removals.GameRemoval, next);
                },
                CancellationToken.None, OperationRequestKey.Plain, target: Game);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(nextCalled.Task.IsCompleted,
                "A game removal asked for while the force-stopped removal's start was still held started at once");

            host.Notifications.Release();
            await firstAnswer.WaitAsync(ForceStoppedOwnerHost.Timeout);
            await nextAnswer.WaitAsync(ForceStoppedOwnerHost.Timeout);
            await next.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(first.Entered.Task.IsCompleted,
                "The removal force stopped before its repair existed went on to its work after the stop");
            Assert.DoesNotContain(removals.Repairs.GetPendingRepairs(), repair => repair.Id == stopped);
        }
        finally
        {
            first.Release.TrySetResult();
            next.Release.TrySetResult();
        }
    }

    private static void AssertWaits(string asked, string stoppedJob, string outcome, Removals removals, Guid stopped)
    {
        var repair = removals.Repairs.GetPendingRepairs().FirstOrDefault(pending => pending.Id == stopped);
        Assert.True(outcome == ForceStoppedOwnerHost.Waits,
            $"A {asked} asked for while the force-stopped {stoppedJob}'s work still ran was {outcome}; it must be " +
            $"{ForceStoppedOwnerHost.Waits} until that work has ended (the stopped removal's repair: " +
            $"{(repair is null ? "none" : $"{repair.Phase}")})");
    }

    /// <summary>A removal's work: it waits for <see cref="Release"/> and does not see the stop.</summary>
    private sealed class HeldWork
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RemovalKind(
        OperationType Type, string Label, string TargetKind, string TargetKey, string TargetName, CacheRepairTarget Target,
        string StartedEvent, string ProgressEvent, string CompleteEvent);

    /// <summary>
    /// Starts a removal through the runner the controllers use, with the real repair calls of
    /// <see cref="CacheManagementService"/>; only the deleting work is the test's.
    /// </summary>
    private sealed class Removals(ForceStoppedOwnerHost host)
    {
        public static readonly RemovalKind GameRemoval = new(
            OperationType.GameRemoval, $"Game Removal ({Game})", "steam", AppId.ToString(), Game,
            new CacheRepairTarget { SteamAppId = (uint)AppId },
            SignalREvents.GameRemovalStarted, SignalREvents.GameRemovalProgress, SignalREvents.GameRemovalComplete);

        public static readonly RemovalKind ServiceRemoval = new(
            OperationType.ServiceRemoval, $"Service Removal ({ServiceName})", "service", ServiceName, ServiceName,
            new CacheRepairTarget { Service = ServiceName },
            SignalREvents.ServiceRemovalStarted, SignalREvents.ServiceRemovalProgress, SignalREvents.ServiceRemovalComplete);

        private readonly CacheManagementService _management = host.Create<CacheManagementService>();

        public OperationStateService Repairs { get; } = host.Services.GetRequiredService<OperationStateService>();

        public Task<Guid> StartAsync(RemovalKind kind, HeldWork work)
        {
            var metrics = new RemovalMetrics
            {
                EntityKey = kind.TargetKey,
                EntityName = kind.TargetName,
                EntityKind = kind.TargetKind
            };
            return TrackedRemovalOperationRunner.StartAsync(
                host.Tracker,
                (ISignalRNotificationService)(object)host.Notifications,
                new RemovalOperationConfig<string>(
                    OperationType: kind.Type,
                    OperationLabel: kind.Label,
                    Target: kind.TargetName,
                    Metrics: metrics,
                    StartedEventName: kind.StartedEvent,
                    BuildStarted: id => id,
                    ProgressEventName: kind.ProgressEvent,
                    InitialStageKey: "signalr.gameRemove.starting",
                    BuildInitialProgress: id => id,
                    BuildProgress: (id, _) => id,
                    CompleteEventName: kind.CompleteEvent,
                    FinalizingStageKey: "signalr.gameRemove.finalizing",
                    BuildFinalizingProgress: (id, _) => id,
                    BuildSuccess: (id, _) => id,
                    BuildCanceled: id => id,
                    BuildErrorProgress: (id, _) => id,
                    BuildErrorComplete: (_, _) => DispatchProxy.Create<IOperationComplete, NullReturningProxy>(),
                    ExecuteAsync: async (id, cancellationToken, _) =>
                    {
                        // As the real removal does before it deletes a datasource's files.
                        await Repairs.StartWorkAsync(id, "alpha", cancellationToken);
                        work.Entered.TrySetResult();
                        await work.Release.Task;
                        return "removed";
                    },
                    BuildRepair: id => _management.BuildRemovalRepair(id, kind.Type, kind.Label, metrics, kind.Target),
                    PrepareRepairAsync: _management.PrepareRepairAsync,
                    FinishRepairAsync: _management.FinishRemovalRepairAsync));
        }

        /// <summary>Starts the removal through the queue as its controller does, waits until its work is held, and
        /// force stops it.</summary>
        public async Task<Guid> StartAndForceStopWhileWorkingAsync(RemovalKind kind, HeldWork work)
        {
            var scope = kind.Type == OperationType.GameRemoval ? ConflictScope.SteamGame(AppId) : ConflictScope.Service(ServiceName);
            var (_, started) = await host.AskThroughTheQueueAsync(kind.Type, scope, kind.Label, async () => await StartAsync(kind, work));
            var stopped = Assert.IsType<Guid>(await started.WaitAsync(ForceStoppedOwnerHost.Timeout));
            await work.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
            Assert.Contains(Repairs.GetPendingRepairs(), repair => repair.Id == stopped);
            await host.ForceStopAsync(stopped);
            return stopped;
        }
    }
}
