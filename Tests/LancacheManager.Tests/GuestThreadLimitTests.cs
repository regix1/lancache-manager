using System.Text.Json.Nodes;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Core.Services.UserPreferencesService;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the thread cap on a guest's prefill. Each service has its own limit: the session's own setting
/// when an admin set one, else that service's guest default. Battle.net, Riot and Xbox used to take the
/// Steam default instead, so changing the Steam limit moved them and their own settings did nothing.
/// </summary>
public sealed class GuestThreadLimitTests : IDisposable
{
    private readonly string _root;

    public GuestThreadLimitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-guest-thread-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task XboxGuest_IsCappedByTheXboxDefaultNotTheSteamOne()
    {
        await using var database = await TestDatabase.CreateAsync();
        var stateService = CreateStateService(_root);
        stateService.SetDefaultGuestMaxThreadCount(null);
        stateService.SetXboxDefaultGuestMaxThreadCount(2);
        var guest = await AddSessionAsync(database, SessionType.Guest);

        Assert.Equal(2, GuestThreadLimit.Resolve(PrefillPlatform.Xbox, guest, NewPreferencesService(database), stateService));
    }

    [Fact]
    public async Task XboxGuestWithItsOwnLimit_IsCappedByIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var stateService = CreateStateService(_root);
        stateService.SetXboxDefaultGuestMaxThreadCount(2);
        var guest = await AddSessionAsync(database, SessionType.Guest);
        var preferencesService = NewPreferencesService(database);
        await preferencesService.SavePreferencesAsync(guest.Id, new UserPreferencesDto { XboxMaxThreadCount = 4 });

