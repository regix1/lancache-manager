using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LancacheManager.Tests.DaemonTestMethods;

namespace LancacheManager.Tests;

public sealed class XboxSavedLoginTests
{
    [Fact]
    public async Task CapturedNameSurvivesCredentialReplacementAndPersistsCurrentSession()
    {
        using var harness = await Harness.CreateAsync("CapturedPlayer");
        harness.Client.HoldDispatch = true;
        var login = harness.StartAsync();
        await harness.Client.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = harness.Storage.RunIntegrationActionAsync(
            harness.Owner,
            () => harness.Storage.UpdateAuthData(auth => auth.DisplayName = "ReplacementPlayer"));
        await Task.Yield();
        Assert.False(replacement.IsCompleted);

        harness.Client.ReleaseDispatch.TrySetResult();
        await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Client.EmitStatusAsync(new DaemonStatus { Status = "logged-in" });
        await login.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("CapturedPlayer", harness.Session.Username);
        Assert.Equal("CapturedPlayer", harness.Session.AccountUsername);
        var persisted = await harness.SessionService.GetSessionAsync(harness.Session.Id);
        Assert.NotNull(persisted);
        Assert.Equal("CapturedPlayer", persisted!.AccountUsername);
        Assert.Contains(harness.Notifications.Invocations, invocation =>
            invocation.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
            && invocation.Args.Length > 0
            && (invocation.Args[0] as string) == SignalREvents.XboxDaemonSessionUpdated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DaemonIdentityTakesPrecedenceOverCapturedSavedName(bool accountField)
    {
        using var harness = await Harness.CreateAsync("SavedPlayer");
        harness.Client.CompletedStatus = new DaemonStatus
        {
            Status = "logged-in",
            AccountDisplayName = accountField ? "DaemonPlayer" : null,
            DisplayName = accountField ? null : "DaemonPlayer"
        };

        await harness.StartAsync();

        Assert.Equal("DaemonPlayer", harness.Session.AccountUsername);
        var persisted = await harness.SessionService.GetSessionAsync(harness.Session.Id);
        Assert.Equal("DaemonPlayer", persisted!.AccountUsername);
    }

    [Fact]
    public async Task RejectedSavedLoginDoesNotApplyCapturedName()
    {
        using var harness = await Harness.CreateAsync("SavedPlayer");
        harness.Client.AcceptLogin = false;

        await harness.StartAsync();

        await harness.AssertNameMissingAsync();
    }

    [Fact]
    public async Task DisconnectedSavedLoginDoesNotApplyCapturedName()
    {
        using var harness = await Harness.CreateAsync("SavedPlayer");
        harness.Client.CompleteImmediately = false;
        var login = harness.StartAsync();
        await harness.Client.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Client.EmitDisconnectedAsync();
        await login.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.AssertNameMissingAsync();
    }

    [Fact]
    public async Task CallerCancellationDoesNotApplyCapturedName()
    {
        using var harness = await Harness.CreateAsync("SavedPlayer");
        harness.Client.CompleteImmediately = false;
        using var cancellation = new CancellationTokenSource();
        var login = harness.StartAsync(cancellationToken: cancellation.Token);
        await harness.Client.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);

        await harness.AssertNameMissingAsync();
    }

    [Fact]
    public async Task PreRevokedLoginIdDoesNotDispatchOrApplyCapturedName()
    {
        using var harness = await Harness.CreateAsync("SavedPlayer");
        var loginId = Guid.NewGuid();
        Assert.False(await harness.Daemon.CancelLoginAsync(harness.Session.Id, loginId: loginId));

        await Assert.ThrowsAsync<ConflictException>(() => harness.StartAsync(loginId));

        Assert.False(harness.Client.DispatchEntered.Task.IsCompleted);
        await harness.AssertNameMissingAsync();
    }

    [Fact]
    public async Task SessionClosedAfterDispatchDoesNotApplyCapturedName()
    {
        using var harness = await Harness.CreateAsync("SavedPlayer");
        harness.Client.CompleteImmediately = false;
        var login = harness.StartAsync();
        await harness.Client.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Session.AdmissionClosed = true;

        await harness.Client.EmitStatusAsync(new DaemonStatus { Status = "logged-in" });
        await Assert.ThrowsAsync<ConflictException>(() => login);

        await harness.AssertNameMissingAsync();
    }

    [Fact]
    public async Task AlreadyLoggedInConflictDoesNotInferSavedCredentialName()
    {
        using var harness = await Harness.CreateAsync("SavedPlayer");
        harness.Client.LiveStatus = new DaemonStatus { Status = "logged-in" };

        var failure = await Assert.ThrowsAsync<ConflictException>(() => harness.StartAsync());

        Assert.Equal("errors.prefill.logoutBeforeSavedLogin", failure.StageKey);
        Assert.Null(harness.Session.AccountUsername);
        var persisted = await harness.SessionService.GetSessionAsync(harness.Session.Id);
        Assert.Null(persisted!.AccountUsername);
    }

    private sealed class Harness : IDisposable
    {
        private readonly IntegrationFixture _files;

        private Harness(
            IntegrationFixture files,
            IntegrationCaller owner,
            XboxAuthStorageService storage,
            TestXboxDaemon daemon,
            DaemonSession session,
            PrefillSessionService sessionService,
            SavedLoginClient client,
            RecordingNotificationProxy notifications)
        {
            _files = files;
            Owner = owner;
            Storage = storage;
            Daemon = daemon;
            Session = session;
            SessionService = sessionService;
            Client = client;
            Notifications = notifications;
        }

        public IntegrationCaller Owner { get; }
        public XboxAuthStorageService Storage { get; }
        public TestXboxDaemon Daemon { get; }
        public DaemonSession Session { get; }
        public PrefillSessionService SessionService { get; }
        public SavedLoginClient Client { get; }
        public RecordingNotificationProxy Notifications { get; }

        public static async Task<Harness> CreateAsync(string savedName)
        {
            var files = new IntegrationFixture();
            var owner = files.Owner;
            files.Xbox.SaveAuthData(new XboxAuthData
            {
                OwnerAccountId = owner.AccountId,
                RefreshToken = "saved-refresh",
                DisplayName = savedName
            });

            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"xbox_saved_login_{Guid.NewGuid():N}")
                .Options;
            var database = new TestDbContextFactory(dbOptions);
            var sessionService = new PrefillSessionService(
                database,
                NullLogger<PrefillSessionService>.Instance);
            var cacheService = new PrefillCacheService(
                database,
                NullLogger<PrefillCacheService>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
            var notificationRecorder = (RecordingNotificationProxy)(object)notifications;
            var configuration = new ConfigurationBuilder().Build();
            var pathResolver = DispatchProxy.Create<IPathResolver, NullReturningProxy>();
            var stateService = DispatchProxy.Create<IStateService, NullReturningProxy>();
            var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());
            var daemon = new TestXboxDaemon(
                notifications,
                configuration,
                pathResolver,
                stateService,
                sessionService,
                cacheService,
                networkOptions,
                files.Xbox);

            var clientInterface = DispatchProxy.Create<IDaemonClient, SavedLoginClient>();
            var client = (SavedLoginClient)(object)clientInterface;
            var session = new DaemonSession
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                UserId = ScheduledPrefillConstants.DeriveSystemUserId(),
                Status = DaemonSessionStatus.Active,
                AuthState = DaemonAuthState.NotAuthenticated,
                IsPersistent = true,
                Platform = "Xbox",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                Client = clientInterface
            };
            daemon.InjectSession(session);
            await sessionService.CreateSessionAsync(
                session.Id,
                session.UserId,
                $"container-{session.Id}",
                $"xbox-{session.Id}",
                session.ExpiresAt,
                "Xbox");

            return new Harness(
                files,
                owner,
                files.Xbox,
                daemon,
                session,
                sessionService,
                client,
                notificationRecorder);
        }

