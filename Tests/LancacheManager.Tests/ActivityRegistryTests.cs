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

    [Fact]
    public async Task BoundDownloadSnapshotSuppliesMembershipStampAndBoundary()
    {
        var activity = CreateRegistry(out _);
        var activeUntilUtc = new DateTime(2026, 9, 30, 18, 0, 15, DateTimeKind.Utc);
        var downloads = SpeedActivityTests.Snapshot(
            activeUntilUtc.AddSeconds(-13),
            activeUntilUtc,
            isAvailable: true);
        activity.BindDownloads(() => downloads);

        var snapshot = await activity.GetSnapshotAsync();

        Assert.Equal(downloads.StreamId, snapshot.DownloadStreamId);
        Assert.Equal(downloads.Revision, snapshot.DownloadRevision);
        Assert.Contains(snapshot.Activities, item =>
            item.Domain == ActivityDomains.Download &&
            item.Key == downloads.GameSpeeds[0].Key &&
            item.ActiveUntilUtc == activeUntilUtc);
        Assert.Contains(snapshot.Activities, item =>
            item.Domain == ActivityDomains.Download &&
            item.Key == downloads.ClientSpeeds[0].ClientIp &&
            item.ActiveUntilUtc == activeUntilUtc);
    }

    [Fact]
    public async Task OlderDownloadReplacementCannotRestoreClearedMembership()
    {
        var activity = CreateRegistry(out _);
        var activeUntilUtc = new DateTime(2026, 9, 30, 18, 0, 15, DateTimeKind.Utc);
        var active = SpeedActivityTests.Snapshot(
            activeUntilUtc.AddSeconds(-13),
            activeUntilUtc,
            isAvailable: true);
        await activity.ReplaceDownloadsAsync(active);
        await activity.ReplaceDownloadsAsync(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = active.StreamId,
            Revision = active.Revision + 1,
            TimestampUtc = activeUntilUtc,
            IsAvailable = true,
            WindowSeconds = 2,
        });
        await activity.ReplaceDownloadsAsync(active);

        var snapshot = await activity.GetSnapshotAsync();
        Assert.Equal(active.Revision + 1, snapshot.DownloadRevision);
        Assert.DoesNotContain(snapshot.Activities, item => item.Domain == ActivityDomains.Download);
    }

    [Fact]
    public async Task NewerEquivalentDownloadSnapshotAdvancesStampWithoutBroadcast()
    {
        var activity = CreateRegistry(out var notifier);
        var activeUntilUtc = new DateTime(2026, 9, 30, 18, 0, 15, DateTimeKind.Utc);
        var first = SpeedActivityTests.Snapshot(
            activeUntilUtc.AddSeconds(-13),
            activeUntilUtc,
            isAvailable: true);
        await activity.ReplaceDownloadsAsync(first);

        var next = SpeedActivityTests.Snapshot(
            activeUntilUtc.AddSeconds(-13),
            activeUntilUtc,
            isAvailable: true);
        next.Revision = first.Revision + 1;
        next.TimestampUtc = first.TimestampUtc.AddSeconds(1);
        await activity.ReplaceDownloadsAsync(next);

        Assert.Single(notifier.ActivitySnapshots);
        var current = await activity.GetSnapshotAsync();
        Assert.Equal(next.StreamId, current.DownloadStreamId);
        Assert.Equal(next.Revision, current.DownloadRevision);

        next.StreamId = "replacement-stream";
        next.Revision = 1;
        await activity.ReplaceDownloadsAsync(next);

        Assert.Equal(2, notifier.ActivitySnapshots.Count);
        Assert.Equal(next.StreamId, notifier.ActivitySnapshots[^1].DownloadStreamId);
        Assert.Equal(next.Revision, notifier.ActivitySnapshots[^1].DownloadRevision);
    }

    [Fact]
    public async Task NonDownloadReportAppliesNewestBoundDownloadSnapshot()
    {
        var activity = CreateRegistry(out _);
        var activeUntilUtc = new DateTime(2026, 9, 30, 18, 0, 15, DateTimeKind.Utc);
        var current = SpeedActivityTests.Snapshot(
            activeUntilUtc.AddSeconds(-13),
            activeUntilUtc,
            isAvailable: true);
        activity.BindDownloads(() => current);
        _ = await activity.GetSnapshotAsync();
        current = new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = current.StreamId,
            Revision = current.Revision + 1,
            TimestampUtc = activeUntilUtc,
            IsAvailable = true,
            WindowSeconds = 2,
        };

        await activity.ReportAsync(ActivityDomains.Schedule, "logRotation", ActivityAspects.Running, true);
        var snapshot = await activity.GetSnapshotAsync();

        Assert.Equal(current.Revision, snapshot.DownloadRevision);
        Assert.DoesNotContain(snapshot.Activities, item => item.Domain == ActivityDomains.Download);
        Assert.Contains(snapshot.Activities, item =>
            item.Domain == ActivityDomains.Schedule && item.Key == "logRotation");
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
        public Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToBattleNetPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToRiotPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToXboxPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
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
