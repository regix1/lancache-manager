using System.Net;
using System.Reflection;
using System.Text;
using LancacheManager.Core.Services.Xbox;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public partial class XboxScheduledRefreshProgressTests
{
    public sealed class XboxIntegrationOwnershipTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task DurableOwnerBlocksOtherAccountsWithoutStartingADeviceRequest(bool usable)
        {
            using var auth = new StubDeviceCodeHandler();
            using var harness = new Harness(authHandler: auth);
            var owner = new IntegrationCaller(Guid.NewGuid(), Guid.NewGuid(), true);
            var other = new IntegrationCaller(Guid.NewGuid(), Guid.NewGuid(), true);
            harness.AuthStorage.SaveAuthData(new XboxAuthData { OwnerAccountId = owner.AccountId, RefreshToken = usable ? "original" : null });
            var path = Path.Combine(harness.AuthStorage.GetAuthDirectory(), "credentials.json");
            var before = File.ReadAllBytes(path);
            await Assert.ThrowsAsync<ForbiddenException>(() => harness.Service.StartLoginAsync(null, caller: other, recover: true));
            await Assert.ThrowsAsync<ForbiddenException>(() => harness.Service.LogoutAsync(other));
            Assert.Equal("owned-by-another-account", harness.Service.GetAuthStatus(other).OwnershipReason);
            Assert.False(harness.Service.GetAuthStatus().LoginInProgress);
            Assert.Equal(0, auth.DeviceRequests);
            Assert.Empty(harness.Tracker.GetActiveOperations(OperationType.XboxMapping));
            Assert.Equal(before, File.ReadAllBytes(path));
        }

        [Fact]
        public async Task PendingAttemptCannotBeCancelledByAnotherSessionOrWithoutItsId()
        {
            using var auth = new StubDeviceCodeHandler();
            using var harness = new Harness(authHandler: auth);
            var owner = new IntegrationCaller(Guid.NewGuid(), Guid.NewGuid(), true);
            var start = await harness.Service.StartLoginAsync(null, caller: owner);
            var other = owner with { SessionId = Guid.NewGuid() };
            Assert.Throws<ForbiddenException>(() => harness.Service.CancelLogin(other, start.AttemptId));
            Assert.Throws<ConflictException>(() => harness.Service.CancelLogin(owner, null));
            await Assert.ThrowsAsync<ConflictException>(() => harness.Service.StartLoginAsync(null, caller: other));
            Assert.Equal(start.AttemptId, harness.Service.GetAuthStatus(owner).AttemptId);
            Assert.Equal(1, auth.DeviceRequests);
            harness.Service.CancelLogin(owner, start.AttemptId);
            await WaitForAsync(() => !harness.Service.GetAuthStatus().LoginInProgress);
        }

        [Fact]
        public async Task CancelledHarvestCannotCommitOrRetireTheNextAttempt()
        {
            using var auth = new StubDeviceCodeHandler
            {
                TokenBody = """{"access_token":"access","refresh_token":"replacement"}""",
                CompleteHarvest = true,
                HoldFirstHarvest = true
            };
            using var harness = new Harness(authHandler: auth);
            var owner = new IntegrationCaller(Guid.NewGuid(), Guid.NewGuid(), true);
            using var signer = XblRequestSigner.CreateNew();
            harness.AuthStorage.SaveAuthData(new XboxAuthData
            {
                OwnerAccountId = owner.AccountId, RefreshToken = "original",
                DeviceKeyPkcs8 = signer.ExportPkcs8Base64()
            });
            var first = await harness.Service.StartLoginAsync(null, caller: owner);
            await auth.FirstHarvestReached.WaitAsync(TimeSpan.FromSeconds(10));
            harness.Service.CancelLogin(owner, first.AttemptId);
            var next = await harness.Service.StartLoginAsync(null, caller: owner);
            Assert.Throws<ConflictException>(() => harness.Service.CancelLogin(owner, first.AttemptId));
            Assert.Equal(next.AttemptId, harness.Service.GetAuthStatus(owner).AttemptId);
            Assert.Equal("original", harness.AuthStorage.GetAuthData().RefreshToken);
            auth.ReleaseFirstHarvest();
            await WaitForAsync(() => !harness.Service.GetAuthStatus().LoginInProgress);
            Assert.Equal("replacement", harness.AuthStorage.GetAuthData().RefreshToken);
            Assert.Equal(owner.AccountId, harness.AuthStorage.GetAuthData().OwnerAccountId);
            var terminals = harness.Notifications.XboxLifecycleEvents().Where(e => e.IsTerminal).ToList();
            Assert.Equal(2, terminals.Count);
            Assert.Single(terminals, e => e.Status == OperationStatus.Cancelled);
            Assert.Single(terminals, e => e.Status == OperationStatus.Completed);
        }

        [Fact]
        public async Task SharedModeCanUseAndReleaseTheActiveOwnerWithoutReadingSavedAccounts()
        {
            using var auth = new StubDeviceCodeHandler();
            using var harness = new Harness(authHandler: auth);
            var owner = new IntegrationCaller(Guid.NewGuid(), Guid.NewGuid(), true);
            var shared = new IntegrationCaller(null, null, false);
            harness.AuthStorage.SaveAuthData(new XboxAuthData { OwnerAccountId = owner.AccountId, RefreshToken = "active" });
            var path = Path.Combine(harness.AuthStorage.GetAuthDirectory(), "credentials.json");
            var before = File.ReadAllBytes(path);
            Assert.True(harness.Service.GetAuthStatus(shared).CanSignIn);
            await using (var lease = await harness.AuthStorage.AcquireIntegrationLoginAsync(shared))
                Assert.Equal("active", harness.AuthStorage.GetIntegrationLogin(lease).RefreshToken);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.True(harness.Service.GetAuthStatus(owner).CanManage);
            var start = await harness.Service.StartLoginAsync(null, caller: shared);
            harness.Service.CancelLogin(shared, start.AttemptId);
            await WaitForAsync(() => !harness.Service.GetAuthStatus().LoginInProgress);
            await harness.Service.LogoutAsync(shared);
            Assert.Null(harness.AuthStorage.GetAuthData().OwnerAccountId);
            Assert.Equal("active", harness.AuthStorage.GetSavedLogin(owner.AccountId!.Value).RefreshToken);
            Assert.Equal("integration-sign-in-required", harness.AuthStorage.GetIntegrationLoginReason(shared));
        }

        [Fact]
        public async Task SharedReplacementBecomesOwnerlessAndRequiresRecoveryWhenAuthenticationReturns()
        {
            using var auth = new StubDeviceCodeHandler
            {
                TokenBody = """{"access_token":"access","refresh_token":"shared"}""", CompleteHarvest = true
            };
            using var harness = new Harness(authHandler: auth);
            var owner = new IntegrationCaller(Guid.NewGuid(), Guid.NewGuid(), true);
            harness.AuthStorage.SaveAuthData(new XboxAuthData { OwnerAccountId = owner.AccountId, RefreshToken = "original" });
            await harness.Service.StartLoginAsync(null, caller: new(null, null, false));
            await WaitForAsync(() => !harness.Service.GetAuthStatus().LoginInProgress);
            Assert.Null(harness.AuthStorage.GetAuthData().OwnerAccountId);
            Assert.Equal("shared", harness.AuthStorage.GetAuthData().RefreshToken);
            Assert.Equal("original", harness.AuthStorage.GetSavedLogin(owner.AccountId!.Value).RefreshToken);
            Assert.Equal("reauthentication-required", harness.Service.GetAuthStatus(owner).OwnershipReason);
        }

        [Fact]
        public async Task OwnerlessRecoveryIsExplicitAndCancelledRecoveryPreservesCredentials()
        {
            using var auth = new StubDeviceCodeHandler();
            using var harness = new Harness(authHandler: auth);
            harness.AuthStorage.SaveAuthData(new XboxAuthData { RefreshToken = "legacy" });
            var main = new IntegrationCaller(Guid.NewGuid(), Guid.NewGuid(), true, true);
            await Assert.ThrowsAsync<ConflictException>(() => harness.Service.StartLoginAsync(null, caller: main));
            var start = await harness.Service.StartLoginAsync(null, caller: main, recover: true);
            harness.Service.CancelLogin(main, start.AttemptId);
            await WaitForAsync(() => !harness.Service.GetAuthStatus().LoginInProgress);
            Assert.Null(harness.AuthStorage.GetAuthData().OwnerAccountId);
            Assert.Equal("legacy", harness.AuthStorage.GetAuthData().RefreshToken);
        }

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable, "invalid_grant", false)]
        [InlineData(HttpStatusCode.BadRequest, "invalid_scope", false)]
        [InlineData(HttpStatusCode.BadRequest, "invalid_grant", true)]
        public async Task ReconnectPreservesOwnershipExceptForDefiniteRejection(HttpStatusCode status, string error, bool cleared)
        {
            using var auth = new TokenHandler(status, "{\"error\":\"" + error + "\"}");
            using var harness = new Harness(authHandler: auth);
            var account = Guid.NewGuid();
            harness.AuthStorage.SaveAuthData(new XboxAuthData { OwnerAccountId = account, RefreshToken = "original" });
            await Reconnect(harness.Service);
            Assert.Equal(cleared, harness.AuthStorage.GetAuthData().OwnerAccountId is null);
            Assert.Equal(cleared, harness.AuthStorage.GetSavedLogin(account).RefreshToken is null);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LateRefreshCannotRestoreReleasedCredentials(bool scheduled)
        {
            using var auth = new TokenHandler(HttpStatusCode.OK, """{"access_token":"access","refresh_token":"late"}""") { Hold = true };
            using var harness = new Harness(authHandler: auth);
            using var signer = XblRequestSigner.CreateNew();
            harness.AuthStorage.SaveAuthData(new XboxAuthData
            {
                OwnerAccountId = Guid.NewGuid(), RefreshToken = "original", DeviceKeyPkcs8 = signer.ExportPkcs8Base64()
            });
            typeof(XboxCatalogMappingService).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(harness.Service, true);
            var pending = scheduled ? harness.Service.RefreshNowAsync() : Reconnect(harness.Service);
            await auth.Reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await using (var release = await harness.AuthStorage.BeginIntegrationReleaseAsync())
                harness.AuthStorage.CompleteIntegrationRelease(release);
            auth.Release.TrySetResult();
            await pending;
            Assert.Null(harness.AuthStorage.GetAuthData().OwnerAccountId);
            Assert.Null(harness.AuthStorage.GetAuthData().RefreshToken);
            Assert.Equal(1, auth.Calls);
        }

        private static Task Reconnect(XboxCatalogMappingService service) => (Task)typeof(XboxCatalogMappingService)
            .GetMethod("TryAutoReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null)!;

        private sealed class TokenHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            public bool Hold { get; init; }
            public int Calls { get; private set; }
            public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal(XboxAuthConstants.TokenUrl, request.RequestUri!.ToString());
                Calls++;
                Reached.TrySetResult();
                if (Hold) await Release.Task;
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }
    }
}
