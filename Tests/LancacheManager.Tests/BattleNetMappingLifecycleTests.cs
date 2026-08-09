using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.BattleNet;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class BattleNetMappingLifecycleTests
{
    [Fact]
    public async Task ResolveDownloads_EmitsCanonicalLifecycleOnlyWhenCandidatesExistAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"battle-net-lifecycle-{Guid.NewGuid():N}")
            .Options;
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(new Download
            {
                Service = "blizzard",
                ClientIp = "127.0.0.1",
                LastUrl = "/tpr/wow/data/aa/bb/hash",
            });
            await seed.SaveChangesAsync();
        }

        var notifications = new RecordingNotifications();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var service = new BattleNetMappingService(
            new TestDbContextFactory(options),
            notifications,
            tracker,
            NullLogger<BattleNetMappingService>.Instance);

        var resolved = await service.ResolveDownloadsAsync();
        await notifications.Terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, resolved);
        var started = Assert.Single(notifications.Payloads<ScheduledRunStartedEvent>(
            SignalREvents.BattleNetMappingStarted));
        var progress = notifications.Payloads<ScheduledRunProgressEvent>(
            SignalREvents.BattleNetMappingProgress);
        var complete = Assert.Single(notifications.Payloads<ScheduledRunCompleteEvent>(
            SignalREvents.BattleNetMappingComplete));

        Assert.NotEmpty(progress);
        Assert.All(progress, item => Assert.Equal(started.OperationId, item.OperationId));
        Assert.Equal(started.OperationId, complete.OperationId);
        Assert.True(complete.Success);
        Assert.Equal(OperationStatus.Completed, complete.Status);
        Assert.Contains(
            notifications.Events,
            item => item.EventName == SignalREvents.DownloadsRefresh);
        Assert.Empty(tracker.GetActiveOperations(OperationType.BattleNetMapping));

        var lifecycleCount = notifications.Events.Count(item =>
            item.EventName is SignalREvents.BattleNetMappingStarted
                or SignalREvents.BattleNetMappingProgress
                or SignalREvents.BattleNetMappingComplete);
        Assert.Equal(0, await service.ResolveDownloadsAsync());
        Assert.Equal(
            lifecycleCount,
            notifications.Events.Count(item =>
                item.EventName is SignalREvents.BattleNetMappingStarted
                    or SignalREvents.BattleNetMappingProgress
                    or SignalREvents.BattleNetMappingComplete));
    }

    private sealed record CapturedEvent(string EventName, object? Payload);

    private sealed class RecordingNotifications : ISignalRNotificationService
    {
        private readonly object _sync = new();
        private readonly List<CapturedEvent> _events = [];

        public TaskCompletionSource Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CapturedEvent> Events
        {
            get
            {
                lock (_sync)
                {
                    return _events.ToList();
                }
            }
        }

        public List<T> Payloads<T>(string eventName)
        {
            lock (_sync)
            {
                return _events
                    .Where(item => item.EventName == eventName && item.Payload is T)
                    .Select(item => (T)item.Payload!)
                    .ToList();
            }
        }

        public Task NotifyAllAsync(string eventName, object? data = null)
        {
            lock (_sync)
            {
                _events.Add(new CapturedEvent(eventName, data));
            }

            if (eventName == SignalREvents.BattleNetMappingComplete)
            {
                Terminal.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public void NotifyAllFireAndForget(string eventName, object? data = null) =>
            _ = NotifyAllAsync(eventName, data);

        public Task NotifyOperationFailedAsync(string eventName, IOperationComplete failedEvent) =>
            NotifyAllAsync(eventName, failedEvent);

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
