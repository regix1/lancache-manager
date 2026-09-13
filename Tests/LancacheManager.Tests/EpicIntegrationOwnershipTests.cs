using System.Net;
using System.Reflection;
using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class EpicIntegrationOwnershipTests
{
    [Fact]
    public async Task SharedModeUsesTheActiveCredentialAndCanReleaseItsRetainedOwner()
    {
        using var fixture = new Fixture();
        fixture.Seed();
        var shared = new IntegrationCaller(null, null, false);
        var before = File.ReadAllBytes(fixture.ActivePath);
        Assert.True(fixture.Service.GetAuthStatus(shared).CanManage);
        Assert.True(fixture.Service.TryGetSavedLoginAccount(null, out _, shared));
        Assert.Equal(before, File.ReadAllBytes(fixture.ActivePath));
        Assert.True(fixture.Service.GetAuthStatus(fixture.Owner).CanManage);
        await using (var lease = await fixture.Service.AcquireIntegrationLoginAsync(shared))
            Assert.Equal("daemon-refresh", await fixture.Service.CreatePrefillRefreshTokenAsync(null, lease: lease));
        Assert.Equal(fixture.Owner.AccountId, fixture.Storage.GetAuthData().OwnerAccountId);
        var start = await fixture.Service.GetAuthorizationUrl(shared);
        Assert.Equal(start.AttemptId, fixture.Storage.ContinueIntegrationLogin(shared, start.AttemptId).AttemptId);
        await fixture.Service.CancelRefreshAsync(shared, start.AttemptId);
        await fixture.Service.LogoutAsync(shared);
        Assert.Null(fixture.Storage.GetAuthData().OwnerAccountId);
        Assert.False(fixture.Service.TryGetSavedLoginAccount(null, out _, shared));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableOwnerRefusesAnotherAccountBeforeAnyProviderRequest(bool usable)
    {
        using var fixture = new Fixture();
        fixture.Storage.SaveAuthData(new EpicAuthData
        {
            OwnerAccountId = fixture.Owner.AccountId,
            RefreshToken = usable ? "original" : null
        });
        var before = File.ReadAllBytes(fixture.ActivePath);
        Assert.Equal("owned-by-another-account", fixture.Service.GetAuthStatus(fixture.Other).OwnershipReason);
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Service.GetAuthorizationUrl(fixture.Other, recover: true));
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Service.LogoutAsync(fixture.Other));
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Service.CreatePrefillRefreshTokenAsync(fixture.Other.AccountId!.Value));
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(before, File.ReadAllBytes(fixture.ActivePath));
    }

    [Fact]
    public async Task PendingAttemptRequiresTheInitiatingSessionAndExactId()
    {
        using var fixture = new Fixture();
        var start = await fixture.Service.GetAuthorizationUrl(fixture.Owner);
        var session = fixture.Owner with { SessionId = Guid.NewGuid() };
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.GetAuthorizationUrl(fixture.Other));
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Service.OnAuthCodeReceivedAsync("code", caller: session, attemptId: start.AttemptId));
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner));
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Service.CancelRefreshAsync(fixture.Other, start.AttemptId));
        Assert.Equal(start.AttemptId, fixture.Service.GetAuthStatus(fixture.Owner).AttemptId);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.True(await fixture.Service.CancelRefreshAsync(fixture.Owner, start.AttemptId));
    }

    [Fact]
    public async Task LateCodeExchangeCannotCommitOrClearTheNextAttempt()
    {
        using var fixture = new Fixture();
        fixture.Seed();
        fixture.Handler.Hold = true;
        var first = await fixture.Service.GetAuthorizationUrl(fixture.Owner);
        var completing = fixture.Service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: first.AttemptId);
        await fixture.Handler.Reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Service.CancelRefreshAsync(fixture.Owner, first.AttemptId);
        var next = await fixture.Service.GetAuthorizationUrl(fixture.Owner);
        fixture.Handler.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completing);
        Assert.Equal(next.AttemptId, fixture.Service.GetAuthStatus(fixture.Owner).AttemptId);
        Assert.Equal("original", fixture.Storage.GetAuthData().RefreshToken);
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.CancelRefreshAsync(fixture.Owner, first.AttemptId));
        await fixture.Service.CancelRefreshAsync(fixture.Owner, next.AttemptId);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FailedReplacementPreservesThePreviousCredentials(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        fixture.Seed();
        var before = File.ReadAllBytes(fixture.ActivePath);
        var saved = File.ReadAllBytes(fixture.SavedPath);
        fixture.Handler.Status = status;
        var start = await fixture.Service.GetAuthorizationUrl(fixture.Owner);
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.Service.OnAuthCodeReceivedAsync(
            "rejected", caller: fixture.Owner, attemptId: start.AttemptId)));
        Assert.Equal(before, File.ReadAllBytes(fixture.ActivePath));
        Assert.Equal(saved, File.ReadAllBytes(fixture.SavedPath));
        Assert.True(fixture.Service.GetAuthStatus(fixture.Owner).CanSignIn);
    }

    [Fact]
    public async Task RecoveryMustBeExplicitAndFailureDoesNotClaimLegacyCredentials()
    {
        using var fixture = new Fixture();
        fixture.Storage.SaveAuthData(new EpicAuthData { RefreshToken = "legacy" });
        var before = File.ReadAllBytes(fixture.ActivePath);
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.GetAuthorizationUrl(fixture.Other));
        var start = await fixture.Service.GetAuthorizationUrl(fixture.Other, recover: true);
        await fixture.Service.CancelRefreshAsync(fixture.Other, start.AttemptId);
        Assert.Equal(before, File.ReadAllBytes(fixture.ActivePath));
        Assert.True(fixture.Service.GetAuthStatus(fixture.Other).CanRecover);
    }

    [Fact]
    public async Task RestartUsesActiveOwnerAndKeepsTheDaemonTokenDistinct()
    {
        using var fixture = new Fixture();
        fixture.Seed();
        var restarted = fixture.CreateService(fixture.FreshStorage());
        var token = await restarted.CreatePrefillRefreshTokenAsync(fixture.Owner.AccountId!.Value);
        var fresh = fixture.FreshStorage();
        Assert.Equal("daemon-refresh", token);
        Assert.Equal("rotated-refresh", fresh.GetAuthData().RefreshToken);
        Assert.Equal("rotated-refresh", fresh.GetSavedLogin(fixture.Owner.AccountId.Value).RefreshToken);
        Assert.Equal(fixture.Owner.AccountId, fresh.GetAuthData().OwnerAccountId);
        restarted.Dispose();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    public async Task ReconnectOnlyClearsADefinitelyRejectedCurrentCredential(HttpStatusCode status, bool cleared)
    {
        using var fixture = new Fixture();
        fixture.Seed();
        fixture.Handler.Status = status;
        await fixture.Reconnect();
        Assert.Equal(cleared, fixture.Storage.GetAuthData().OwnerAccountId is null);
        Assert.Equal(cleared, fixture.Storage.GetAuthData().RefreshToken is null);
    }

    [Fact]
    public async Task LateReconnectCannotRestoreAReleasedCredential()
    {
        using var fixture = new Fixture();
        fixture.Seed();
        fixture.Handler.Hold = true;
        var reconnect = fixture.Reconnect();
        await fixture.Handler.Reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await using (var release = await fixture.Storage.BeginIntegrationReleaseAsync())
            fixture.Storage.CompleteIntegrationRelease(release);
        fixture.Handler.Release.TrySetResult();
        await reconnect;
        Assert.Null(fixture.Storage.GetAuthData().OwnerAccountId);
        Assert.Null(fixture.Storage.GetAuthData().RefreshToken);
        Assert.False(fixture.Service.IsAuthenticated);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "epic-integration-tests", Guid.NewGuid().ToString("N"));
        private readonly AuthCredentialFormatTests.TempDirPathResolver _paths;
        private readonly SecureStateEncryptionService _encryption;
        private readonly HttpClient _http;
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        public IntegrationCaller Owner { get; } = new(Guid.NewGuid(), Guid.NewGuid(), true);
        public IntegrationCaller Other { get; } = new(Guid.NewGuid(), Guid.NewGuid(), true, true);
        public Handler Handler { get; } = new();
        public EpicAuthStorageService Storage { get; }
        public EpicMappingService Service { get; }
        public string ActivePath => Path.Combine(Storage.GetAuthDirectory(), "credentials.json");
        public string SavedPath => Path.Combine(Storage.GetAuthDirectory(), "saved", $"{Owner.AccountId:N}.json");

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _paths = new AuthCredentialFormatTests.TempDirPathResolver(_root);
            var keys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), _paths);
            _encryption = new SecureStateEncryptionService(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))), keys,
                NullLogger<SecureStateEncryptionService>.Instance);
            Storage = FreshStorage();
            _http = new HttpClient(Handler);
            Service = CreateService(Storage);
        }

        public EpicAuthStorageService FreshStorage() => new(NullLogger<EpicAuthStorageService>.Instance, _paths, _encryption);
        public EpicMappingService CreateService(EpicAuthStorageService storage) => new(
            NullLogger<EpicMappingService>.Instance, new EpicApiDirectClient(_http, NullLogger<EpicApiDirectClient>.Instance),
            storage, DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(), null!,
            DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>(),
            _services.GetRequiredService<IServiceScopeFactory>(), DispatchProxy.Create<IStateService, NullReturningProxy>());
        public void Seed() => Storage.SaveAuthData(new EpicAuthData
        {
            OwnerAccountId = Owner.AccountId, RefreshToken = "original", DisplayName = "owner"
        });
        public Task Reconnect() => (Task)typeof(EpicMappingService)
            .GetMethod("TryAutoReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Service, null)!;
        public void Dispose()
        {
            Handler.Release.TrySetResult();
            Service.Dispose();
            _http.Dispose();
            _services.Dispose();
            Directory.Delete(_root, true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Hold { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Hold)
            {
                Reached.TrySetResult();
                await Release.Task;
            }
            var json = request.Method == HttpMethod.Get
                ? """{"code":"minted"}"""
                : body.Contains("grant_type=exchange_code", StringComparison.Ordinal)
                    ? """{"access_token":"daemon-access","refresh_token":"daemon-refresh","expires_in":3600,"displayName":"owner","account_id":"epic"}"""
                    : """{"access_token":"access","refresh_token":"rotated-refresh","expires_in":3600,"displayName":"owner","account_id":"epic"}""";
            return new HttpResponseMessage(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
