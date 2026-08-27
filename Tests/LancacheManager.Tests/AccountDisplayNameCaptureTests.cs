using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LancacheManager.Tests.DaemonTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Proves authenticated daemon status updates capture a resolved account display name into
/// <see cref="DaemonSession.Username"/> and <see cref="DaemonSession.AccountUsername"/> (persisted via
/// SetUsernameAsync) without an Epic/Xbox platform gate, including AccountDisplayName-only (GetStatus)
/// and DisplayName-only (AuthState) payloads.
/// </summary>
public class AccountDisplayNameCaptureTests
{
    [Fact]
    public async Task OnStatusChangeAsync_Xbox_AccountDisplayNameOnly_SetsUsernameAndAccountUsername()
    {
        var (daemon, session, sessionService, recorder) = await CreateDaemonWithSeededSessionAsync(platform: "Xbox");

        await InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, new DaemonStatus
        {
            Status = "logged-in",
            AccountDisplayName = "XboxGamerTag"
        });

        Assert.Equal("XboxGamerTag", session.Username);
        Assert.Equal("XboxGamerTag", session.AccountUsername);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);

        var persisted = await sessionService.GetSessionAsync(session.Id);
        Assert.NotNull(persisted);
        Assert.Equal("XboxGamerTag", persisted!.AccountUsername);

        Assert.Contains(recorder.Invocations, i =>
            i.Method == nameof(ISignalRNotificationService.NotifySteamHubAsync)
            && i.Args.Length > 0
            && (i.Args[0] as string) == SignalREvents.DaemonSessionUpdated);
    }

    [Fact]
    public async Task OnStatusChangeAsync_Xbox_DisplayNameOnly_SetsUsernameAndAccountUsername()
    {
        var (daemon, session, sessionService, _) = await CreateDaemonWithSeededSessionAsync(platform: "Xbox");

        await InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, new DaemonStatus
        {
            Status = "logged-in",
            DisplayName = "AuthStateGamertag"
        });

        Assert.Equal("AuthStateGamertag", session.Username);
        Assert.Equal("AuthStateGamertag", session.AccountUsername);

        var persisted = await sessionService.GetSessionAsync(session.Id);
        Assert.NotNull(persisted);
        Assert.Equal("AuthStateGamertag", persisted!.AccountUsername);
    }

    [Fact]
    public async Task OnStatusChangeAsync_BattleNet_WithResolvedName_CapturesWithoutPlatformGate()
    {
        var (daemon, session, sessionService, _) = await CreateDaemonWithSeededSessionAsync(platform: "battlenet");

        await InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, new DaemonStatus
        {
            Status = "logged-in",
            AccountDisplayName = "UnexpectedBattleNetName"
        });

        Assert.Equal("UnexpectedBattleNetName", session.Username);
        Assert.Equal("UnexpectedBattleNetName", session.AccountUsername);

        var persisted = await sessionService.GetSessionAsync(session.Id);
        Assert.NotNull(persisted);
        Assert.Equal("UnexpectedBattleNetName", persisted!.AccountUsername);
    }

    [Fact]
    public async Task OnStatusChangeAsync_BattleNet_AnonymousNoName_LeavesUsernameEmpty()
    {
        var (daemon, session, sessionService, _) = await CreateDaemonWithSeededSessionAsync(platform: "battlenet");

        await InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, new DaemonStatus
        {
            Status = "logged-in"
        });

        Assert.Null(session.Username);
        Assert.Null(session.AccountUsername);
        Assert.Equal(DaemonAuthState.Authenticated, session.AuthState);

        var persisted = await sessionService.GetSessionAsync(session.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.AccountUsername);
    }

    [Fact]
    public async Task OnStatusChangeAsync_LateNameWhileAlreadyAuthenticated_PushesSessionUpdated()
    {
        var (daemon, session, sessionService, recorder) = await CreateDaemonWithSeededSessionAsync(platform: "Xbox");
        session.AuthState = DaemonAuthState.Authenticated;

        await InvokePrivateHandlerAsync(daemon, "OnStatusChangeAsync", session, new DaemonStatus
        {
            Status = "logged-in",
            AccountDisplayName = "LateGamertag"
        });

        Assert.Equal("LateGamertag", session.Username);
        Assert.Equal("LateGamertag", session.AccountUsername);

        var persisted = await sessionService.GetSessionAsync(session.Id);
        Assert.NotNull(persisted);
        Assert.Equal("LateGamertag", persisted!.AccountUsername);

        Assert.Contains(recorder.Invocations, i =>
            i.Method == nameof(ISignalRNotificationService.NotifySteamHubAsync)
            && i.Args.Length > 0
            && (i.Args[0] as string) == SignalREvents.DaemonSessionUpdated);
    }

    [Fact]
    public void ParseAccountDisplayName_PrefersAccountDisplayName_ThenUsername_ThenDisplayName()
    {
        using var doc = JsonDocument.Parse("""{"displayName":"fromDisplay","username":"fromUser","accountDisplayName":"fromAccount"}""");
        Assert.Equal("fromAccount", DaemonStatus.ParseAccountDisplayName(doc.RootElement));

        using var docUser = JsonDocument.Parse("""{"displayName":"fromDisplay","Username":"fromUser"}""");
        Assert.Equal("fromUser", DaemonStatus.ParseAccountDisplayName(docUser.RootElement));

        using var docDisplay = JsonDocument.Parse("""{"DisplayName":"fromDisplay"}""");
        Assert.Equal("fromDisplay", DaemonStatus.ParseAccountDisplayName(docDisplay.RootElement));
    }

    [Fact]
    public void ResolveAccountDisplayName_PrefersAccountDisplayName_OverDisplayName()
    {
        var status = new DaemonStatus
        {
            AccountDisplayName = "account",
            DisplayName = "display"
        };
        Assert.Equal("account", status.ResolveAccountDisplayName());

        Assert.Equal("display", new DaemonStatus { DisplayName = "display" }.ResolveAccountDisplayName());
        Assert.Null(new DaemonStatus().ResolveAccountDisplayName());
        Assert.Null(new DaemonStatus { AccountDisplayName = "  ", DisplayName = "" }.ResolveAccountDisplayName());
    }

    private static async Task<(TestableSteamDaemonService Daemon, DaemonSession Session, PrefillSessionService SessionService, RecordingNotificationProxy Recorder)>
        CreateDaemonWithSeededSessionAsync(string platform)
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"account_display_name_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new TestDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var recorder = (RecordingNotificationProxy)(object)notifications;
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        var daemon = new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
            stateService, sessionService, cacheService, networkOptions);

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.NotAuthenticated,
            Platform = platform,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = new FakeReconnectDaemonClient()
        };
        daemon.InjectSession(session);

        // PrefillPlatform parses "Xbox"/"BattleNet"; runtime session.Platform may be "battlenet".
        var persistPlatform = platform.Equals("battlenet", StringComparison.OrdinalIgnoreCase) ? "BattleNet" : platform;
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, $"container-{session.Id}", $"name-{session.Id}", session.ExpiresAt, persistPlatform);

        return (daemon, session, sessionService, recorder);
    }
}
