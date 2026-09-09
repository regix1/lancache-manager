using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SteamKit2;

namespace LancacheManager.Tests;

public sealed class SteamSessionPolicyTests
{
    [Theory]
    [InlineData(EResult.LogonSessionReplaced)]
    [InlineData(EResult.LoggedInElsewhere)]
    public void ConflictPreservesCredentialsAndFaultsPendingWait(EResult result)
    {
        using var fixture = new Fixture();
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(fixture.Service, "_loggedOnTcs", wait);
        fixture.LoggedOff(result);
        Assert.True(Get<bool>(fixture.Service, "_sessionReplaced"));
        Assert.False(Get<bool>(fixture.Service, "_isLoggedOn"));
        Assert.True(fixture.Service.IsSteamAuthenticated);
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
        Assert.Equal("api-key", fixture.Storage.GetAuthData().SteamApiKey);
        var error = Assert.IsType<SteamLogonException>(wait.Task.Exception!.InnerException);
        Assert.Equal("signalr.steamSession.savedSignInPreserved", error.StageKey);
        Assert.DoesNotContain("expired", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogoutWaitsForSessionGateAndPreservesSavedLoginAsync()
    {
        using var fixture = new Fixture();
        var gate = Get<SemaphoreSlim>(fixture.Service, "_sessionGate");
        await gate.WaitAsync();
        var logout = fixture.Service.LogoutAsync();
        Assert.False(logout.IsCompleted);
        Assert.True(fixture.Service.IsSteamAuthenticated);
        gate.Release();
        await logout;
        Assert.False(fixture.Service.IsSteamAuthenticated);
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
        Assert.Equal("other-token", fixture.Storage.GetSavedLogin(fixture.OtherOwner)?.RefreshToken);
    }

    [Theory]
    [InlineData(EResult.InvalidPassword)]
    [InlineData(EResult.AccountLogonDenied)]
    [InlineData(EResult.Expired)]
    public void RejectedCredentialClearsOnlyItsOwnerAndPreservesApiKey(EResult result)
    {
        using var fixture = new Fixture();
        fixture.LoggedOff(result);
        Assert.False(fixture.Service.IsSteamAuthenticated);
        Assert.Null(fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
        Assert.Equal("other-token", fixture.Storage.GetSavedLogin(fixture.OtherOwner)?.RefreshToken);
        Assert.Equal("api-key", fixture.Storage.GetAuthData().SteamApiKey);
        Assert.Null(fixture.Storage.GetAuthData().Username);
        Assert.Equal("anonymous", fixture.Storage.GetAuthData().Mode);
    }

    [Fact]
    public void OldRejectionCannotEraseNewOwnerCredential()
    {
        using var fixture = new Fixture();
        fixture.Storage.SaveAuthData(new SteamAuthData
        {
            OwnerAccountId = fixture.OtherOwner,
            Mode = "authenticated",
            Username = "other",
            RefreshToken = "new-token"
        });
        fixture.LoggedOff(EResult.InvalidPassword);
        Assert.True(fixture.Service.IsSteamAuthenticated);
        Assert.Equal("new-token", fixture.Storage.GetSavedLogin(fixture.OtherOwner)?.RefreshToken);
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
    }

    [Fact]
    public void OldRejectionCannotEraseNewTokenForSameOwner()
    {
        using var fixture = new Fixture();
        fixture.Storage.UpdateAuthData(auth => auth.RefreshToken = "new-token");
        fixture.LoggedOff(EResult.Expired);
        Assert.Equal("new-token", fixture.Storage.GetAuthData().RefreshToken);
        Assert.Equal("new-token", fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
    }

    [Fact]
    public void ExplicitCredentialClearPreservesSavedLogin()
    {
        using var fixture = new Fixture();
        Invoke(fixture.Service, "ClearSteamCredentials", false);
        Assert.False(fixture.Service.IsSteamAuthenticated);
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
        Assert.Equal("api-key", fixture.Storage.GetAuthData().SteamApiKey);
    }

    [Fact]
    public void UnknownDaemonAvailabilityIsDistinctFromNoDaemon()
    {
        using var fixture = new Fixture();
        Assert.Null(Invoke(fixture.Service, "IsSteamDaemonActive"));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(null, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void SessionPolicyUsesAnonymousForActiveUnknownOrReplaced(bool? daemonActive, bool replaced, bool anonymous)
    {
        using var fixture = new Fixture();
        Set(fixture.Service, "_sessionReplaced", replaced);
        Assert.Equal(anonymous, Invoke(fixture.Service, "UseAnonymousSession", daemonActive));
    }

    [Theory]
    [InlineData(EAccountType.Individual, false)]
    [InlineData(EAccountType.AnonUser, true)]
    public void SessionModeUsesLiveSteamAccountType(EAccountType type, bool anonymous)
    {
        using var fixture = new Fixture();
        var client = new SteamClient();
        var clientType = typeof(SteamClient);
        FieldInfo? steamId = null;
        while (clientType != null && steamId == null)
        {
            steamId = clientType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .SingleOrDefault(field => field.FieldType == typeof(SteamID));
            clientType = clientType.BaseType;
        }
        Assert.NotNull(steamId);
        steamId.SetValue(client, new SteamID(1, EUniverse.Public, type));
        Set(fixture.Service, "_steamClient", client);
        Assert.True(fixture.Service.IsSteamAuthenticated);
        Assert.Equal(true, Invoke(fixture.Service, "HasSessionMode", anonymous));
        Assert.Equal(false, Invoke(fixture.Service, "HasSessionMode", !anonymous));
    }

    [Fact]
    public async Task EnsureSessionWaitsForExistingTransitionGateAsync()
    {
        using var fixture = new Fixture();
        var gate = Get<SemaphoreSlim>(fixture.Service, "_sessionGate");
        await gate.WaitAsync();
        using var cancel = new CancellationTokenSource();
        var pending = (Task)Invoke(fixture.Service, "EnsureSessionAsync", cancel.Token, false)!;
        Assert.False(pending.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(fixture.Service.IsSteamAuthenticated);
        gate.Release();
    }

    [Fact]
    public void UnclassifiedLogoffPreservesCredentialAndWakesWait()
    {
        using var fixture = new Fixture();
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(fixture.Service, "_connectedTcs", wait);
        fixture.LoggedOff(EResult.Fail);
        Assert.True(wait.Task.IsFaulted);
        Assert.True(fixture.Service.IsSteamAuthenticated);
        Assert.False(Get<bool>(fixture.Service, "_sessionReplaced"));
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
    }

    [Theory]
    [InlineData(EResult.InvalidPassword)]
    [InlineData(EResult.AccountLogonDenied)]
    [InlineData(EResult.Expired)]
    public void LogonRejectionInvalidatesCapturedCredential(EResult result)
    {
        using var fixture = new Fixture();
        fixture.LoggedOn(result);
        Assert.False(fixture.Service.IsSteamAuthenticated);
        Assert.Null(fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
        Assert.Equal("other-token", fixture.Storage.GetSavedLogin(fixture.OtherOwner)?.RefreshToken);
    }

    [Theory]
    [InlineData(EResult.LogonSessionReplaced)]
    [InlineData(EResult.LoggedInElsewhere)]
    public void LogonConflictPreservesCredentialAndBlocksAccountRetry(EResult result)
    {
        using var fixture = new Fixture();
        fixture.LoggedOn(result);
        Assert.True(fixture.Service.IsSteamAuthenticated);
        Assert.Equal(true, Invoke(fixture.Service, "UseAnonymousSession", false));
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner)?.RefreshToken);
    }

    [Fact]
    public void RebuildConflictRecordsFailureAndRequestsCleanup()
    {
        using var fixture = new Fixture();
        var run = Guid.NewGuid();
        using var cancel = new CancellationTokenSource();
        Set(fixture.Service, "_rebuildActive", 1);
        Set(fixture.Service, "_currentPicsOperationId", run);
        Set(fixture.Service, "_currentRebuildCts", cancel);
        fixture.LoggedOff(EResult.LogonSessionReplaced);
        var failures = Get<System.Collections.Concurrent.ConcurrentDictionary<Guid, string>>(fixture.Service, "_depotRunFailures");
        Assert.True(cancel.IsCancellationRequested);
        Assert.Single(failures);
        Assert.Contains("saved sign-in", failures[run]);
        fixture.LoggedOff(EResult.LogonSessionReplaced);
        Assert.Single(failures);
        Assert.True(fixture.Service.IsSteamAuthenticated);
    }

    private static object? Invoke(object instance, string method, params object?[] args) =>
        instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);

    private static void Set(object instance, string field, object value) =>
        instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static T Get<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "steam-session-tests", Guid.NewGuid().ToString("N"));
        private readonly SteamService _steam;
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid OtherOwner { get; } = Guid.NewGuid();
        public SteamAuthStorageService Storage { get; }
        public SteamKit2Service Service { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var paths = new AuthCredentialFormatTests.TempDirPathResolver(_root);
            var keys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths);
            var encryption = new SecureStateEncryptionService(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
                keys, NullLogger<SecureStateEncryptionService>.Instance);
            Storage = new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption);
            Storage.SaveAuthData(new SteamAuthData
            {
                OwnerAccountId = Owner,
                Mode = "authenticated",
                Username = "user",
                RefreshToken = "token",
                SteamApiKey = "api-key"
            });
            Storage.SaveSavedLogin(OtherOwner, new SteamAuthData
            {
                Mode = "authenticated",
                Username = "other",
                RefreshToken = "other-token"
            });
            var state = new StateService(NullLogger<StateService>.Instance, paths, encryption, Storage);
            var scopes = DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
            var clients = DispatchProxy.Create<IHttpClientFactory, NullReturningProxy>();
            _steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
            Service = new SteamKit2Service(NullLogger<SteamKit2Service>.Instance, scopes, _steam,
                new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state), state, clients,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, Storage), Storage,
                DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>());
            Set(Service, "_sessionCredential", ((Guid?)Owner, "token"));
            Set(Service, "_isLoggedOn", true);
        }

        public void LoggedOff(EResult result)
        {
            var callback = (SteamUser.LoggedOffCallback)RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOffCallback));
            typeof(SteamUser.LoggedOffCallback).GetProperty("Result")!.SetValue(callback, result);
            Invoke(Service, "OnLoggedOff", callback);
        }

        public void LoggedOn(EResult result)
        {
            var callback = (SteamUser.LoggedOnCallback)RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOnCallback));
            typeof(SteamUser.LoggedOnCallback).GetProperty("Result")!.SetValue(callback, result);
            Invoke(Service, "OnLoggedOn", callback);
        }

        public void Dispose()
        {
            Service.Dispose();
            _steam.Dispose();
            Directory.Delete(_root, true);
        }
    }
}
