using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A removal restored after a restart ends with the key its run saved. The completion event words the
/// failure by that key, as the live ending did, so a refusal (a missing log, logs not read yet) or a run the
/// restart stopped is named on the card instead of the removal's generic failure.
/// </summary>
public sealed class RestoredRemovalFailureKeyTests
{
    private const string RefusalKey = "errors.unmappedRemoval.logsMissing";

    [Fact]
    public async Task ARestoredServiceRemovalRefusalSendsTheRefusalKeyAsync()
    {
        var complete = await RestoreAndFailAsync<SignalRNotifications.ServiceRemovalComplete>(
            ServiceRepair(), RefusalKey, failureReason: null);

        Assert.False(complete.Success);
        Assert.Equal(RefusalKey, complete.StageKey);
        Assert.Null(complete.Error);
        Assert.Equal("steam", complete.Context!["name"]);
    }

    [Fact]
    public async Task ARestoredServiceRefusalWithValuesSendsTheKeyAndItsValuesAsync()
    {
        var complete = await RestoreAndFailAsync<SignalRNotifications.ServiceRemovalComplete>(
            ServiceRepair(),
            "errors.logs.startFailedForDatasource",
            new RunWarning("errors.logs.startFailedForDatasource", new Dictionary<string, object?> { ["datasource"] = "beta" }));

        Assert.Equal("errors.logs.startFailedForDatasource", complete.StageKey);
        Assert.Equal("beta", complete.Context!["datasource"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARestoredGameRemovalRefusalSendsTheRefusalKeyAsync(bool epic)
    {
        var complete = await RestoreAndFailAsync<SignalRNotifications.GameRemovalComplete>(
            GameRepair(epic), RefusalKey, failureReason: null);

        Assert.False(complete.Success);
        Assert.Equal(RefusalKey, complete.StageKey);
        Assert.Null(complete.Error);
    }

    [Fact]
    public async Task ARestoredGameRemovalTheRestartStoppedSaysSoAsync()
    {
        var complete = await RestoreAndFailAsync<SignalRNotifications.GameRemovalComplete>(
            GameRepair(epic: false), OperationStateService.InterruptedByRestartError, failureReason: null);

        Assert.Equal(OperationStateService.InterruptedByRestartError, complete.StageKey);
    }

    [Fact]
    public async Task ARestoredUnkeyedFailureKeepsTheRemovalKeyWithItsTextAsync()
    {
        const string generic = "signalr.generic.failedWithDetail";
        var reason = new RunWarning(generic, new Dictionary<string, object?> { ["errorDetail"] = "The cache disk went away" });

        var service = await RestoreAndFailAsync<SignalRNotifications.ServiceRemovalComplete>(ServiceRepair(), generic, reason);
        var game = await RestoreAndFailAsync<SignalRNotifications.GameRemovalComplete>(GameRepair(epic: false), generic, reason);

        Assert.Equal("signalr.serviceRemove.error.default", service.StageKey);
        Assert.Equal("The cache disk went away", service.Context!["errorDetail"]);
        Assert.Equal("signalr.gameRemove.error.fatal", game.StageKey);
        Assert.Equal("The cache disk went away", game.Context!["errorDetail"]);
    }

    private static OperationRepair ServiceRepair() => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.ServiceRemoval,
        Name = "Service removal: steam",
        StartedAt = DateTime.UtcNow.AddMinutes(-1),
        Outcome = OperationStatus.Failed,
        Target = new CacheRepairTarget { Service = "steam" },
        Removal = new RemovalRepair { EntityKey = "steam", EntityName = "steam", EntityKind = "service" }
    };

    private static OperationRepair GameRepair(bool epic) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.GameRemoval,
        Name = "Game Removal: Dota 2",
        StartedAt = DateTime.UtcNow.AddMinutes(-1),
        Outcome = OperationStatus.Failed,
        Target = epic
            ? new CacheRepairTarget { EpicGame = "Fortnite", Service = "epicgames" }
            : new CacheRepairTarget { SteamAppId = 570 },
        Removal = new RemovalRepair
        {
            EntityKey = epic ? "Fortnite" : "570",
            EntityName = epic ? "Fortnite" : "Dota 2",
            EntityKind = epic ? "epic" : "steam"
        }
    };

    /// <summary>
    /// Restores the run as a restart does, then ends it failed with the saved key and the failure values
    /// the record kept, the way the restart's completion from the record does.
    /// </summary>
    private static async Task<T> RestoreAndFailAsync<T>(OperationRepair repair, string savedError, RunWarning? failureReason)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = (CompleteRecordingProxy)(object)DispatchProxy
            .Create<ISignalRNotificationService, CompleteRecordingProxy>();
        var manager = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        typeof(CacheManagementService)
            .GetField("_operationTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, tracker);
        typeof(CacheManagementService)
            .GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, (ISignalRNotificationService)(object)notifications);

        await manager.RestoreRepairAsync(repair, CancellationToken.None);
        tracker.CompleteOperation(repair.Id, success: false, error: savedError, canceled: false,
            onCompleting: operation =>
            {
                if (failureReason is not null) operation.FailureReason = failureReason;
            });

        var sent = await notifications.Sent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return Assert.IsType<T>(sent);
    }

    /// <summary>Records the first removal completion event. Not sealed for DispatchProxy.</summary>
    private class CompleteRecordingProxy : NullReturningProxy
    {
        public TaskCompletionSource<object> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [string eventName, { } value, ..]
                && (eventName == SignalREvents.ServiceRemovalComplete || eventName == SignalREvents.GameRemovalComplete))
            {
                Sent.TrySetResult(value);
            }

            return base.Invoke(targetMethod, args);
        }
    }
}
