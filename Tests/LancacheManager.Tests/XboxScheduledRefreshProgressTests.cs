using System.Net;
using System.Reflection;
using System.Text;
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
/// operationId-scoped lifecycle events with non-decreasing percent, and finishes with exactly one
/// terminal event. It also proves the display-flag pattern:
/// a silent-mode run still emits every lifecycle event, only stamped <c>showNotification=false</c>
/// (never transport suppression), so the frontend gates the card rather than the backend dropping events.
/// The run executes with no authenticated session, no daemon, and an empty database, so it takes the
/// signed-out branch: started plus a terminal that names the skip rather than a completed refresh.
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

        var events = harness.Notifications.XboxLifecycleEvents();

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

        // Percent is non-decreasing across the emitted sequence.
        var percents = events.Select(e => e.PercentComplete).ToList();
        for (var i = 1; i < percents.Count; i++)
        {
            Assert.True(percents[i] >= percents[i - 1],
                $"percent regressed: {percents[i - 1]} -> {percents[i]}");
        }

        // The harness runs signed out, so this run had nothing to read and terminates as Skipped
        // with no work claimed. Success stays true because nothing went wrong.
        var terminal = terminals[0];
        Assert.True(terminal.Success);
        Assert.Equal(OperationStatus.Skipped, terminal.Status);
        Assert.Equal(0.0, terminal.PercentComplete);

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

        var events = harness.Notifications.XboxLifecycleEvents();

        Assert.True(events.Count >= 2, "lifecycle events must always emit, even when silent");
        Assert.Contains(events, e => e.IsTerminal);
        Assert.All(events, e => Assert.False(e.ShowNotification));

        // The terminal still reports success (display gating never changes the outcome).
        var terminal = events.Single(e => e.IsTerminal);
        Assert.True(terminal.Success);
    }

    [Fact]
    public async Task ScheduledRefresh_SignedOut_ReportsSkippedStageKeyAsync()
    {
        using var harness = new Harness();

        // No manager MSA session and no daemon: there is no catalog to read, so the run must say so
        // instead of walking the pipeline and finishing on the ordinary "completed" stage key.
        await harness.Service.RefreshNowAsync();
        await harness.Notifications.TerminalRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var events = harness.Notifications.XboxLifecycleEvents();
        var terminal = events.Single(e => e.IsTerminal);

        Assert.Equal("signalr.xboxMapping.skippedNotSignedIn", terminal.StageKey);
        Assert.NotEqual("signalr.xboxMapping.completed", terminal.StageKey);

        // The wire status has to say it too. The stage key alone still left status "completed" on
        // the frame, so a run that read nothing announced itself as a finished one.
        Assert.Equal(OperationStatus.Skipped, terminal.Status);

        // Nothing happened, so nothing is claimed: no fabricated 100%.
        Assert.Equal(0d, terminal.PercentComplete);

        // The collection stages never ran, so started is the only non-terminal event.
        Assert.Single(events, e => !e.IsTerminal);
    }

    [Fact]
    public void PostAuthenticationMapping_UsesTheSharedRefreshGate()
    {
        var source = ReadLoginSource();

        var reporterIndex = source.IndexOf(
            "var reporter = new MappingOperationReporter(",
            StringComparison.Ordinal);
        var pollMethodIndex = source.IndexOf(
            "private async Task RunLoginPollAsync",
            StringComparison.Ordinal);
        var waitIndex = source.IndexOf(
            "await _refreshGate.WaitAsync(reporter.Token)",
            StringComparison.Ordinal);
        var pollIndex = source.IndexOf(
            "await _authClient.PollForTokenAsync(deviceCode, reporter.Token)",
            StringComparison.Ordinal);
        var releaseIndex = source.IndexOf(
            "_refreshGate.Release();",
            StringComparison.Ordinal);
        var logoutIndex = source.IndexOf(
            "public async Task LogoutAsync()",
            StringComparison.Ordinal);

        Assert.True(reporterIndex >= 0, "the sign-in must own a mapping reporter");
        Assert.True(pollMethodIndex > reporterIndex,
            "the reporter must be built in StartLoginAsync, so one operation id covers the whole sign-in");
        Assert.True(waitIndex > pollMethodIndex, "the sign-in must acquire the shared refresh gate");
        Assert.True(pollIndex > waitIndex,
            "the wait for approval must happen under the shared refresh gate, or a scheduled tick registers a second XboxMapping operation beside the sign-in");
        Assert.True(releaseIndex > pollIndex && releaseIndex < logoutIndex,
            "the shared refresh gate must be acquired and released inside RunLoginPollAsync, or an early exit leaks it");
    }

    [Fact]
    public void SignInCardCarriesTheWaitingStageKey()
    {
        // The started event now fires before the approval wait, so the card it creates is what the
        // user reads while approving. It shows the started stage key, so that key has to be the
        // waiting one and not the generic starting one.
        var source = ReadLoginSource();

        Assert.Contains(
            "await reporter.StartAsync(CreateXboxMappingContext(), XboxAwaitingSignInStageKey);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "XboxAwaitingSignInStageKey = \"signalr.xbox.mapping.authenticating\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AwaitingSignInMatchesTheScheduleRegistryLookup()
    {
        // ServiceScheduleRegistry looks this member up with these exact flags and drops it when the
        // type is not an exact match. A field, a method or a bool? reads back as absent, and the row
        // then says nothing at all rather than failing loudly.
        var property = typeof(XboxCatalogMappingService).GetProperty(
            "AwaitingSignIn",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property!.PropertyType);

        using var harness = new Harness();
        Assert.False((bool)property.GetValue(harness.Service)!);

        // The half the reflection check above cannot see: it pins the property against this test's
        // own copy of the name, so editing the registry's literal instead would leave both green and
        // the row silent. Pin the registry's own call site too, so the pair has to move together.
        Assert.Contains(
            "GetPropertyValue(service.GetType(), service, \"AwaitingSignIn\", typeof(bool))",
            ReadScheduleRegistrySource(),
            StringComparison.Ordinal);
    }

    private static string ReadLoginSource() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "Api",
        "LancacheManager",
        "Services",
        "Xbox",
        "XboxCatalogMappingService.Authentication.cs"));

    private static string ReadScheduleRegistrySource() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "Api",
        "LancacheManager",
        "Core",
        "Services",
        "ServiceScheduleRegistry.cs"));

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

    // ---- harness (hand-rolled; no mocking framework, matching the suite idiom) ----

    [Fact]
    public async Task SignedOutRefreshThatStoresBannerArtReportsCompletedNotSkippedAsync()
    {
        // Banner backfill needs no Microsoft account - it reads the public DisplayCatalog - and it
        // WRITES to the database when it finds art. So a signed-out pass that stores a banner has done
        // real work, and calling it "skipped" would tell the user nothing happened when a row changed.
        // The skip test used to count only new patterns and re-tagged downloads, which is what let this
        // through.
        const string catalogJson =
            """
            {"Products":[{"ProductId":"9NBLGGH4R315","LocalizedProperties":[{"ProductTitle":"Test Title",
            "Images":[{"Uri":"//store-images.example/banner.jpg","ImagePurpose":"Poster"}]}]}]}
            """;

        using var harness = new Harness(new StubCatalogHandler(catalogJson));

        await using (var db = await harness.DbFactory.CreateDbContextAsync())
        {
            db.XboxGameMappings.Add(new XboxGameMapping
            {
                ProductId = "9NBLGGH4R315",
                Title = "Test Title",
                ImageUrl = null
            });
            await db.SaveChangesAsync();
        }

        // No manager MSA session and no daemon, so this run is signed out exactly like the skip test.
        await harness.Service.RefreshNowAsync();
        await harness.Notifications.TerminalRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var terminal = harness.Notifications.XboxLifecycleEvents().Single(e => e.IsTerminal);

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        Assert.NotEqual("signalr.xboxMapping.skippedNotSignedIn", terminal.StageKey);

        // And the art actually landed, so the run really did change something.
        await using (var db = await harness.DbFactory.CreateDbContextAsync())
        {
            var stored = Assert.Single(db.XboxGameMappings.ToList());
            Assert.False(string.IsNullOrEmpty(stored.ImageUrl));
        }
    }

    /// <summary>Answers every request with one canned DisplayCatalog body.</summary>
    private sealed class StubCatalogHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubCatalogHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly HttpClient _authHttp = new();
        private readonly HttpClient _apiHttp;

        public RecordingNotifications Notifications { get; }
        public UnifiedOperationTracker Tracker { get; }
        public XboxCatalogMappingService Service { get; }

        /// <summary>Exposed so a test can seed rows the run is expected to act on.</summary>
        public InMemoryDbContextFactory DbFactory { get; }

        /// <param name="catalogHandler">Stub responder for the public DisplayCatalog lookup that banner
        /// backfill makes. Left null the run reaches the real endpoint and stores nothing, which is what
        /// every pre-existing test here wants.</param>
        public Harness(HttpMessageHandler? catalogHandler = null)
        {
            _apiHttp = catalogHandler is null ? new HttpClient() : new HttpClient(catalogHandler);
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
            DbFactory = new InMemoryDbContextFactory(dbOptions);
            var mappingService = new XboxMappingService(
                DbFactory,
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
        Guid OperationId, double PercentComplete, bool IsTerminal, OperationStatus Status, bool Success, bool ShowNotification, string StageKey);

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
                terminal = eventName == SignalREvents.XboxMappingComplete;
            }

            if (terminal)
            {
                TerminalRecorded.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public List<ProgressSnapshot> XboxLifecycleEvents()
        {
            lock (_sync)
            {
                return _events
                    .Where(e => e.Data != null && e.EventName is
                        SignalREvents.XboxMappingStarted or
                        SignalREvents.XboxMappingProgress or
                        SignalREvents.XboxMappingComplete)
                    .Select(e => e.Data switch
                    {
                        ScheduledRunStartedEvent started => new ProgressSnapshot(
                            started.OperationId,
                            0,
                            false,
                            OperationStatus.Running,
                            false,
                            started.ShowNotification,
                            started.StageKey),
                        ScheduledRunProgressEvent progress => new ProgressSnapshot(
                            progress.OperationId,
                            progress.PercentComplete,
                            false,
                            OperationStatus.Running,
                            false,
                            progress.ShowNotification,
                            progress.StageKey),
                        ScheduledRunCompleteEvent complete => new ProgressSnapshot(
                            complete.OperationId,
                            complete.PercentComplete,
                            true,
                            complete.Status,
                            complete.Success,
                            complete.ShowNotification,
                            complete.StageKey),
                        _ => throw new InvalidOperationException("Unexpected Xbox lifecycle payload")
                    })
                    .ToList();
            }
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
