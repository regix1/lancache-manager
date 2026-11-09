using System.Net;
using System.Reflection;
using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The scheduled refresh's count write and the image refresh's token write change only their own fields of the
/// stored login, and neither re-creates a login that a logout cleared.
/// </summary>
public sealed class EpicTokenWriteTests
{
    private const string OneAsset =
        """[{"appName":"A","buildVersion":"1","catalogItemId":"ca","namespace":"na"}]""";

    private const string RotatedTokenForAccountA =
        """{"access_token":"new-access","refresh_token":"rotated","expires_at":"2099-01-01T00:00:00Z","expires_in":3600,"refresh_expires":3600,"account_id":"acct-A","displayName":"A"}""";

    [Fact]
    public async Task AScheduledRefreshStoresItsGamesCountAndKeepsATokenSavedDuringTheMappingAsync()
    {
        Rig? rig = null;
        rig = new Rig(OneAsset, tokenExpired: false, onRequest: request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/catalog/api/", StringComparison.Ordinal))
            {
                rig!.Storage.SaveAuthData(new EpicAuthData
                {
                    RefreshToken = "rotated-during-mapping",
                    DisplayName = "owner",
                    AccountId = "acct-A"
                }, saveSavedLogin: false);
            }
        });
        using var owned = rig;

        var run = await rig.RunScheduledRefreshAsync();

