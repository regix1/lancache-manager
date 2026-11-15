using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Core.Services.UserPreferencesService;

namespace LancacheManager.Tests;

/// <summary>
/// Covers what leaves the preferences controller after a write. The stored row is announced to every
/// connected client rather than only the session it names, so it never carries the thread caps. It does
/// carry the session's stored refresh lock and allowed formats, even after a guest's own save: the guest's
/// own window applies them from this event, and a null there put it back on the defaults.
/// </summary>
public sealed class UserPreferencesControllerTests
{
    private static readonly string[] _adminChosenFormats = ["server-24h", "utc"];

    [Fact]
    public async Task GuestSave_AnnouncesNoThreadCap()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Guest);
        await database.AddAdminOnlyValuesAsync(sessionId);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Guest);

        var result = await controller.SavePreferencesAsync(new UserPreferencesDto { SharpCorners = true });

        Assert.IsType<OkObjectResult>(result);
        AssertAnnouncedAsStored(Assert.Single(notifications.Events));
    }

    [Fact]
    public async Task GuestSave_PreservesAdminOnlyFieldsWhileAnnouncingGuestView()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Guest);
        await database.AddAdminOnlyValuesAsync(sessionId);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Guest);

        var result = await controller.SavePreferencesAsync(new UserPreferencesDto { SharpCorners = true });

        Assert.IsType<OkObjectResult>(result);
        AssertAnnouncedAsStored(Assert.Single(notifications.Events));

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserPreferences.SingleAsync(preferences => preferences.SessionId == sessionId);

        Assert.Equal(JsonSerializer.Serialize(_adminChosenFormats), stored.AllowedTimeFormats);
        Assert.True(stored.RefreshRateLocked);
        Assert.Equal(4, stored.SteamMaxThreadCount);
        Assert.Equal(2, stored.EpicMaxThreadCount);
        Assert.Equal(3, stored.XboxMaxThreadCount);
        Assert.Equal(5, stored.BattleNetMaxThreadCount);
        Assert.Equal(6, stored.RiotMaxThreadCount);
    }

    [Fact]
    public async Task GuestSingleKeyWrite_AnnouncesNoThreadCap()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Guest);
        await database.AddAdminOnlyValuesAsync(sessionId);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Guest);

        var result = await controller.UpdatePreferenceAsync(
            "sharpCorners", JsonSerializer.SerializeToElement(true));

        Assert.IsType<OkObjectResult>(result);
        AssertAnnouncedAsStored(Assert.Single(notifications.Events));
    }

    [Fact]
    public async Task GuestClockWrite_AnnouncesNoThreadCap()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Guest);
        await database.AddAdminOnlyValuesAsync(sessionId);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Guest);

        var result = await controller.UpdateClockPreferencesAsync(
            new ClockPreferences { UseUtcTimezone = true });

        Assert.IsType<OkObjectResult>(result);
        AssertAnnouncedAsStored(Assert.Single(notifications.Events));
    }

    /// <summary>
    /// An admin's own write carries the lock and formats it set, and leaves the thread caps out like every
    /// other announcement.
    /// </summary>
    [Fact]
    public async Task AdminSave_StillAnnouncesTheAdminOnlyFields()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Admin);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Admin);

        var result = await controller.SavePreferencesAsync(new UserPreferencesDto
        {
            SharpCorners = true,
            AllowedTimeFormats = _adminChosenFormats,
            RefreshRateLocked = true,
            SteamMaxThreadCount = 4,
            EpicMaxThreadCount = 2,
            XboxMaxThreadCount = 3,
            BattleNetMaxThreadCount = 5,
            RiotMaxThreadCount = 6
        });

        Assert.IsType<OkObjectResult>(result);

        var announced = AnnouncedPreferences(Assert.Single(notifications.Events));
        Assert.Equal(_adminChosenFormats, announced.AllowedTimeFormats);
        Assert.True(announced.RefreshRateLocked);
        Assert.Null(announced.SteamMaxThreadCount);
        Assert.Null(announced.EpicMaxThreadCount);
        Assert.Null(announced.XboxMaxThreadCount);
        Assert.Null(announced.BattleNetMaxThreadCount);
        Assert.Null(announced.RiotMaxThreadCount);
    }

    /// <summary>
    /// An admin's save for one guest is announced to every connected client, other guests included, so the
    /// guest's thread caps stay off it. The refresh lock still travels: the named guest's own client applies
    /// it from this event. The stored row and a later read keep the caps.
    /// </summary>
    [Fact]
    public async Task AdminSaveForGuestSession_AnnouncesNoThreadCap()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var adminSessionId = await database.AddSessionAsync(SessionType.Admin);
        var guestSessionId = await database.AddSessionAsync(SessionType.Guest);

        var (controller, notifications) = CreateController(database, adminSessionId, SessionType.Admin);

        var result = await controller.SaveForSessionAsync(guestSessionId, new UserPreferencesDto
        {
            RefreshRateLocked = true,
            SteamMaxThreadCount = 4,
            XboxMaxThreadCount = 2
        });

        Assert.IsType<OkObjectResult>(result.Result);

        var announced = AnnouncedPreferences(Assert.Single(notifications.Events));
        Assert.True(announced.RefreshRateLocked);
        Assert.Null(announced.SteamMaxThreadCount);
        Assert.Null(announced.EpicMaxThreadCount);
        Assert.Null(announced.XboxMaxThreadCount);
        Assert.Null(announced.BattleNetMaxThreadCount);
        Assert.Null(announced.RiotMaxThreadCount);

        var reread = await controller.GetForSessionAsync(guestSessionId);
        var stored = Assert.IsType<UserPreferencesDto>(Assert.IsType<OkObjectResult>(reread.Result).Value);
        Assert.Equal(4, stored.SteamMaxThreadCount);
        Assert.Equal(2, stored.XboxMaxThreadCount);
    }

    /// <summary>
    /// A session removed between the pane's read and its save answers 404. A session id that never existed
    /// takes the same path, because the visibility check lets a missing session through.
    /// </summary>
    [Fact]
    public async Task AdminSaveForMissingSession_AnswersNotFound()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var adminSessionId = await database.AddSessionAsync(SessionType.Admin);

        var (controller, notifications) = CreateController(database, adminSessionId, SessionType.Admin);

        var result = await controller.SaveForSessionAsync(Guid.NewGuid(), new UserPreferencesDto { SharpCorners = true });

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Empty(notifications.Events);
    }

    /// <summary>
    /// The guest's announcement after its own write: the lock and formats the admin stored on its row, and
    /// no thread cap.
    /// </summary>
    private static void AssertAnnouncedAsStored(CapturedEvent announced)
    {
        var preferences = AnnouncedPreferences(announced);
        Assert.Equal(_adminChosenFormats, preferences.AllowedTimeFormats);
        Assert.True(preferences.RefreshRateLocked);
        Assert.Null(preferences.SteamMaxThreadCount);
        Assert.Null(preferences.EpicMaxThreadCount);
        Assert.Null(preferences.XboxMaxThreadCount);
        Assert.Null(preferences.BattleNetMaxThreadCount);
        Assert.Null(preferences.RiotMaxThreadCount);
    }

    private static UserPreferencesDto AnnouncedPreferences(CapturedEvent announced)
    {
        Assert.Equal(SignalREvents.UserPreferencesUpdated, announced.EventName);
        Assert.NotNull(announced.Payload);

        var property = announced.Payload!.GetType().GetProperty("preferences")
            ?? throw new InvalidOperationException("The announced payload carries no preferences.");
        return Assert.IsType<UserPreferencesDto>(property.GetValue(announced.Payload));
    }

    private static (UserPreferencesController Controller, RecordingNotifications Notifications) CreateController(
        PreferencesDatabase database, Guid sessionId, SessionType sessionType)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var controller = new UserPreferencesController(
            NullLogger<UserPreferencesController>.Instance,
            new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, database.Factory),
            notifications,
            new SessionService(
                database.Factory,
                apiKeyService: null!,
                NullLogger<SessionService>.Instance,
                stateService: null!,
                signalR: notifications,
                new ConfigurationBuilder().Build()),
            database.State);

        var context = new DefaultHttpContext();
        context.Items["Session"] = new UserSession
        {
            Id = sessionId,
            SessionType = sessionType,
            SessionTokenHash = sessionId.ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenAtUtc = DateTime.UtcNow
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        return (controller, (RecordingNotifications)(object)notifications);
    }

    /// <summary>
    /// Records the broadcasts the controller sends. Built as a proxy rather than an implementation because
    /// the notification interface carries a member per hub and none of the rest is exercised here.
    /// </summary>
    private sealed class PreferencesDatabase : IAsyncDisposable
    {
        private readonly TestDatabase _database;

        private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), $"lcm-preferences-controller-{Guid.NewGuid():N}");

        private PreferencesDatabase(TestDatabase database)
        {
            _database = database;
            Factory = database.Factory;
            Directory.CreateDirectory(_stateRoot);
            State = StateTestMethods.CreateStateService(_stateRoot);
        }

        public TestDbContextFactory Factory { get; }

        /// <summary>The state file the controller reads the guest refresh-rate lock from.</summary>
        public StateService State { get; }

        public static async Task<PreferencesDatabase> CreateAsync() =>
            new(await TestDatabase.CreateAsync());

        public async Task<Guid> AddSessionAsync(SessionType sessionType)
        {
            var sessionId = Guid.NewGuid();
            await using var context = Factory.CreateDbContext();
            context.UserSessions.Add(new UserSession
            {
                Id = sessionId,
                SessionTokenHash = sessionId.ToString("N"),
                SessionType = sessionType,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            return sessionId;
        }

        /// <summary>
        /// Puts the seven admin-only columns on the session's row the way an admin restricting one guest
        /// does, so a later guest write has something to carry out if it is not stripped.
        /// </summary>
        public async Task AddAdminOnlyValuesAsync(Guid sessionId)
        {
            await using var context = Factory.CreateDbContext();
            context.UserPreferences.Add(new UserPreferences
            {
                SessionId = sessionId,
                AllowedTimeFormats = JsonSerializer.Serialize(_adminChosenFormats),
                RefreshRateLocked = true,
                SteamMaxThreadCount = 4,
                EpicMaxThreadCount = 2,
                XboxMaxThreadCount = 3,
                BattleNetMaxThreadCount = 5,
                RiotMaxThreadCount = 6,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
            if (Directory.Exists(_stateRoot))
                Directory.Delete(_stateRoot, recursive: true);
        }
    }
}
