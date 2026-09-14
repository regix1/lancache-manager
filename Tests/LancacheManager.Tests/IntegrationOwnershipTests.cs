using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Controllers;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class IntegrationOwnershipTests
{
    [Theory]
    [InlineData("account-required", "errors.integration.accountRequired", true)]
    [InlineData("owned-by-another-account", "errors.integration.ownedByAnotherAccount", true)]
    [InlineData("login-in-progress", "errors.integration.loginInProgress", false)]
    [InlineData("reauthentication-required", "errors.integration.reauthenticationRequired", false)]
    [InlineData("release-in-progress", "errors.integration.releaseInProgress", false)]
    [InlineData("attempt-required", "errors.integration.attemptRequired", false)]
    [InlineData("attempt-expired", "errors.integration.attemptExpired", false)]
    [InlineData("main-owner-required", "errors.integration.mainOwnerRequired", true)]
    [InlineData("integration-sign-in-required", "errors.integration.signInRequired", false)]
    [InlineData("no-saved-login", "errors.integration.noSavedLogin", false)]
    [InlineData("not-supported", "errors.integration.notSupported", false)]
    public void RefusalPreservesTheReasonKeyAndStatus(string reason, string stageKey, bool forbidden)
    {
        if (forbidden)
        {
            var error = Assert.Throws<ForbiddenException>(() => IntegrationLease.Refuse(reason));
            Assert.Equal(stageKey, error.StageKey);
            Assert.Equal(reason, error.Message);
        }
        else
        {
            var error = Assert.Throws<ConflictException>(() => IntegrationLease.Refuse(reason));
            Assert.Equal(stageKey, error.StageKey);
            Assert.Equal(reason, error.Message);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    [InlineData("status-unavailable")]
    public void RefusalRejectsUnrecognizedReasons(string? reason)
    {
        Assert.Throws<InvalidOperationException>(() => IntegrationLease.Refuse(reason));
    }

    [Fact]
    public async Task ConfiguredSharedModeOverridesTheLegacyAuthenticationDefaultAndClearsActorIdentity()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        fixture.State.GetState().Access.Mode = AccountMode.Unauthenticated;
        fixture.State.GetState().Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        var configuration = new ConfigurationBuilder().Build();
        var access = new AccessService(fixture.State, configuration, null!);
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(access)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Items["Session"] = new UserSession
        { Id = fixture.Owner.SessionId!.Value, AccountId = fixture.Owner.AccountId };
        var caller = await IntegrationLease.ResolveCallerAsync(context);
        Assert.False(caller.AuthenticationEnabled);
        Assert.Null(caller.AccountId);
        Assert.Null(caller.SessionId);
        Assert.False(caller.OwnsInstallation);
        using var lease = await fixture.Storage.AcquireIntegrationLoginAsync(caller);
        Assert.Equal("token", fixture.Storage.GetIntegrationLogin(lease).RefreshToken);
        Assert.Equal(fixture.Owner.AccountId, fixture.Storage.GetAuthData().OwnerAccountId);
    }

    [Fact]
    public async Task CallerResolutionLetsTheMainAdministratorRecoverAnotherLogin()
    {
        using var fixture = new IntegrationFixture();
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContextFactory<AppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()))
            .BuildServiceProvider();
        await using (var accounts = await services
            .GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync())
        {
            accounts.UserAccounts.Add(new UserAccount
            {
                Id = fixture.Other.AccountId!.Value,
                Username = "primary",
                Role = SessionType.Admin,
                IsMainAdmin = true
            });
            await accounts.SaveChangesAsync();
        }

        var context = new DefaultHttpContext { RequestServices = services };
        context.Items["Session"] = new UserSession
        {
            Id = fixture.Other.SessionId!.Value,
            AccountId = fixture.Other.AccountId,
            SessionType = SessionType.Admin
        };
        var caller = await IntegrationLease.ResolveCallerAsync(context);
        Assert.True(caller.OwnsInstallation);
        fixture.Seed();
        var access = fixture.Storage.GetIntegrationAccess(caller);
        Assert.False(access.CanSignIn);
        Assert.True(access.CanRecover);
        Assert.False(access.CanLogout);
        Assert.Equal("reauthentication-required", access.OwnershipReason);
    }

    [Fact]
    public async Task WebApiKeyAuthorityUsesTheCurrentOwnerRowAndNeverThePicsOwnerOrRole()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContextFactory<AppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()))
            .BuildServiceProvider();
        await using (var accounts = await services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync())
        {
            accounts.UserAccounts.Add(new UserAccount { Id = fixture.Owner.AccountId!.Value, Username = "ordinary", Role = SessionType.Admin });
            accounts.UserAccounts.Add(new UserAccount { Id = fixture.Other.AccountId!.Value, Username = "owner", Role = SessionType.User, IsMainAdmin = true });
            await accounts.SaveChangesAsync();
        }
        using var clients = new ProbeClients();
        var webApi = new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, fixture.Storage);
        var controller = new SteamApiKeysController(webApi, NullLogger<SteamApiKeysController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } } };
        controller.HttpContext.Items["Session"] = new UserSession
        { Id = fixture.Owner.SessionId!.Value, AccountId = fixture.Owner.AccountId, SessionType = SessionType.Admin };
        var before = File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath());
        var saved = File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId.Value));
        await Assert.ThrowsAsync<ForbiddenException>(() => controller.SaveKeyAsync(new SaveApiKeyRequest { ApiKey = "changed" }));
        await Assert.ThrowsAsync<ForbiddenException>(() => controller.RemoveKey());
        Assert.Equal(0, clients.Calls);
        Assert.Equal(before, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));

        controller.HttpContext.Items["Session"] = new UserSession
        { Id = fixture.Other.SessionId!.Value, AccountId = fixture.Other.AccountId, SessionType = SessionType.User };
        Assert.IsType<CreatedResult>((await controller.SaveKeyAsync(new SaveApiKeyRequest { ApiKey = "changed" })).Result);
        Assert.Equal(1, clients.Calls);
        Assert.Equal("changed", fixture.Storage.GetAuthData().SteamApiKey);
        Assert.Equal(fixture.Owner.AccountId, fixture.Storage.GetAuthData().OwnerAccountId);
        Assert.Equal(saved, File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId.Value)));
        Assert.IsType<OkObjectResult>((await controller.RemoveKey()).Result);
        Assert.Null(fixture.Storage.GetAuthData().SteamApiKey);
        Assert.Equal("token", fixture.Storage.GetAuthData().RefreshToken);
        var writes = fixture.Storage.Writes;
        await controller.GetStatusAsync(forceRefresh: true);
        Assert.Equal(writes, fixture.Storage.Writes);
    }
    [Fact]
    public async Task SimultaneousStartsReserveExactlyOneCallerBeforeAnyCredentialWrite()
    {
        using var fixture = new IntegrationFixture();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Reserve(IntegrationCaller caller)
        {
            await start.Task;
            try { await fixture.Storage.BeginIntegrationLoginAsync(caller); return true; }
            catch (ConflictException) { return false; }
        }
        var first = Reserve(fixture.Owner);
        var second = Reserve(fixture.Other);
        start.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, result => result);
        Assert.Equal(0, fixture.Storage.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableOwnerBlocksAnotherAccountEvenWithoutAUsableToken(bool usable)
    {
        using var fixture = new IntegrationFixture();
        var caller = fixture.Other with { OwnsInstallation = false };
        var auth = fixture.Credentials();
        if (!usable) auth.RefreshToken = null;
        fixture.Storage.SaveAuthData(auth);
        var writes = fixture.Storage.Writes;
        var before = File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath());
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Storage.BeginIntegrationLoginAsync(caller, recover: true));
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Storage.BeginIntegrationReleaseAsync(caller));
        await Assert.ThrowsAsync<ForbiddenException>(() => fixture.Storage.AcquireIntegrationLoginAsync(caller));
        Assert.Equal(writes, fixture.Storage.Writes);
        Assert.Equal(before, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
        Assert.Null(fixture.Storage.GetIntegrationAccess(caller).AttemptId);
    }

    [Fact]
    public async Task PendingAttemptRejectsOtherAccountsAndOtherSessionsWithoutCallbacks()
    {
        using var fixture = new IntegrationFixture();
        var login = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        var callbacks = 0;
        foreach (var caller in new[] { fixture.Other, fixture.Owner with { SessionId = Guid.NewGuid() } })
        {
            Assert.Throws<ForbiddenException>(() => fixture.Storage.ContinueIntegrationLogin(caller, login.AttemptId));
            Assert.Throws<ForbiddenException>(() => fixture.Storage.CancelIntegrationLogin(caller, login.AttemptId, () => callbacks++));
            await Assert.ThrowsAsync<ConflictException>(() => fixture.Storage.BeginIntegrationLoginAsync(caller));
            await Assert.ThrowsAsync<ConflictException>(() => fixture.Storage.BeginIntegrationReleaseAsync(caller));
        }
        Assert.Equal(0, callbacks);
        Assert.Equal(0, fixture.Storage.Writes);
        Assert.Equal(login, fixture.Storage.ContinueIntegrationLogin(fixture.Owner, login.AttemptId));
        Assert.Throws<ConflictException>(() => fixture.Storage.CancelIntegrationLogin(fixture.Owner, null));
        Assert.True(fixture.Storage.IsIntegrationLoginCurrent(login));
    }

    [Fact]
    public async Task CancelledAttemptCannotCompleteOrCancelItsSuccessor()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var first = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        fixture.Storage.CancelIntegrationLogin(fixture.Owner, first.AttemptId);
        var second = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        var writes = fixture.Storage.Writes;
        Assert.False(fixture.Storage.CompleteIntegrationLogin(first, fixture.Credentials("old")));
        Assert.False(fixture.Storage.FinishIntegrationLogin(first));
        Assert.Throws<ConflictException>(() => fixture.Storage.CancelIntegrationLogin(fixture.Owner, first.AttemptId));
        Assert.Equal(writes, fixture.Storage.Writes);
        Assert.True(fixture.Storage.CompleteIntegrationLogin(second, fixture.Credentials("new")));
        Assert.Equal("new", fixture.Storage.GetAuthData().RefreshToken);
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner, first.AttemptId));
    }

    [Fact]
    public async Task ExplicitOwnerlessRecoveryPreservesCredentialsUntilCommit()
    {
        using var fixture = new IntegrationFixture();
        var shared = fixture.Credentials("legacy");
        shared.OwnerAccountId = null;
        fixture.Storage.SaveAuthData(shared);
        var before = File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath());
        Assert.Equal("reauthentication-required", fixture.Storage.GetIntegrationAccess(fixture.Owner).OwnershipReason);
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner, recover: true));
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Storage.BeginIntegrationLoginAsync(fixture.Other));
        var first = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Other, recover: true);
        fixture.Storage.CancelIntegrationLogin(fixture.Other, first.AttemptId);
        Assert.Equal(before, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
        var second = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Other, recover: true);
        var recovered = fixture.Credentials("recovered");
        recovered.OwnerAccountId = fixture.Other.AccountId;
        Assert.True(fixture.Storage.CompleteIntegrationLogin(second, recovered));
        Assert.Equal(fixture.Other.AccountId, fixture.Storage.GetAuthData().OwnerAccountId);
    }

    [Fact]
    public async Task MainAdministratorCanReplaceAnotherLogin()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var primary = fixture.Other;
        var recoverable = fixture.Storage.GetIntegrationAccess(primary);
        Assert.False(recoverable.CanSignIn);
        Assert.True(recoverable.CanRecover);
        Assert.False(recoverable.CanLogout);
        Assert.Equal("reauthentication-required", recoverable.OwnershipReason);

        var login = await fixture.Storage.BeginIntegrationLoginAsync(primary, recover: true);
        var replacement = fixture.Credentials("replacement");
        replacement.OwnerAccountId = primary.AccountId;
        Assert.True(fixture.Storage.CompleteIntegrationLogin(login, replacement));
        Assert.Equal(primary.AccountId, fixture.Storage.GetAuthData().OwnerAccountId);
    }

    [Fact]
    public async Task SharedModeUsesActiveCredentialsWithoutChangingTheStoredOwner()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var shared = new IntegrationCaller(null, null, false);
        var active = File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath());
        Assert.True(fixture.Storage.GetIntegrationAccess(shared).CanManage);
        await using (var dispatch = await fixture.Storage.AcquireIntegrationLoginAsync(shared))
            Assert.Equal("token", fixture.Storage.GetIntegrationLogin(dispatch).RefreshToken);
        Assert.Equal(active, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
        Assert.True(fixture.Storage.GetIntegrationAccess(fixture.Owner).CanManage);
        Assert.False(fixture.Storage.GetIntegrationAccess(fixture.Other).CanManage);
        var pending = await fixture.Storage.BeginIntegrationLoginAsync(shared);
        Assert.Equal(pending, fixture.Storage.ContinueIntegrationLogin(shared, pending.AttemptId));
        fixture.Storage.CancelIntegrationLogin(shared, pending.AttemptId);
        Assert.Equal(active, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
        await using (var release = await fixture.Storage.BeginIntegrationReleaseAsync(shared))
            fixture.Storage.CompleteIntegrationRelease(release);
        Assert.Equal("integration-sign-in-required", fixture.Storage.GetIntegrationLoginReason(shared));
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner.AccountId!.Value).RefreshToken);
        var login = await fixture.Storage.BeginIntegrationLoginAsync(shared);
        var auth = fixture.Credentials();
        auth.OwnerAccountId = null;
        Assert.True(fixture.Storage.CompleteIntegrationLogin(login, auth));
        Assert.True(fixture.Storage.GetIntegrationAccess(shared).CanLogout);
        Assert.Equal("reauthentication-required", fixture.Storage.GetIntegrationAccess(fixture.Owner).OwnershipReason);
    }

    [Fact]
    public async Task RestartPreservesDurableOwnershipButRejectsPendingIds()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var login = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        var restarted = fixture.FreshStorage();
        Assert.Equal(fixture.Owner.AccountId, restarted.GetAuthData().OwnerAccountId);
        Assert.Throws<ConflictException>(() => restarted.ContinueIntegrationLogin(fixture.Owner, login.AttemptId));
        Assert.False(restarted.GetIntegrationAccess(fixture.Other).CanSignIn);
        using var lease = await restarted.AcquireIntegrationLoginAsync(fixture.Owner);
        Assert.Equal("token", restarted.GetIntegrationLogin(lease).RefreshToken);
    }

    [Fact]
    public async Task ExpiryRejectsOldWorkWithoutClearingTheActiveLogin()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var login = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        login = fixture.Storage.SetIntegrationLoginExpiry(login, DateTime.UtcNow.AddSeconds(-1));
        Assert.Throws<ConflictException>(() => fixture.Storage.ContinueIntegrationLogin(fixture.Owner, login.AttemptId));
        Assert.False(fixture.Storage.CompleteIntegrationLogin(login, fixture.Credentials("expired")));
        Assert.Equal("token", fixture.Storage.GetAuthData().RefreshToken);
        Assert.True(fixture.Storage.GetIntegrationAccess(fixture.Owner).CanSignIn);
    }

    private sealed class ProbeClients : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"response\":{}}") });
        }
    }
}
