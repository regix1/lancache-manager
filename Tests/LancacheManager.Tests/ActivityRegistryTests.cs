using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Behaviour of the unified activity/presence registry: reports only broadcast on a real change,
/// ReplaceAsync clears stale keys, the revision counter is monotonic, and the snapshot reflects state.
/// </summary>
public class ActivityRegistryTests
{
    private static ActivityRegistry CreateRegistry(out RecordingNotifier notifier)
    {
        notifier = new RecordingNotifier();
        return new ActivityRegistry(notifier, NullLogger<ActivityRegistry>.Instance);
    }

    [Fact]
    public async Task ReportAsync_ActiveThenInactive_BroadcastsEachTransition()
    {
        var registry = CreateRegistry(out var notifier);

        await registry.ReportAsync(ActivityDomains.Schedule, "depotMapping", ActivityAspects.Running, true);
        await registry.ReportAsync(ActivityDomains.Schedule, "depotMapping", ActivityAspects.Running, false);

        Assert.Equal(2, notifier.ActivitySnapshots.Count);
        Assert.Contains(
            notifier.ActivitySnapshots[0].Activities,
            a => a.Domain == ActivityDomains.Schedule && a.Key == "depotMapping" && a.Aspect == ActivityAspects.Running);
        Assert.Empty(notifier.ActivitySnapshots[1].Activities);
    }

    [Fact]
    public async Task ReportAsync_RedundantReport_DoesNotBroadcastAgain()
    {
        var registry = CreateRegistry(out var notifier);

        await registry.ReportAsync(ActivityDomains.Operation, "gameDetection", ActivityAspects.Running, true);
        await registry.ReportAsync(ActivityDomains.Operation, "gameDetection", ActivityAspects.Running, true);

        Assert.Single(notifier.ActivitySnapshots);
    }

    [Fact]
    public async Task ReplaceAsync_SetsExactMembershipAndClearsStale()
    {
        var registry = CreateRegistry(out var notifier);

        await registry.ReplaceAsync(ActivityDomains.Schedule, ActivityAspects.Running,
            new Dictionary<string, int> { ["logRotation"] = 1, ["gameDetection"] = 1 });
        await registry.ReplaceAsync(ActivityDomains.Schedule, ActivityAspects.Running,
            new Dictionary<string, int> { ["logRotation"] = 1 });

        var latest = notifier.ActivitySnapshots[^1];
        Assert.Contains(latest.Activities, a => a.Key == "logRotation");
        Assert.DoesNotContain(latest.Activities, a => a.Key == "gameDetection");
    }

    [Fact]
    public async Task ReplaceAsync_DoesNotTouchOtherDomains()
    {
        var registry = CreateRegistry(out var notifier);

        await registry.ReportAsync(ActivityDomains.Download, "steam", ActivityAspects.Downloading, true);
        // Replacing the (schedule, running) set must not clear the (download, downloading) entry.
        await registry.ReplaceAsync(ActivityDomains.Schedule, ActivityAspects.Running,
            new Dictionary<string, int> { ["logRotation"] = 1 });

        var latest = notifier.ActivitySnapshots[^1];
        Assert.Contains(latest.Activities, a => a.Domain == ActivityDomains.Download && a.Key == "steam");
        Assert.Contains(latest.Activities, a => a.Domain == ActivityDomains.Schedule && a.Key == "logRotation");
    }

    [Fact]
    public async Task Revision_IsMonotonic()
    {
        var registry = CreateRegistry(out var notifier);

        await registry.ReportAsync(ActivityDomains.Schedule, "a", ActivityAspects.Running, true);
        await registry.ReportAsync(ActivityDomains.Schedule, "b", ActivityAspects.Running, true);
        await registry.ReportAsync(ActivityDomains.Schedule, "a", ActivityAspects.Running, false);

        var revisions = notifier.ActivitySnapshots.Select(s => s.Revision).ToList();
        for (var i = 1; i < revisions.Count; i++)
        {
            Assert.True(revisions[i] > revisions[i - 1], "Revision must strictly increase per broadcast");
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_ReflectsCurrentActiveSet()
    {
        var registry = CreateRegistry(out _);

        await registry.ReportAsync(ActivityDomains.Integration, "steam", ActivityAspects.Authenticated, true);

        var snapshot = await registry.GetSnapshotAsync();
        Assert.Contains(
            snapshot.Activities,
            a => a.Domain == ActivityDomains.Integration && a.Key == "steam" && a.Aspect == ActivityAspects.Authenticated);
    }

    /// <summary>
    /// Records every <c>ActivityUpdated</c> payload; all other notification methods are inert no-ops.
    /// </summary>
    private sealed class RecordingNotifier : ISignalRNotificationService
    {
        public List<ActivitySnapshot> ActivitySnapshots { get; } = new();

        public Task NotifyAllAsync(string eventName, object? data = null)
        {
            RecordIfActivitySnapshot(eventName, data);
            return Task.CompletedTask;
        }

        public void NotifyAllFireAndForget(string eventName, object? data = null) { }

        private void RecordIfActivitySnapshot(string eventName, object? data)
        {
            if (eventName == SignalREvents.ActivityUpdated && data is ActivitySnapshot snapshot)
            {
                ActivitySnapshots.Add(snapshot);
            }
        }
        public Task NotifyOperationFailedAsync(string eventName, IOperationComplete failedEvent) => Task.CompletedTask;
        public Task NotifyPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyEpicPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifySteamHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyEpicHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyBattleNetPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToBattleNetPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyBattleNetHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyRiotPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToRiotPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyRiotHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyXboxPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToXboxPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyXboxHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyAdminAsync(string eventName, object? data = null)
        {
            // ActivityRegistry broadcasts the full (unfiltered) snapshot to admins - these tests assert
            // against the complete active set, so this is the channel that must be recorded.
            RecordIfActivitySnapshot(eventName, data);
            return Task.CompletedTask;
        }
        public Task NotifyGuestAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyGroupAsync(string groupName, string eventName, object? data = null) => Task.CompletedTask;
    }
}
