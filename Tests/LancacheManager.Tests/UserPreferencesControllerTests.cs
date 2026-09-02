using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
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
/// connected client rather than only the session it names, and the row carries the admin-only columns
/// whoever last wrote it, so a guest's own save must not carry an admin's allowed formats, refresh lock or
/// thread caps out to everyone.
/// </summary>
public sealed class UserPreferencesControllerTests
{
    private static readonly string[] _adminChosenFormats = ["server-24h", "utc"];

    [Fact]
    public async Task GuestSave_AnnouncesNoAdminOnlyField()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Guest);
        await database.AddAdminOnlyValuesAsync(sessionId);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Guest);

        var result = await controller.SavePreferencesAsync(new UserPreferencesDto { SharpCorners = true });

        Assert.IsType<OkObjectResult>(result);
        AssertNoAdminOnlyField(Assert.Single(notifications.Events));
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
        AssertNoAdminOnlyField(Assert.Single(notifications.Events));

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserPreferences.SingleAsync(preferences => preferences.SessionId == sessionId);

        Assert.Equal(JsonSerializer.Serialize(_adminChosenFormats), stored.AllowedTimeFormats);
        Assert.True(stored.RefreshRateLocked);
        Assert.Equal(4, stored.SteamMaxThreadCount);
        Assert.Equal(2, stored.EpicMaxThreadCount);
    }

    [Fact]
    public async Task GuestSingleKeyWrite_AnnouncesNoAdminOnlyField()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Guest);
        await database.AddAdminOnlyValuesAsync(sessionId);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Guest);

        var result = await controller.UpdatePreferenceAsync(
            "sharpCorners", JsonSerializer.SerializeToElement(true));

        Assert.IsType<OkObjectResult>(result);
        AssertNoAdminOnlyField(Assert.Single(notifications.Events));
    }

    [Fact]
    public async Task GuestClockWrite_AnnouncesNoAdminOnlyField()
    {
        await using var database = await PreferencesDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync(SessionType.Guest);
        await database.AddAdminOnlyValuesAsync(sessionId);

        var (controller, notifications) = CreateController(database, sessionId, SessionType.Guest);

        var result = await controller.UpdateClockPreferencesAsync(
            new ClockPreferences { UseUtcTimezone = true });

        Assert.IsType<OkObjectResult>(result);
        AssertNoAdminOnlyField(Assert.Single(notifications.Events));
    }

    /// <summary>
    /// An admin's own write still carries them. The admin screens are the ones that set these values, and
    /// they need to see them land.
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
            EpicMaxThreadCount = 2
        });

        Assert.IsType<OkObjectResult>(result);

        var announced = AnnouncedPreferences(Assert.Single(notifications.Events));
        Assert.Equal(_adminChosenFormats, announced.AllowedTimeFormats);
        Assert.True(announced.RefreshRateLocked);
        Assert.Equal(4, announced.SteamMaxThreadCount);
        Assert.Equal(2, announced.EpicMaxThreadCount);
    }

    private static void AssertNoAdminOnlyField(CapturedEvent announced)
    {
        var preferences = AnnouncedPreferences(announced);
        Assert.Null(preferences.AllowedTimeFormats);
        Assert.Null(preferences.RefreshRateLocked);
        Assert.Null(preferences.SteamMaxThreadCount);
        Assert.Null(preferences.EpicMaxThreadCount);
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
                new ConfigurationBuilder().Build()));

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

        private PreferencesDatabase(TestDatabase database)
        {
            _database = database;
            Factory = database.Factory;
        }

        public TestDbContextFactory Factory { get; }

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
        /// Puts the four admin-only columns on the session's row the way an admin restricting one guest
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
                UpdatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync() => await _database.DisposeAsync();
    }
}
