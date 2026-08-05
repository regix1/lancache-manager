using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class MappingOperationReporterTests
{
    public static TheoryData<MappingOperationDefinition> Definitions =>
        new()
        {
            MappingOperations.Steam,
            MappingOperations.Epic,
            MappingOperations.Xbox,
            MappingOperations.BattleNet,
            MappingOperations.Riot,
        };

    [Fact]
    public void Definitions_ExposeTheFiveCanonicalContracts()
    {
        Assert.Collection(
            MappingOperations.All,
            definition => AssertDefinition(
                definition,
                "depotMapping",
                OperationType.DepotMapping,
                SignalREvents.DepotMappingStarted,
                SignalREvents.DepotMappingProgress,
                SignalREvents.DepotMappingComplete),
            definition => AssertDefinition(
                definition,
                "epicMapping",
                OperationType.EpicMapping,
                SignalREvents.EpicMappingStarted,
                SignalREvents.EpicMappingProgress,
                SignalREvents.EpicMappingComplete),
            definition => AssertDefinition(
                definition,
                "xboxMapping",
                OperationType.XboxMapping,
                SignalREvents.XboxMappingStarted,
                SignalREvents.XboxMappingProgress,
                SignalREvents.XboxMappingComplete),
            definition => AssertDefinition(
                definition,
                "battleNetMapping",
                OperationType.BattleNetMapping,
                SignalREvents.BattleNetMappingStarted,
                SignalREvents.BattleNetMappingProgress,
                SignalREvents.BattleNetMappingComplete),
            definition => AssertDefinition(
                definition,
                "riotMapping",
                OperationType.RiotMapping,
                SignalREvents.RiotMappingStarted,
                SignalREvents.RiotMappingProgress,
                SignalREvents.RiotMappingComplete));
    }

    [Fact]
    public void EpicAuthentication_RecomputesManualNotificationVisibilityBeforeReporterCreation()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Api",
            "LancacheManager",
            "Core",
            "Services",
            "EpicMapping",
            "EpicMappingService.Authentication.cs"));

        var visibilityIndex = source.IndexOf(
            "_showNotification = EffectiveNotificationMode.AllowsTrigger(RunTrigger.Manual);",
            StringComparison.Ordinal);
        var reporterIndex = source.IndexOf(
            "reporter = CreateEpicMappingReporter(",
            StringComparison.Ordinal);

        Assert.True(visibilityIndex >= 0);
        Assert.True(reporterIndex > visibilityIndex);
    }

    /// <summary>
    /// Both branches of the depot scheduling dispatch must hand the run's real trigger to the work
    /// they start. A literal trigger here told the notification gate a Run Now was automatic, so a
    /// service left on the Manual mode dropped every event of an import the user asked for.
    /// </summary>
    [Fact]
    public void DepotScheduling_PassesTheRunTriggerToBothDispatchBranches()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Api",
            "LancacheManager",
            "Core",
            "Services",
            "SteamKit2",
            "SteamKit2Service.Scheduling.cs"));

        Assert.Contains(
            "ImportFromGitHubAsync(stoppingToken, CurrentRunTrigger)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("trigger: CurrentRunTrigger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RunTrigger.Scheduled)", source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public async Task Reporter_EmitsOneStartedMonotonicProgressAndOneComplete_WithOneTrackedIdAsync(
        MappingOperationDefinition definition)
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker, definition);

        await reporter.StartAsync(new Dictionary<string, object?> { ["processed"] = 0, ["total"] = 8 });
        await reporter.ReportAsync(
            70,
            $"{definition.StageKeyPrefix}.resolving",
            new Dictionary<string, object?> { ["processed"] = 6, ["total"] = 8 });
        await reporter.ReportAsync(
            20,
            $"{definition.StageKeyPrefix}.resolving",
            new Dictionary<string, object?> { ["processed"] = 7, ["total"] = 8 });

        var active = tracker.GetActiveOperations(definition.OperationType).Single();
        Assert.Equal(reporter.OperationId, active.Id);
        Assert.Equal(70, active.PercentComplete);
        Assert.Equal($"{definition.StageKeyPrefix}.resolving", active.Message);

        var metadata = Assert.IsType<Dictionary<string, object?>>(active.Metadata);
        var context = Assert.IsType<Dictionary<string, object?>>(metadata["context"]);
        Assert.Equal(7, context["processed"]);
        Assert.Equal(8, context["total"]);

        await reporter.CompleteAsync(
            success: true,
            stageKey: $"{definition.StageKeyPrefix}.completed",
            context: new Dictionary<string, object?> { ["processed"] = 8, ["total"] = 8 });

        var terminal = await notifications.WhenEventAsync(definition.Events.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var started = Assert.Single(notifications.PayloadsFor<ScheduledRunStartedEvent>(definition.Events.Started));
        var progress = notifications.PayloadsFor<ScheduledRunProgressEvent>(definition.Events.Progress);
        var complete = Assert.IsType<ScheduledRunCompleteEvent>(terminal.Payload);

        Assert.Equal(2, progress.Count);
        Assert.Equal(new[] { 70d, 70d }, progress.Select(item => item.PercentComplete));
        Assert.Equal(started.OperationId, reporter.OperationId);
        Assert.All(progress, item => Assert.Equal(reporter.OperationId, item.OperationId));
        Assert.Equal(reporter.OperationId, complete.OperationId);
        Assert.True(complete.Success);
        Assert.False(complete.Cancelled);
        Assert.Equal(OperationStatus.Completed, complete.Status);
        Assert.Equal(100, complete.PercentComplete);
        Assert.Empty(tracker.GetActiveOperations(definition.OperationType));
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public async Task Reporter_FailureEmitsOneTerminalAtHighestProgressAsync(
        MappingOperationDefinition definition)
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker, definition);

        await reporter.StartAsync();
        await reporter.ReportAsync(42, $"{definition.StageKeyPrefix}.resolving");
        await reporter.CompleteAsync(
            success: false,
            error: "mapping failed",
            stageKey: $"{definition.StageKeyPrefix}.failed",
            context: new Dictionary<string, object?> { ["errorDetail"] = "mapping failed" });
        await reporter.CompleteAsync(success: true);

        var terminal = await notifications.WhenEventAsync(definition.Events.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var complete = Assert.IsType<ScheduledRunCompleteEvent>(terminal.Payload);

        Assert.Single(notifications.PayloadsFor<ScheduledRunCompleteEvent>(definition.Events.Complete));
        Assert.Equal(reporter.OperationId, complete.OperationId);
        Assert.False(complete.Success);
        Assert.False(complete.Cancelled);
        Assert.Equal(OperationStatus.Failed, complete.Status);
        Assert.Equal(42, complete.PercentComplete);
        Assert.Equal("mapping failed", complete.Error);
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public async Task Reporter_CancellationEmitsOneCancelledTerminalAtHighestProgressAsync(
        MappingOperationDefinition definition)
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker, definition);

        await reporter.StartAsync();
        await reporter.ReportAsync(55, $"{definition.StageKeyPrefix}.resolving");
        await reporter.CompleteAsync(
            success: false,
            error: "Cancelled by user",
            cancelled: true,
            stageKey: $"{definition.StageKeyPrefix}.cancelled");

        var terminal = await notifications.WhenEventAsync(definition.Events.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var complete = Assert.IsType<ScheduledRunCompleteEvent>(terminal.Payload);

        Assert.Single(notifications.PayloadsFor<ScheduledRunCompleteEvent>(definition.Events.Complete));
        Assert.Equal(reporter.OperationId, complete.OperationId);
        Assert.False(complete.Success);
        Assert.True(complete.Cancelled);
        Assert.Equal(OperationStatus.Cancelled, complete.Status);
        Assert.Equal(55, complete.PercentComplete);
    }

    [Theory]
    [MemberData(nameof(Definitions))]
    public async Task ExternalCancellation_UsesCancelledStageAndRejectsPostTerminalProgressAsync(
        MappingOperationDefinition definition)
    {
        var notifications = new CapturingNotificationService();
        var tracker = CreateTracker();
        await using var reporter = CreateReporter(notifications, tracker, definition);

        await reporter.StartAsync();
        await reporter.ReportAsync(35, $"{definition.StageKeyPrefix}.resolving");

        tracker.CompleteOperation(
            reporter.OperationId,
            success: false,
            error: "Force killed by user",
            cancelled: true);
        var terminal = await notifications.WhenEventAsync(definition.Events.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await reporter.ReportAsync(80, $"{definition.StageKeyPrefix}.late");

        var complete = Assert.IsType<ScheduledRunCompleteEvent>(terminal.Payload);
        Assert.True(complete.Cancelled);
        Assert.Equal($"{definition.StageKeyPrefix}.cancelled", complete.StageKey);
        Assert.Single(notifications.PayloadsFor<ScheduledRunProgressEvent>(definition.Events.Progress));
    }

    private static MappingOperationReporter CreateReporter(
        CapturingNotificationService notifications,
        UnifiedOperationTracker tracker,
        MappingOperationDefinition definition)
        => new(
            notifications,
            tracker,
            definition,
            showNotification: true,
            CancellationToken.None,
            NullLogger.Instance);

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private static void AssertDefinition(
        MappingOperationDefinition definition,
        string serviceKey,
        OperationType operationType,
        string started,
        string progress,
        string complete)
    {
        Assert.Equal(serviceKey, definition.ServiceKey);
        Assert.Equal(operationType, definition.OperationType);
        Assert.Equal(new ScheduledRunEventNames(started, progress, complete), definition.Events);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Api")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Web")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed record CapturedEvent(string EventName, object? Payload);

    private sealed class CapturingNotificationService : ISignalRNotificationService
    {
        private readonly object _sync = new();
        private readonly List<CapturedEvent> _events = new();
        private readonly Dictionary<string, TaskCompletionSource<CapturedEvent>> _waiters = new();

        public IReadOnlyList<TPayload> PayloadsFor<TPayload>(string eventName)
        {
            lock (_sync)
            {
                return _events
                    .Where(item => item.EventName == eventName && item.Payload is TPayload)
                    .Select(item => (TPayload)item.Payload!)
                    .ToList();
            }
        }

        public Task<CapturedEvent> WhenEventAsync(string eventName)
        {
            lock (_sync)
            {
                var existing = _events.FirstOrDefault(item => item.EventName == eventName);
                if (existing is not null)
                {
                    return Task.FromResult(existing);
                }

                if (!_waiters.TryGetValue(eventName, out var waiter))
                {
                    waiter = new TaskCompletionSource<CapturedEvent>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[eventName] = waiter;
                }

                return waiter.Task;
            }
        }

        private void Capture(string eventName, object? payload)
        {
            var captured = new CapturedEvent(eventName, payload);
            lock (_sync)
            {
                _events.Add(captured);
                if (_waiters.Remove(eventName, out var waiter))
                {
                    waiter.TrySetResult(captured);
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
