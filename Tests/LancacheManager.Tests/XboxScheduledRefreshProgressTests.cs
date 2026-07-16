using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Services.Xbox;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the scheduled Xbox catalog refresh owns a tracked operation and drives the universal bar:
/// <see cref="XboxCatalogMappingService.RefreshNowAsync"/> registers exactly one tracker op, emits
/// operationId-scoped <see cref="SignalREvents.XboxMappingProgress"/> ticks with non-decreasing
/// percent, and finishes with exactly one terminal event. It also proves the display-flag pattern:
/// a silent-mode run still emits every lifecycle event, only stamped <c>showNotification=false</c>
/// (never transport suppression), so the frontend gates the card rather than the backend dropping events.
/// The run executes with no authenticated session, no daemon, and an empty database, so only the
/// instrumentation is exercised.
/// </summary>
public class XboxScheduledRefreshProgressTests
{
    [Fact]
    public async Task ScheduledRefresh_RegistersSingleTrackerOp_EmitsMonotonicProgress_AndOneTerminalAsync()
    {
        using var harness = new Harness();

        var terminalCount = 0;
        harness.Tracker.OperationTerminal += info =>
        {
            if (info.Type == OperationType.XboxMapping)
            {
                Interlocked.Increment(ref terminalCount);
            }
        };

        await harness.Service.RefreshNowAsync();
        await harness.Notifications.TerminalRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var events = harness.Notifications.XboxProgressEvents();

        // Exactly one tracker op: every event carries the same non-empty operationId, and the tracker
        // fired its terminal exactly once for this type.
        var operationIds = events.Select(e => e.OperationId).Distinct().ToList();
        Assert.Single(operationIds);
        Assert.NotEqual(Guid.Empty, operationIds[0]);
        Assert.Equal(1, Volatile.Read(ref terminalCount));

        // At least one running progress tick and a single terminal.
        Assert.Contains(events, e => !e.IsTerminal);
        var terminals = events.Where(e => e.IsTerminal).ToList();
        Assert.Single(terminals);

        // Percent is non-decreasing across the emitted sequence and ends at 100 on success.
        var percents = events.Select(e => e.PercentComplete).ToList();
        for (var i = 1; i < percents.Count; i++)
        {
            Assert.True(percents[i] >= percents[i - 1],
                $"percent regressed: {percents[i - 1]} -> {percents[i]}");
        }

        var terminal = terminals[0];
        Assert.True(terminal.Success);
        Assert.Equal(OperationStatus.Completed, terminal.Status);
        Assert.Equal(100.0, terminal.PercentComplete);

        // Default mode is All, so the run is visible.
        Assert.All(events, e => Assert.True(e.ShowNotification));

        // The op reached a terminal state (nothing left active for this type).
        Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.XboxMapping));
    }

    [Fact]
    public async Task ScheduledRefresh_SilentMode_StillEmitsLifecycle_WithShowNotificationFalseAsync()
    {
        using var harness = new Harness();

        // Silent + a scheduled (non-manual) trigger => the run must not surface, but events still emit.
        harness.Service.SetNotificationMode(NotificationMode.Silent);

        await harness.Service.RefreshNowAsync();
        await harness.Notifications.TerminalRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var events = harness.Notifications.XboxProgressEvents();

        Assert.True(events.Count >= 2, "lifecycle events must always emit, even when silent");
        Assert.Contains(events, e => e.IsTerminal);
        Assert.All(events, e => Assert.False(e.ShowNotification));

        // The terminal still reports success (display gating never changes the outcome).
        var terminal = events.Single(e => e.IsTerminal);
        Assert.True(terminal.Success);
    }

    // ---- harness (hand-rolled; no mocking framework, matching the suite idiom) ----

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly HttpClient _authHttp = new();
        private readonly HttpClient _apiHttp = new();

        public RecordingNotifications Notifications { get; }
        public UnifiedOperationTracker Tracker { get; }
        public XboxCatalogMappingService Service { get; }

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), $"xbox_sched_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            Notifications = new RecordingNotifications();
            Tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);

            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = _root;

            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
            var apiKeyService = new ApiKeyService(
                NullLogger<ApiKeyService>.Instance,
                new ConfigurationBuilder().Build(),
                pathResolver);
            var encryption = new SecureStateEncryptionService(
                dataProtection,
                apiKeyService,
                NullLogger<SecureStateEncryptionService>.Instance);

            var authStorage = new XboxAuthStorageService(
                NullLogger<XboxAuthStorageService>.Instance,
                pathResolver,
                encryption);
            var authClient = new XboxAuthClient(_authHttp, NullLogger<XboxAuthClient>.Instance);
            var apiClient = new XboxApiDirectClient(_apiHttp, NullLogger<XboxApiDirectClient>.Instance);

            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"xbox_sched_{Guid.NewGuid():N}")
                .Options;
            var mappingService = new XboxMappingService(
                new InMemoryDbContextFactory(dbOptions),
                Notifications,
                apiClient,
                NullLogger<XboxMappingService>.Instance);

            // Empty provider: the daemon service is not registered, so ResolveDaemonService returns null.
            var scopeFactory = new ServiceCollection().BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();

            // Null-returning state proxy: no saved interval / run-on-startup / notification-mode overrides.
            var stateService = DispatchProxy.Create<IStateService, NullStateServiceProxy>();

            Service = new XboxCatalogMappingService(
                NullLogger<XboxCatalogMappingService>.Instance,
                scopeFactory,
                mappingService,
                authClient,
                authStorage,
                Notifications,
                Tracker,
                stateService);
        }

        public void Dispose()
        {
            Service.Dispose();
            _authHttp.Dispose();
            _apiHttp.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private sealed record ProgressSnapshot(
        Guid OperationId, double PercentComplete, bool IsTerminal, OperationStatus Status, bool Success, bool ShowNotification);

    private sealed class RecordingNotifications : ISignalRNotificationService
    {
        private readonly object _sync = new();
        private readonly List<(string EventName, object? Data)> _events = new();

        public TaskCompletionSource TerminalRecorded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NotifyAllAsync(string eventName, object? data = null)
        {
            bool terminal;
            lock (_sync)
            {
                _events.Add((eventName, data));
                terminal = eventName == SignalREvents.XboxMappingProgress && GetBool(data, "isTerminal");
            }

            if (terminal)
            {
                TerminalRecorded.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public List<ProgressSnapshot> XboxProgressEvents()
        {
            lock (_sync)
            {
                return _events
                    .Where(e => e.EventName == SignalREvents.XboxMappingProgress && e.Data != null)
                    .Select(e => new ProgressSnapshot(
                        GetValue<Guid>(e.Data!, "operationId"),
                        GetValue<double>(e.Data!, "percentComplete"),
                        GetBool(e.Data!, "isTerminal"),
                        GetValue<OperationStatus>(e.Data!, "status"),
                        GetBool(e.Data!, "success"),
                        GetBool(e.Data!, "showNotification")))
                    .ToList();
            }
        }

        private static T GetValue<T>(object data, string prop)
            => (T)data.GetType().GetProperty(prop)!.GetValue(data)!;

        private static bool GetBool(object? data, string prop)
        {
            var value = data?.GetType().GetProperty(prop)?.GetValue(data);
            return value is bool b && b;
        }

        // Unused transport surface for this test - every path is a no-op.
        public void NotifyAllFireAndForget(string eventName, object? data = null) { }
        public Task NotifyOperationFailedAsync(string eventName, IOperationComplete failedEvent) => NotifyAllAsync(eventName, failedEvent);
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

    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;

        public AppDbContext CreateDbContext() => new(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private class PathResolverProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IPathResolver.ResolvePath))
            {
                var path = (string)args![0]!;
                return Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            }

            if (targetMethod.Name == nameof(IPathResolver.NormalizePath))
            {
                return (string)args![0]!;
            }

            if (targetMethod.ReturnType == typeof(string))
            {
                return Path.Combine(Root, targetMethod.Name);
            }

            if (targetMethod.ReturnType == typeof(bool))
            {
                return true;
            }

            if (targetMethod.ReturnType == typeof(int))
            {
                return 0;
            }

            return null;
        }
    }

    // Returns null/default for every IStateService member; the constructor only reads the three
    // per-service override getters, which must be absent (null) so no saved override is applied.
    private class NullStateServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
