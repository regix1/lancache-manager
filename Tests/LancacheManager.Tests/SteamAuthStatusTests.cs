using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

public sealed class SteamAuthStatusTests
{
    [Theory]
    [InlineData(null, "refresh-token")]
    [InlineData("", "refresh-token")]
    [InlineData("   ", "refresh-token")]
    [InlineData("steam-user", null)]
    [InlineData("steam-user", "")]
    [InlineData("steam-user", "   ")]
    public void GetStatus_IncompleteAuthenticatedCredentialsBecomeAnonymous(string? username, string? refreshToken)
    {
        using var fixture = new SteamFixture();
        var storage = fixture.NewStorage();
        storage.SaveAuthData(new SteamAuthData
        {
            Mode = "authenticated",
            Username = username,
            RefreshToken = refreshToken
        });
        var state = fixture.NewState(storage);
        using var steamKit = fixture.NewSteamKit(state, storage);
        MarkLoggedOn(steamKit);

        var status = ReadStatus(fixture.NewController(steamKit, state));

        Assert.False(steamKit.IsSteamAuthenticated);
        Assert.False(steamKit.GetProgress().IsLoggedOn);
        Assert.False(status.IsAuthenticated);
        Assert.Equal("anonymous", status.Mode);
        Assert.Equal("anonymous", status.AuthMode);
        Assert.Empty(status.Username);
    }

    [Fact]
    public void GetStatus_PartialDecryptionBecomesAnonymous()
    {
        using var fixture = new SteamFixture();
        var storage = fixture.NewStorage();
        storage.SaveAuthData(new SteamAuthData
        {
            Mode = "authenticated",
            Username = "steam-user",
            RefreshToken = "refresh-token",
            SteamApiKey = "api-key"
        });
        AuthCredentialFormatTests.ReplaceStoredSecret(fixture.ActiveCredentialsPath, "ENC2:not-valid");

        var freshStorage = fixture.NewStorage();
        var loaded = freshStorage.GetAuthData();
        var state = fixture.NewState(freshStorage);
        using var steamKit = fixture.NewSteamKit(state, freshStorage);
        MarkLoggedOn(steamKit);

        var status = ReadStatus(fixture.NewController(steamKit, state));

        Assert.Equal("steam-user", loaded.Username);
        Assert.Null(loaded.RefreshToken);
        Assert.Equal("api-key", loaded.SteamApiKey);
        Assert.False(steamKit.IsSteamAuthenticated);
        Assert.False(steamKit.GetProgress().IsLoggedOn);
        Assert.False(status.IsAuthenticated);
        Assert.Equal("anonymous", status.Mode);
        Assert.Empty(status.Username);
    }

    [Fact]
    public void GetStatus_CompleteCredentialsDoNotDependOnMappingsOrConnectivity()
    {
        using var fixture = new SteamFixture();
        var storage = fixture.NewStorage();
        storage.SaveAuthData(new SteamAuthData
        {
            Mode = "authenticated",
            Username = "steam-user",
            RefreshToken = "refresh-token"
        });
        var state = fixture.NewState(storage);
        using var steamKit = fixture.NewSteamKit(state, storage);
        MarkLoggedOn(steamKit);
        MarkRebuildRunning(steamKit);

        var progress = steamKit.GetProgress();
        var status = ReadStatus(fixture.NewController(steamKit, state));

        Assert.True(steamKit.IsSteamAuthenticated);
        Assert.True(progress.IsLoggedOn);
        Assert.True(progress.IsProcessing);
        Assert.False(progress.IsReady);
        Assert.False(progress.IsConnected);
        Assert.True(status.IsAuthenticated);
        Assert.Equal("authenticated", status.Mode);
        Assert.Equal("authenticated", status.AuthMode);
        Assert.Equal("steam-user", status.Username);
        Assert.False(status.IsConnected);
    }

    [Fact]
    public void ConfiguredOwnerlessLoginStaysSeparateFromAccountSavedLogin()
    {
        using var fixture = new SteamFixture();
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var storage = fixture.NewStorage();
        storage.SaveAuthData(new SteamAuthData
        {
            Mode = "authenticated",
            Username = "shared-login",
            RefreshToken = "shared-token"
        });
        storage.SaveSavedLogin(accountId, new SteamAuthData
        {
            Mode = "authenticated",
            Username = "account-login",
            RefreshToken = "account-token"
        });
        var state = fixture.NewState(storage);
        using var steamKit = fixture.NewSteamKit(state, storage);
        var daemon = fixture.NewDaemon(storage);

        var status = ReadStatus(fixture.NewController(steamKit, state));
        var available = daemon.GetIntegrationLoginAvailability(accountId);
        var unavailable = daemon.GetIntegrationLoginAvailability(otherAccountId);
        var accountRequired = daemon.GetIntegrationLoginAvailability(null);

        Assert.True(status.IsAuthenticated);
        Assert.Equal("shared-login", status.Username);
        Assert.True(available.Available);
        Assert.Equal("account-login", available.Account);
        Assert.Null(available.Reason);
        Assert.False(unavailable.Available);
        Assert.Null(unavailable.Account);
        Assert.Equal("no-saved-login", unavailable.Reason);
        Assert.False(accountRequired.Available);
        Assert.Equal("account-required", accountRequired.Reason);
    }

