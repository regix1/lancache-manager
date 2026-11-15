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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The admin lets one guest change its refresh rate while every other guest stays locked. When that guest
/// saves a rate of its own, its window learns the result from the preferences announcement, and a reload
/// learns it from the guest's own preferences read. Both must report the same refresh lock. Otherwise the
/// window drops back to the lock every guest has and stays locked until the guest reloads.
/// </summary>
public sealed class GuestOwnRateSaveLockTests
{
    /// <summary>The hub writes events with the web defaults, so this is the JSON a window receives.</summary>
    private static readonly JsonSerializerOptions _wire = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GuestOwnRateSave_AnnouncesTheLockItsReloadReads()
    {
        var stateRoot = Path.Combine(Path.GetTempPath(), $"lcm-guest-own-rate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateRoot);
        try
        {
            await SaveAndCompareAsync(stateRoot);
        }
        finally
        {
            Directory.Delete(stateRoot, recursive: true);
        }
    }

    private static async Task SaveAndCompareAsync(string stateRoot)
    {
        await using var database = await TestDatabase.CreateAsync();
        var state = StateTestMethods.CreateStateService(stateRoot);
        state.SetGuestRefreshRateLocked(true);

        var sessionId = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserSessions.Add(new UserSession
            {
                Id = sessionId,
                SessionTokenHash = sessionId.ToString("N"),
                SessionType = SessionType.Guest,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            // "Guest may change the refresh rate" turned On for this guest by the admin.
            context.UserPreferences.Add(new UserPreferences
            {
                SessionId = sessionId,
                RefreshRate = RefreshRate.Relaxed,
                RefreshRateLocked = false,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

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
            state);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = new UserSession
        {
            Id = sessionId,
            SessionType = SessionType.Guest,
            SessionTokenHash = sessionId.ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            LastSeenAtUtc = DateTime.UtcNow
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var save = await controller.UpdatePreferenceAsync("refreshRate", JsonSerializer.SerializeToElement("SLOW"));
        Assert.IsType<OkObjectResult>(save);

        var recorded = (RecordingNotifications)(object)notifications;
        var announcement = Assert.Single(
            recorded.Events,
            captured => captured.EventName == SignalREvents.UserPreferencesUpdated);
        var sent = JsonSerializer.SerializeToElement(announcement.Payload, _wire);
        Assert.Equal(sessionId, sent.GetProperty("sessionId").GetGuid());
        var announced = sent.GetProperty("preferences");
        Assert.Equal("SLOW", announced.GetProperty("refreshRate").GetString());

        var reload = Assert.IsType<OkObjectResult>(controller.GetPreferences().Result);
        var reloaded = JsonSerializer.SerializeToElement(reload.Value, _wire);
        var reloadedLock = reloaded.GetProperty("refreshRateLocked").GetRawText();
        Assert.Equal("false", reloadedLock);

        // An announcement may leave the lock out, so the window keeps the lock it already knows. One that
        // names a lock must name the lock a reload reads.
        if (announced.TryGetProperty("refreshRateLocked", out var announcedLock))
        {
            Assert.True(
                announcedLock.GetRawText() == reloadedLock,
                $"The guest's own rate save told its window refreshRateLocked = {announcedLock.GetRawText()}, but a reload "
                + $"reads {reloadedLock}. The window falls back to the lock every guest has and stays locked until it reloads.");
        }
    }
}