        public async Task StartAsync(
            Guid? loginId = null,
            CancellationToken cancellationToken = default)
        {
            using var lease = await Storage.AcquireIntegrationLoginAsync(Owner, cancellationToken);
            await Daemon.ReuseIntegrationLoginForEditAsync(
                Session.Id,
                Owner.AccountId,
                () => { },
                Owner.SessionId!.Value,
                cancellationToken,
                lease,
                loginId);
        }

        public async Task AssertNameMissingAsync()
        {
            Assert.Null(Session.Username);
            Assert.Null(Session.AccountUsername);
            var persisted = await SessionService.GetSessionAsync(Session.Id);
            Assert.NotNull(persisted);
            Assert.Null(persisted!.AccountUsername);
        }

        public void Dispose() => _files.Dispose();
    }

    private sealed class TestXboxDaemon : XboxPrefillDaemonService
    {
        public TestXboxDaemon(
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions,
            XboxAuthStorageService authStorage)
            : base(
                NullLogger<XboxPrefillDaemonService>.Instance,
                notifications,
                configuration,
                pathResolver,
                stateService,
                sessionService,
                cacheService,
                null!,
                networkOptions,
                new TestLancacheServerLocator(),
                new UnavailableContainerGatewayFactory(),
                authStorage: authStorage)
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;
    }

    private class SavedLoginClient : DispatchProxy
    {
        private Func<DaemonStatus, Task>? _statusUpdate;
        private Func<Task>? _disconnected;

        public bool AcceptLogin { get; set; } = true;
        public bool HoldDispatch { get; set; }
        public bool CompleteImmediately { get; set; } = true;
        public DaemonStatus LiveStatus { get; set; } = new() { Status = "awaiting-login" };
        public DaemonStatus CompletedStatus { get; set; } = new() { Status = "logged-in" };
        public TaskCompletionSource DispatchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task EmitStatusAsync(DaemonStatus status)
        {
            LiveStatus = status;
            if (_statusUpdate is not null)
            {
                await _statusUpdate(status);
            }
        }

        public Task EmitDisconnectedAsync() => _disconnected?.Invoke() ?? Task.CompletedTask;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "add_OnStatusUpdate":
                    _statusUpdate += (Func<DaemonStatus, Task>)args![0]!;
                    return null;
                case "remove_OnStatusUpdate":
                    _statusUpdate -= (Func<DaemonStatus, Task>)args![0]!;
                    return null;
                case "add_OnDisconnected":
                    _disconnected += (Func<Task>)args![0]!;
                    return null;
                case "remove_OnDisconnected":
                    _disconnected -= (Func<Task>)args![0]!;
                    return null;
                case nameof(IDaemonClient.GetStatusAsync):
                    return Task.FromResult<DaemonStatus?>(LiveStatus);
                case nameof(IDaemonClient.CancelLoginWithOutcomeAsync):
                    return Task.FromResult(true);
                case nameof(IDaemonClient.ProvideXboxAutoLoginWithDispatchAsync):
                    return ProvideAsync((Action)args![2]!, (CancellationToken)args[3]!);
                case nameof(IDaemonClient.CancelLoginAsync):
                    return Task.CompletedTask;
                case nameof(IDaemonClient.ClearPendingChallenges):
                case nameof(IDisposable.Dispose):
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected daemon call: {targetMethod?.Name}");
            }
        }

        private async Task<bool> ProvideAsync(Action onCommandDispatched, CancellationToken cancellationToken)
        {
            if (!AcceptLogin)
            {
                return false;
            }

            onCommandDispatched();
            DispatchEntered.TrySetResult();
            if (HoldDispatch)
            {
                await ReleaseDispatch.Task.WaitAsync(cancellationToken);
            }
            if (CompleteImmediately)
            {
                LiveStatus = CompletedStatus;
            }
            return true;
        }
    }
}
