using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the ScheduledRunReporter invariants the pipeline-less scheduled services depend on:
/// monotonic percent, a single terminal even under a racing completion, a run-stable showNotification
/// flag stamped into every payload, an awaited run-started ordering, and the success-100 /
/// failure-highest terminal percent.
/// </summary>
public class ScheduledRunReporterTests
{
    private const string StartedEventName = "ProbeRunStarted";
    private const string ProgressEventName = "ProbeRunProgress";
    private const string CompleteEventName = "ProbeRunComplete";

    private static readonly ScheduledRunEventNames Events = new(
        StartedEventName, ProgressEventName, CompleteEventName);

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private static ScheduledRunReporter CreateReporter(
        CapturingNotificationService notifications,
        UnifiedOperationTracker tracker,
        bool showNotification = true)
        => new(
            notifications,
            tracker,
            "probe",
            OperationType.GameDetection,
            Events,
            "probe.complete",
            showNotification,
            CancellationToken.None);

    [Fact]
    public async Task ReportAsync_ClampsPercentMonotonic_WhenALowerValueFollowsAHigherOneAsync()
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker);

        await reporter.StartAsync("probe.starting");
        await reporter.ReportAsync(50, "probe.running");
        await reporter.ReportAsync(30, "probe.running");
        await reporter.CompleteAsync(success: true);

        var progress = notifications.PayloadsFor<ScheduledRunProgressEvent>(ProgressEventName);
        Assert.Equal(2, progress.Count);
        Assert.Equal(50, progress[0].PercentComplete);
        Assert.Equal(50, progress[1].PercentComplete);
    }

    [Fact]
    public async Task StartAsync_IsAwaitedBeforeTheFirstProgressSendAsync()
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker);

        await reporter.StartAsync("probe.starting");
        await reporter.ReportAsync(10, "probe.running");
        await reporter.CompleteAsync(success: true);

        Assert.Equal(StartedEventName, notifications.Events[0].EventName);
        Assert.Equal(ProgressEventName, notifications.Events[1].EventName);
    }

    [Fact]
    public async Task ShowNotification_IsStampedVerbatimIntoEveryPayloadAsync()
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker, showNotification: false);

        await reporter.StartAsync("probe.starting");
        await reporter.ReportAsync(40, "probe.running");
        await reporter.CompleteAsync(success: true);

        var complete = await notifications.WhenEventAsync(CompleteEventName).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(notifications.PayloadsFor<ScheduledRunStartedEvent>(StartedEventName).Single().ShowNotification);
        Assert.False(notifications.PayloadsFor<ScheduledRunProgressEvent>(ProgressEventName).Single().ShowNotification);
        Assert.False(Assert.IsType<ScheduledRunCompleteEvent>(complete.Payload).ShowNotification);
    }

    [Fact]
    public async Task CompleteAsync_SuccessTerminalCarries100Async()
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker);

        await reporter.StartAsync("probe.starting");
        await reporter.ReportAsync(40, "probe.running");
        await reporter.CompleteAsync(success: true);

        var complete = await notifications.WhenEventAsync(CompleteEventName).WaitAsync(TimeSpan.FromSeconds(5));
        var payload = Assert.IsType<ScheduledRunCompleteEvent>(complete.Payload);

        Assert.True(payload.Success);
        Assert.Equal(100, payload.PercentComplete);
        Assert.Null(payload.Error);
    }

    [Fact]
    public async Task CompleteAsync_FailureTerminalCarriesHighestSentPercentAsync()
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker);

        await reporter.StartAsync("probe.starting");
        await reporter.ReportAsync(60, "probe.running");
        await reporter.CompleteAsync(success: false, error: "boom");

        var complete = await notifications.WhenEventAsync(CompleteEventName).WaitAsync(TimeSpan.FromSeconds(5));
        var payload = Assert.IsType<ScheduledRunCompleteEvent>(complete.Payload);

        Assert.False(payload.Success);
        Assert.Equal(60, payload.PercentComplete);
        Assert.Equal("boom", payload.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartAsync_PersistsShowNotificationIntoOperationMetadataAsync(bool showNotification)
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker, showNotification);

        await reporter.StartAsync("probe.starting");

        // The run-status recovery endpoint reads the display flag from the tracked operation's
        // metadata under this exact key; a silent run must not resurface a card on refresh.
        var operation = tracker.GetActiveOperations(OperationType.GameDetection).Single();
        var metadata = Assert.IsType<Dictionary<string, object?>>(operation.Metadata);
        Assert.Equal(showNotification, Assert.IsType<bool>(metadata["showNotification"]));

        await reporter.CompleteAsync(success: true);
    }

    [Fact]
    public async Task ReportAsync_MirrorsLatestContextIntoOperationMetadataAsync()
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker);

        await reporter.StartAsync("probe.starting", new Dictionary<string, object?> { ["processed"] = 0, ["total"] = 8 });
        await reporter.ReportAsync(50, "probe.running", new Dictionary<string, object?> { ["processed"] = 4, ["total"] = 8 });

        // The run-status recovery endpoint reads the latest interpolation context from the tracked
        // operation's metadata under this exact key so a mid-run refresh rehydrates the card with its
        // {{processed}}/{{total}} values rather than a bare stage key.
        var operation = tracker.GetActiveOperations(OperationType.GameDetection).Single();
        var metadata = Assert.IsType<Dictionary<string, object?>>(operation.Metadata);
        var context = Assert.IsType<Dictionary<string, object?>>(metadata["context"]);
        Assert.Equal(4, context["processed"]);
        Assert.Equal(8, context["total"]);

        await reporter.CompleteAsync(success: true);
    }

    [Fact]
    public async Task CompleteAsync_EmitsExactlyOneTerminal_WhenARacingCompletionFollowsAsync()
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker);

        await reporter.StartAsync("probe.starting");
        await reporter.ReportAsync(70, "probe.running");

        var operationId = tracker.GetActiveOperations(OperationType.GameDetection).Single().Id;

        // The reporter completes, then a racing force-kill (e.g. universal cancel) completes the same op.
        await reporter.CompleteAsync(success: true);
        tracker.CompleteOperation(operationId, success: false, error: "duplicate complete should be a no-op");

        await notifications.WhenEventAsync(CompleteEventName).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(notifications.PayloadsFor<ScheduledRunCompleteEvent>(CompleteEventName));
    }

    /// <summary>
    /// Locks the SteamService refresh outcome contract: RefreshMappingsAsync propagates whether the
    /// run ended with usable data (a failed refresh whose database-fallback read also failed returns
    /// false; a fallback read that succeeded returns true), and RefreshWithReportingAsync stamps the
    /// run terminal with that result. A run with no usable data completes as a failure terminal, not
    /// a false success, and the failure keeps the highest progress the refresh reached (its 80%
    /// milestone) rather than snapping to 100.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompleteAsync_SteamRefreshTerminalMirrorsFallbackOutcomeAsync(bool fallbackLoaded)
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = new ScheduledRunReporter(
            notifications,
            tracker,
            "steamService",
            OperationType.SteamServiceRefresh,
            Events,
            "signalr.scheduledRun.steamService.complete",
            showNotification: true,
            CancellationToken.None);

        // The refresh reports its stepped milestones, then RefreshMappingsAsync resolves to the
        // fallback outcome: true when the fallback read produced usable data, false when both the
        // refresh and the fallback read failed.
        await reporter.StartAsync("signalr.scheduledRun.steamService.starting");
        await reporter.ReportAsync(30, "signalr.scheduledRun.steamService.running");
        await reporter.ReportAsync(80, "signalr.scheduledRun.steamService.running");
        var refreshed = fallbackLoaded;
        await reporter.CompleteAsync(refreshed, error: refreshed ? null : "Steam metadata refresh failed");

        var complete = await notifications.WhenEventAsync(CompleteEventName).WaitAsync(TimeSpan.FromSeconds(5));
        var payload = Assert.IsType<ScheduledRunCompleteEvent>(complete.Payload);

        Assert.Equal(fallbackLoaded, payload.Success);
        Assert.Equal(fallbackLoaded ? 100 : 80, payload.PercentComplete);
        Assert.Equal(fallbackLoaded ? null : "Steam metadata refresh failed", payload.Error);
    }

    private sealed record CapturedEvent(string EventName, object? Payload);

    private sealed class CapturingNotificationService : ISignalRNotificationService
    {
        private readonly object _lock = new();
        private readonly List<CapturedEvent> _events = new();
        private readonly Dictionary<string, TaskCompletionSource<CapturedEvent>> _waiters = new();

        public IReadOnlyList<CapturedEvent> Events
        {
            get { lock (_lock) { return _events.ToList(); } }
        }

        public IReadOnlyList<TPayload> PayloadsFor<TPayload>(string eventName)
        {
            lock (_lock)
            {
                return _events
                    .Where(e => e.EventName == eventName && e.Payload is TPayload)
                    .Select(e => (TPayload)e.Payload!)
                    .ToList();
            }
        }

        public Task<CapturedEvent> WhenEventAsync(string eventName)
        {
            lock (_lock)
            {
                var existing = _events.FirstOrDefault(e => e.EventName == eventName);
                if (existing != null)
                {
                    return Task.FromResult(existing);
                }

                if (!_waiters.TryGetValue(eventName, out var tcs))
                {
                    tcs = new TaskCompletionSource<CapturedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[eventName] = tcs;
                }

                return tcs.Task;
            }
        }

        private void Capture(string eventName, object? payload)
        {
            var captured = new CapturedEvent(eventName, payload);
            lock (_lock)
            {
                _events.Add(captured);
                if (_waiters.Remove(eventName, out var tcs))
                {
                    tcs.TrySetResult(captured);
                }
            }
        }

        public Task NotifyAllAsync(string eventName, object? data = null)
        {
            Capture(eventName, data);
            return Task.CompletedTask;
        }

        public void NotifyAllFireAndForget(string eventName, object? data = null) => Capture(eventName, data);

        public Task NotifyOperationFailedAsync(string eventName, IOperationComplete failedEvent)
        {
            Capture(eventName, failedEvent);
            return Task.CompletedTask;
        }

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
        public Task NotifyAdminAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyGuestAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyGroupAsync(string groupName, string eventName, object? data = null) => Task.CompletedTask;
    }
}