    [Fact]
    public void SavedLoginAvailabilityRejectsMissingAndPartiallyDecryptedTokens()
    {
        using var fixture = new SteamFixture();
        var missingTokenAccountId = Guid.NewGuid();
        var partialAccountId = Guid.NewGuid();
        var storage = fixture.NewStorage();
        storage.SaveSavedLogin(missingTokenAccountId, new SteamAuthData
        {
            Mode = "authenticated",
            Username = "missing-token",
            SteamApiKey = "api-key"
        });
        storage.SaveSavedLogin(partialAccountId, new SteamAuthData
        {
            Mode = "authenticated",
            Username = "partial-token",
            RefreshToken = "refresh-token",
            SteamApiKey = "api-key"
        });
        AuthCredentialFormatTests.ReplaceStoredSecret(fixture.SavedCredentialsPath(partialAccountId), "ENC2:not-valid");

        var freshStorage = fixture.NewStorage();
        var daemon = fixture.NewDaemon(freshStorage);
        var missingToken = daemon.GetIntegrationLoginAvailability(missingTokenAccountId);
        var partial = daemon.GetIntegrationLoginAvailability(partialAccountId);

        Assert.False(missingToken.Available);
        Assert.Null(missingToken.Account);
        Assert.Equal("no-saved-login", missingToken.Reason);
        Assert.False(partial.Available);
        Assert.Null(partial.Account);
        Assert.Equal("no-saved-login", partial.Reason);
    }

    private static SteamAuthStatusResponse ReadStatus(SteamAuthController controller)
    {
        var response = controller.GetStatus();
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SteamAuthStatusResponse>(ok.Value);
    }

    private static void MarkLoggedOn(SteamKit2Service steamKit)
    {
        var field = typeof(SteamKit2Service).GetField("_isLoggedOn", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(steamKit, true);
    }

    private static void MarkRebuildRunning(SteamKit2Service steamKit)
    {
        var field = typeof(SteamKit2Service).GetField("_rebuildActive", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(steamKit, 1);
    }

    private sealed class SteamFixture : IDisposable
    {
        private readonly string _root;
        private readonly AuthCredentialFormatTests.TempDirPathResolver _paths;
        private readonly SecureStateEncryptionService _encryption;
        private readonly List<IDisposable> _resources = [];

        public SteamFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "lcm-steam-status-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _paths = new AuthCredentialFormatTests.TempDirPathResolver(_root);
            var apiKeyService = new ApiKeyService(
                NullLogger<ApiKeyService>.Instance,
                new ConfigurationBuilder().Build(),
                _paths);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
            _encryption = new SecureStateEncryptionService(
                protection,
                apiKeyService,
                NullLogger<SecureStateEncryptionService>.Instance);
        }

        public string ActiveCredentialsPath =>
            Path.Combine(_paths.GetSecurityDirectory(), "steam_auth", "credentials.json");

        public string SavedCredentialsPath(Guid accountId) =>
            Path.Combine(_paths.GetSecurityDirectory(), "steam_auth", "saved", $"{accountId:N}.json");

        public SteamAuthStorageService NewStorage() => new(
            NullLogger<SteamAuthStorageService>.Instance,
            _paths,
            _encryption);

        public StateService NewState(SteamAuthStorageService storage) => new(
            NullLogger<StateService>.Instance,
            _paths,
            _encryption,
            storage);

        public SteamKit2Service NewSteamKit(StateService state, SteamAuthStorageService storage)
        {
            var scopes = Proxy<IServiceScopeFactory>();
            var httpClients = Proxy<IHttpClientFactory>();
            var steamService = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
            _resources.Add(steamService);
            var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, _paths, state);
            var webApi = new SteamWebApiService(
                NullLogger<SteamWebApiService>.Instance,
                httpClients,
                storage);

            return new SteamKit2Service(
                NullLogger<SteamKit2Service>.Instance,
                scopes,
                steamService,
                pics,
                state,
                httpClients,
                Proxy<ISignalRNotificationService>(),
                webApi,
                storage,
                Proxy<IUnifiedOperationTracker>());
        }

        public SteamAuthController NewController(SteamKit2Service steamKit, StateService state) => new(
            steamKit,
            state,
            NullLogger<SteamAuthController>.Instance);

        public SteamDaemonService NewDaemon(SteamAuthStorageService storage) => new(
            NullLogger<SteamDaemonService>.Instance,
            Proxy<ISignalRNotificationService>(),
            new ConfigurationBuilder().Build(),
            _paths,
            Proxy<IStateService>(),
            null!,
            null!,
            Proxy<IOptionsMonitor<PrefillNetworkOptions>>(),
            Proxy<ILancacheServerLocator>(),
            Proxy<IPrefillContainerGatewayFactory>(),
            authStorage: storage);

        public void Dispose()
        {
            foreach (var resource in _resources)
            {
                resource.Dispose();
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that will not delete is not worth failing a test over.
            }
        }

        private static T Proxy<T>() where T : class => DispatchProxy.Create<T, NullReturningProxy>();
    }
}