        Assert.Equal(4, GuestThreadLimit.Resolve(PrefillPlatform.Xbox, guest, preferencesService, stateService));
    }

    [Fact]
    public async Task BattleNetAndRiotGuests_AreCappedByTheirOwnDefaults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var stateService = CreateStateService(_root);
        stateService.SetDefaultGuestMaxThreadCount(8);
        stateService.SetBattleNetDefaultGuestMaxThreadCount(3);
        stateService.SetRiotDefaultGuestMaxThreadCount(5);
        var guest = await AddSessionAsync(database, SessionType.Guest);
        var preferencesService = NewPreferencesService(database);

        Assert.Equal(3, GuestThreadLimit.Resolve(PrefillPlatform.BattleNet, guest, preferencesService, stateService));
        Assert.Equal(5, GuestThreadLimit.Resolve(PrefillPlatform.Riot, guest, preferencesService, stateService));
    }

    [Fact]
    public async Task SteamAndEpicGuests_KeepTheirLimits()
    {
        await using var database = await TestDatabase.CreateAsync();
        var stateService = CreateStateService(_root);
        stateService.SetDefaultGuestMaxThreadCount(6);
        stateService.SetEpicDefaultGuestMaxThreadCount(7);
        var guest = await AddSessionAsync(database, SessionType.Guest);
        var preferencesService = NewPreferencesService(database);

        Assert.Equal(6, GuestThreadLimit.Resolve(PrefillPlatform.Steam, guest, preferencesService, stateService));
        Assert.Equal(7, GuestThreadLimit.Resolve(PrefillPlatform.Epic, guest, preferencesService, stateService));

        await preferencesService.SavePreferencesAsync(guest.Id, new UserPreferencesDto
        {
            SteamMaxThreadCount = 2,
            EpicMaxThreadCount = 3
        });

        Assert.Equal(2, GuestThreadLimit.Resolve(PrefillPlatform.Steam, guest, preferencesService, stateService));
        Assert.Equal(3, GuestThreadLimit.Resolve(PrefillPlatform.Epic, guest, preferencesService, stateService));
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    [InlineData(PrefillPlatform.Xbox)]
    public async Task AccountHolder_IsNeverCapped(PrefillPlatform platform)
    {
        await using var database = await TestDatabase.CreateAsync();
        var stateService = CreateStateService(_root);
        stateService.SetDefaultGuestMaxThreadCount(2);
        stateService.SetEpicDefaultGuestMaxThreadCount(2);
        stateService.SetBattleNetDefaultGuestMaxThreadCount(2);
        stateService.SetRiotDefaultGuestMaxThreadCount(2);
        stateService.SetXboxDefaultGuestMaxThreadCount(2);
        var admin = await AddSessionAsync(database, SessionType.Admin);

        Assert.Null(GuestThreadLimit.Resolve(platform, admin, NewPreferencesService(database), stateService));
    }

    // The guest prefill panel asks for the limit of the service it shows, so its thread choices stop at
    // the number that run will actually get.
    [Fact]
    public async Task PrefillDefaults_ForXbox_AnswersTheXboxLimit()
    {
        await using var database = await TestDatabase.CreateAsync();
        var stateService = CreateStateService(_root);
        stateService.SetDefaultGuestMaxThreadCount(null);
        stateService.SetXboxDefaultGuestMaxThreadCount(2);
        var guest = await AddSessionAsync(database, SessionType.Guest);
        var controller = CreateSystemController(stateService, NewPreferencesService(database), guest);

        var result = controller.GetPrefillDefaults("xbox");

        var defaults = Assert.IsType<PrefillDefaultsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, defaults.MaxThreadLimit);
    }

    // A value that is not a service id is the caller's mistake, not a server fault.
    [Fact]
    public async Task PrefillDefaults_ForAValueThatIsNoService_AnswersBadRequest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var stateService = CreateStateService(_root);
        var guest = await AddSessionAsync(database, SessionType.Guest);
        var controller = CreateSystemController(stateService, NewPreferencesService(database), guest);

        var result = controller.GetPrefillDefaults("7");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // An install upgrading from the shared limit keeps the cap its Battle.net and Riot guests had.
    [Fact]
    public void UpgradedInstall_CopiesTheSteamLimitToBattleNetAndRiot()
    {
        WriteState(new JsonObject { ["DefaultGuestMaxThreadCount"] = 3 });

        var state = CreateStateService(_root).GetState();

        Assert.Equal(3, state.BattleNetDefaultGuestMaxThreadCount);
        Assert.Equal(3, state.RiotDefaultGuestMaxThreadCount);
        Assert.True(state.GuestThreadLimitsSeeded);
    }

    // Null is also the "no limit" an admin picks, so once the copy has run it never runs again.
    [Fact]
    public void SeededInstall_KeepsNoLimit()
    {
        WriteState(new JsonObject
        {
            ["DefaultGuestMaxThreadCount"] = 3,
            ["GuestThreadLimitsSeeded"] = true
        });

        var state = CreateStateService(_root).GetState();

        Assert.Null(state.BattleNetDefaultGuestMaxThreadCount);
        Assert.Null(state.RiotDefaultGuestMaxThreadCount);
    }

    [Fact]
    public void NoLimitChosenAfterTheCopy_SurvivesARestart()
    {
        WriteState(new JsonObject { ["DefaultGuestMaxThreadCount"] = 3 });
        var firstRun = CreateStateService(_root);
        firstRun.GetState();
        firstRun.SetBattleNetDefaultGuestMaxThreadCount(null);

        var restarted = CreateStateService(_root).GetState();

        Assert.Null(restarted.BattleNetDefaultGuestMaxThreadCount);
        Assert.Equal(3, restarted.RiotDefaultGuestMaxThreadCount);
    }

    private string StateFilePath => Path.Combine(_root, "GetStateDirectory", "state.json");

    private void WriteState(JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        File.WriteAllText(StateFilePath, state.ToJsonString());
    }

    private static UserPreferencesService NewPreferencesService(TestDatabase database) =>
        new(NullLogger<UserPreferencesService>.Instance, database.Factory);

    private static async Task<UserSession> AddSessionAsync(TestDatabase database, SessionType sessionType)
    {
        var sessionId = Guid.NewGuid();
        var session = new UserSession
        {
            Id = sessionId,
            SessionTokenHash = sessionId.ToString("N"),
            SessionType = sessionType,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenAtUtc = DateTime.UtcNow
        };

        await using var context = database.Factory.CreateDbContext();
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private static SystemController CreateSystemController(
        StateService stateService, UserPreferencesService preferencesService, UserSession caller)
    {
        var controller = new SystemController(
            stateService,
            new ConfigurationBuilder().Build(),
            NullLogger<SystemController>.Instance,
            pathResolver: null!,
            cacheClearingService: null!,
            steamKit2Service: null!,
            datasourceService: null!,
            notifications: null!,
            preferencesService,
            capabilityService: null!,
            nginxLogRotationService: null!,
            cacheManagementService: null!,
            dbContextFactory: null!,
            claimWindow: null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Items["Session"] = caller;
        return controller;
    }
}
