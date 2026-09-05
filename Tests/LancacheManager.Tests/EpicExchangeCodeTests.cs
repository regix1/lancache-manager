using System.Net;
using System.Reflection;
using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class EpicExchangeCodeTests
{
    [Fact]
    public async Task ExchangeCodeFlow_UsesBearerMintThenExchangeGrant()
    {
        var requests = new List<CapturedRequest>();
        using var httpClient = new HttpClient(new RecordingHandler(requests));
        var client = new EpicApiDirectClient(httpClient, NullLogger<EpicApiDirectClient>.Instance);

        var code = await client.GetExchangeCodeAsync("manager-access");
        var tokens = await client.ExchangeCodeAsync(code);

        Assert.Equal("one-use-code", code);
        Assert.Equal("daemon-refresh", tokens.RefreshToken);
        Assert.Collection(
            requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.EndsWith("/account/api/oauth/exchange", request.Uri, StringComparison.Ordinal);
                Assert.Equal("Bearer manager-access", request.Authorization);
                Assert.Empty(request.Body);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/account/api/oauth/token", request.Uri, StringComparison.Ordinal);
                Assert.StartsWith("Basic ", request.Authorization, StringComparison.Ordinal);
                Assert.Contains("grant_type=exchange_code", request.Body, StringComparison.Ordinal);
                Assert.Contains("exchange_code=one-use-code", request.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("manager-access", request.Body, StringComparison.Ordinal);
            });
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task RefreshToken_RejectionIsDistinguishedFromTransientFailure(
        HttpStatusCode status,
        bool rejected)
    {
        using var httpClient = new HttpClient(new FixedResponseHandler(status));
        var client = new EpicApiDirectClient(httpClient, NullLogger<EpicApiDirectClient>.Instance);

        var exception = await Record.ExceptionAsync(() => client.RefreshTokenAsync("saved-refresh"));

        Assert.NotNull(exception);
        Assert.Equal(rejected, exception is ValidationException);
    }

    [Fact]
    public async Task InactiveSavedAccountRefresh_DoesNotReplaceTheSharedActiveAccountAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lcm-epic-reuse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new TempDirPathResolver(root);
            var apiKeyService = new ApiKeyService(
                NullLogger<ApiKeyService>.Instance,
                new ConfigurationBuilder().Build(),
                paths);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys")));
            var encryption = new SecureStateEncryptionService(
                protection,
                apiKeyService,
                NullLogger<SecureStateEncryptionService>.Instance);
            var storage = new EpicAuthStorageService(
                NullLogger<EpicAuthStorageService>.Instance,
                paths,
                encryption);
            var activeAccount = Guid.NewGuid();
            var inactiveAccount = Guid.NewGuid();
            storage.SaveAuthData(new EpicAuthData
            {
                OwnerAccountId = activeAccount,
                RefreshToken = "active-refresh",
                DisplayName = "Active Account"
            });
            storage.SaveSavedLogin(inactiveAccount, new EpicAuthData
            {
                RefreshToken = "inactive-refresh",
                DisplayName = "Inactive Account"
            });

            using var httpClient = new HttpClient(new InactiveOwnerHandler());
            var api = new EpicApiDirectClient(httpClient, NullLogger<EpicApiDirectClient>.Instance);
            var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
            var tracker = (IUnifiedOperationTracker)DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>();
            var scopeFactory = (IServiceScopeFactory)DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
            var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
            var service = new EpicMappingService(
                NullLogger<EpicMappingService>.Instance,
                api,
                storage,
                notifications,
                null!,
                tracker,
                scopeFactory,
                stateService);

            var daemonRefresh = await service.CreatePrefillRefreshTokenAsync(inactiveAccount);

            var fresh = new EpicAuthStorageService(
                NullLogger<EpicAuthStorageService>.Instance,
                paths,
                encryption);
            Assert.Equal("daemon-refresh", daemonRefresh);
            Assert.Equal(activeAccount, fresh.GetAuthData().OwnerAccountId);
            Assert.Equal("active-refresh", fresh.GetAuthData().RefreshToken);
            Assert.Equal(inactiveAccount, fresh.GetSavedLogin(inactiveAccount).OwnerAccountId);
            Assert.Equal("rotated-inactive-refresh", fresh.GetSavedLogin(inactiveAccount).RefreshToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string Authorization,
        string Body);

    private sealed class RecordingHandler(List<CapturedRequest> requests) : HttpMessageHandler
    {
        private int _call;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body));

            var json = _call++ == 0
                ? "{\"code\":\"one-use-code\"}"
                : "{\"access_token\":\"daemon-access\",\"refresh_token\":\"daemon-refresh\",\"displayName\":\"Epic User\",\"account_id\":\"epic-id\",\"expires_in\":3600,\"refresh_expires\":28800}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class InactiveOwnerHandler : HttpMessageHandler
    {
        private int _call;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = _call++ switch
            {
                0 => "{\"access_token\":\"rotated-access\",\"refresh_token\":\"rotated-inactive-refresh\",\"displayName\":\"Inactive Account\",\"account_id\":\"inactive-id\",\"expires_in\":3600,\"refresh_expires\":28800}",
                1 => "{\"code\":\"one-use-code\"}",
                _ => "{\"access_token\":\"daemon-access\",\"refresh_token\":\"daemon-refresh\",\"displayName\":\"Inactive Account\",\"account_id\":\"inactive-id\",\"expires_in\":3600,\"refresh_expires\":28800}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TempDirPathResolver(string root) : PathResolverBase(NullLogger.Instance)
    {
        protected override string BasePath => root;
        protected override string RustExecutableExtension => string.Empty;

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }
}