        Assert.Equal("completed", run.Status);
        var stored = rig.Storage.GetAuthData();
        Assert.Equal(1, stored.GamesDiscovered);
        Assert.Equal("rotated-during-mapping", stored.RefreshToken);
        Assert.Equal(1, rig.Service.GamesDiscovered);
        Assert.Equal(1, rig.State.LastCollectionWrites);
    }

    [Fact]
    public async Task AScheduledRefreshWhileAPrefillHoldsItsLeaseStillStoresItsCountAndEndsCompletedAsync()
    {
        using var rig = new Rig(OneAsset, tokenExpired: false);
        await using var lease = await rig.Storage.AcquireIntegrationLoginAsync(new IntegrationCaller(null, null, false));

        var run = await rig.RunScheduledRefreshAsync();

        Assert.Equal("completed", run.Status);
        var stored = rig.Storage.GetAuthData();
        Assert.Equal(1, stored.GamesDiscovered);
        Assert.NotNull(stored.LastAuthenticated);
    }

    [Fact]
    public async Task ALogoutDuringAScheduledRefreshLeavesNoCountNoCollectionTimeAndNoStoredLoginAsync()
    {
        Rig? rig = null;
        rig = new Rig(OneAsset, tokenExpired: false, onFirstDbContext: () =>
            Task.Run(() => rig!.Service.LogoutAsync()));
        using var owned = rig;

        var run = await rig.RunScheduledRefreshAsync();

        Assert.NotEqual("completed", run.Status);
        Assert.Null(rig.Service.LastCollectionUtc);
        Assert.Equal(0, rig.Service.GamesDiscovered);
        Assert.Equal(0, rig.State.LastCollectionWrites);
        Assert.True(string.IsNullOrEmpty(rig.Storage.GetAuthData().RefreshToken));
        Assert.Equal(0, rig.Storage.GetAuthData().GamesDiscovered);
    }

    [Fact]
    public async Task AnImageRefreshKeepsACountStoredDuringItsTokenCallAsync()
    {
        Rig? rig = null;
        rig = new Rig("[]", tokenExpired: true, onRequest: request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                rig!.Storage.UpdateAuthData(auth => auth.GamesDiscovered = 7, saveSavedLogin: false);
            }
        });
        using var owned = rig;

        var updated = await rig.Service.RefreshImagesAsync();

        Assert.Equal(0, updated);
        var stored = rig.Storage.GetAuthData();
        Assert.Equal(7, stored.GamesDiscovered);
        Assert.Equal("rotated", stored.RefreshToken);
    }

    [Fact]
    public async Task AnImageRefreshDoesNotOverwriteASignInAsAnotherAccountMadeDuringItsTokenCallAsync()
    {
        Rig? rig = null;
        rig = new Rig("[]", tokenExpired: true, onRequest: request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                rig!.Storage.SaveAuthData(new EpicAuthData
                {
                    RefreshToken = "b-refresh",
                    DisplayName = "B",
                    AccountId = "acct-B"
                });
                rig.SetCurrentTokens(new EpicOAuthTokens
                {
                    AccessToken = "b-access",
                    RefreshToken = "b-refresh",
                    DisplayName = "B",
                    AccountId = "acct-B",
                    ExpiresAt = DateTime.UtcNow.AddHours(1)
                });
            }
        });
        using var owned = rig;

        var updated = await rig.Service.RefreshImagesAsync();

        Assert.Equal(0, updated);
        var stored = rig.Storage.GetAuthData();
        Assert.Equal("b-refresh", stored.RefreshToken);
        Assert.Equal("acct-B", stored.AccountId);
        Assert.Equal("B", stored.DisplayName);
        var current = rig.GetCurrentTokens()!;
        Assert.Equal("b-refresh", current.RefreshToken);
        Assert.Equal("acct-B", current.AccountId);
        Assert.Equal("B", current.DisplayName);
    }

    [Fact]
    public async Task AnImageRefreshDuringALogoutLeavesNoStoredLoginAsync()
    {
        Rig? rig = null;
        rig = new Rig("[]", tokenExpired: true, onRequest: request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                Task.Run(() => rig!.Service.LogoutAsync()).GetAwaiter().GetResult();
            }
        });
        using var owned = rig;

        var updated = await rig.Service.RefreshImagesAsync();

        Assert.Equal(0, updated);
        Assert.True(string.IsNullOrEmpty(rig.Storage.GetAuthData().RefreshToken));
        Assert.False(rig.Service.IsAuthenticated);
    }

    [Fact]
    public async Task AnImageRefreshWhoseTokenRequestFailsThrowsSoTheImagePassCanWarnAsync()
    {
        using var rig = new Rig("[]", tokenExpired: true, onRequest: request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                throw new HttpRequestException("connection dropped");
            }
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Service.RefreshImagesAsync());
    }

    [Fact]
    public async Task AScheduledRefreshKeepsGoingWhenTheSameLoginRotatesItsTokenDuringTheMappingAsync()
    {
        Rig? rig = null;
        rig = new Rig(OneAsset, tokenExpired: false, onRequest: request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/catalog/api/", StringComparison.Ordinal))
            {
                rig!.Storage.UpdateAuthData(auth => auth.RefreshToken = "rotated-during-mapping");
            }
        });
        using var owned = rig;

        var run = await rig.RunScheduledRefreshAsync();

        Assert.Equal("completed", run.Status);
        Assert.Equal(1, rig.Storage.GetAuthData().GamesDiscovered);
        Assert.Equal("rotated-during-mapping", rig.Storage.GetAuthData().RefreshToken);
    }

    [Fact]
    public async Task AScheduledRunThatStartsSignedOutRenewsTheSavedLoginAndRefreshesTheCatalogAsync()
    {
        var libraryReads = 0;
        using var rig = new Rig(OneAsset, tokenExpired: false, onRequest: request =>
        {
            if (request.RequestUri!.ToString().Contains("/assets/Windows?label=Live", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref libraryReads);
            }
        });

        var run = await rig.RunSignedOutScheduleAsync();

        Assert.Equal("completed", run.Status);
        Assert.True(rig.Service.IsAuthenticated);
        Assert.True(libraryReads > 0, "the catalog refresh never read the library");
        Assert.Equal("rotated", rig.Storage.GetAuthData().RefreshToken);
    }

    [Fact]
    public async Task AScheduledRunWhoseLoginRenewalTimesOutEndsWithTheSignedOutResultAsync()
    {
        var libraryReads = 0;
        using var rig = new Rig(OneAsset, tokenExpired: false, onRequest: request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                    new TimeoutException());
            }
            if (request.RequestUri.ToString().Contains("/assets/Windows?label=Live", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref libraryReads);
            }
        });

        // A throw out of the scheduled work is what the schedule records as a failed run with a retry delay.
        var run = await rig.RunSignedOutScheduleAsync();

        Assert.NotEqual("failed", run.Status);
        Assert.False(rig.Service.IsAuthenticated);
        Assert.Equal(0, libraryReads);
    }

    [Fact]
    public async Task APrefillTokenRequestRefusedBecauseASignInStartedWhileItWaitedReleasesTheSessionLockAsync()
    {
        using var rig = new Rig(OneAsset, tokenExpired: false);
        await using var lease = await rig.Storage.AcquireIntegrationLoginAsync(new IntegrationCaller(null, null, false));
        var sessionLock = (SemaphoreSlim)typeof(EpicMappingService)
            .GetField("_sessionLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(rig.Service)!;
        var processingLogin = typeof(EpicMappingService)
            .GetField("_processingLogin", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await sessionLock.WaitAsync();
        var request = rig.Service.CreatePrefillRefreshTokenAsync(null, lease: lease);
        Assert.False(request.IsCompleted);
        processingLogin.SetValue(rig.Service, new IntegrationLogin(
            Guid.NewGuid(), 1, null, null, DateTime.UtcNow.AddMinutes(15), true, false));
        sessionLock.Release();

        var refused = await Assert.ThrowsAsync<ConflictException>(() => request);
        Assert.Equal("errors.integration.loginInProgress", refused.StageKey);
        Assert.True(await sessionLock.WaitAsync(TimeSpan.FromSeconds(5)), "the refused request kept the session lock");
        sessionLock.Release();
    }

    private sealed class Rig : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Action<HttpRequestMessage>? _onRequest;
        private readonly Func<Task>? _onFirstDbContext;
        private int _dbContextCalls;

        public Rig(string assetsJson, bool tokenExpired, Action<HttpRequestMessage>? onRequest = null, Func<Task>? onFirstDbContext = null)
        {
            _onRequest = onRequest;
            _onFirstDbContext = onFirstDbContext;
            Root = Path.Combine(Path.GetTempPath(), "epic-token-write", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var paths = new AuthCredentialFormatTests.TempDirPathResolver(Root);
            var keys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths);
            var encryption = new SecureStateEncryptionService(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root, "keys"))), keys,
                NullLogger<SecureStateEncryptionService>.Instance);
            Storage = new EpicAuthStorageService(NullLogger<EpicAuthStorageService>.Instance, paths, encryption);
            Storage.SaveAuthData(new EpicAuthData { RefreshToken = "refresh", DisplayName = "owner", AccountId = "acct-A" });

            _http = new HttpClient(new RouteHandler(assetsJson, request => _onRequest?.Invoke(request)));
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"epic_token_write_{Guid.NewGuid():N}")
                .Options;
            Tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            var state = DispatchProxy.Create<IStateService, RecordingState>();
            State = (RecordingState)(object)state;
            Services = new ServiceCollection().BuildServiceProvider();
            Service = new EpicMappingService(
                NullLogger<EpicMappingService>.Instance,
                new EpicApiDirectClient(_http, NullLogger<EpicApiDirectClient>.Instance),
                Storage,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                new CallbackDbContexts(options, () =>
                {
                    if (Interlocked.Increment(ref _dbContextCalls) == 1 && _onFirstDbContext is not null)
                    {
                        _onFirstDbContext().GetAwaiter().GetResult();
                    }
                }),
                Tracker,
                Services.GetRequiredService<IServiceScopeFactory>(),
                state);
            typeof(EpicMappingService)
                .GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, true);
            SetCurrentTokens(new EpicOAuthTokens
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                AccountId = "acct-A",
                DisplayName = "owner",
                ExpiresAt = tokenExpired ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddHours(1)
            });
        }

        public string Root { get; }
        public EpicAuthStorageService Storage { get; }
        public EpicMappingService Service { get; }
        public UnifiedOperationTracker Tracker { get; }
        public RecordingState State { get; }
        private ServiceProvider Services { get; }

        public void SetCurrentTokens(EpicOAuthTokens tokens) =>
            typeof(EpicMappingService)
                .GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, tokens);

        public EpicOAuthTokens? GetCurrentTokens() =>
            (EpicOAuthTokens?)typeof(EpicMappingService)
                .GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Service);

        public async Task<OperationRun> RunScheduledRefreshAsync()
        {
            Assert.True(Service.TryStartRefresh());
            await (Task)typeof(EpicMappingService)
                .GetField("_currentRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Service)!;
            return Assert.Single(Tracker.GetRuns().Runs);
        }

        // A scheduled run that starts while the service holds the saved login but is signed out, the state a failed
        // renewal at startup leaves.
        public async Task<OperationRun> RunSignedOutScheduleAsync()
        {
            typeof(EpicMappingService)
                .GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, false);
            typeof(EpicMappingService)
                .GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, null);
            typeof(EpicMappingService)
                .GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, 1);
            ((TaskCompletionSource<bool>)typeof(EpicMappingService)
                .GetField("_startupAutoReconnectCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Service)!).TrySetResult(true);
            await (Task)typeof(EpicMappingService)
                .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Service, [CancellationToken.None])!;
            return Assert.Single(Tracker.GetRuns().Runs);
        }

        public void Dispose()
        {
            Service.Dispose();
            _http.Dispose();
            Services.Dispose();
            Directory.Delete(Root, true);
        }
    }

    // Answers the library request with the given assets and the token request with a rotated token for account A;
    // every other request is a 404, which the mapping treats as "no data".
    private sealed class RouteHandler(string assetsJson, Action<HttpRequestMessage> onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest(request);
            var url = request.RequestUri!.ToString();
            if (url.Contains("/assets/Windows?label=Live", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(assetsJson));
            }
            if (url.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(RotatedTokenForAccountA));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class CallbackDbContexts(DbContextOptions<AppDbContext> options, Action onCreate)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            onCreate();
            return new AppDbContext(options);
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private class RecordingState : NullReturningProxy
    {
        public int LastCollectionWrites { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IStateService.SetEpicMappingLastCollection) && args![0] is not null)
            {
                LastCollectionWrites++;
            }
            return base.Invoke(targetMethod, args);
        }
    }
}
