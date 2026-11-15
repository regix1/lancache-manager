using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Core.Services.UserPreferencesService;

namespace LancacheManager.Tests;

/// <summary>
/// The other two routes that write a guest's refresh rate follow the same lock as the single-field
/// route: the guest's own lock when the administrator set one, else the lock for every guest, which is
/// the order the guest page decides in (<c>effectiveLocked</c> in <c>RefreshRateProvider</c>,
/// <c>Web/src/contexts/RefreshRateContext.tsx</c>). A guest's full preferences save while locked keeps
/// the stored rate and saves the rest; the per-session route refuses a locked guest and lets an
/// unlocked one through.
/// </summary>
public sealed class GuestRefreshRateLockKeptOnEveryRouteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-guest-rate-routes-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(null, true, true)]
    [InlineData(false, true, false)]
    [InlineData(null, false, false)]
    public async Task AGuestsWrites_FollowTheLockTheGuestPageShowsAsync(bool? ownLock, bool everyGuestLock, bool locked)
    {
        await using var scene = await SceneAsync(ownLock, everyGuestLock);

        await scene.Preferences.SavePreferencesAsync(new UserPreferencesDto { RefreshRate = "LIVE", SharpCorners = true });
        var afterSave = await scene.StoredAsync();

        // Put the rate back so the second route starts from the same row.
        await using (var reset = await scene.Database.Factory.CreateDbContextAsync())
        {
            var row = await reset.UserPreferences.SingleAsync(preferences => preferences.SessionId == scene.Guest.Id);
            row.RefreshRate = RefreshRate.Slow;
            await reset.SaveChangesAsync();
        }

        string answer;
        try
        {
            await scene.Sessions.UpdateRefreshRateAsync(scene.Guest.Id, new RefreshRateRequest { RefreshRate = "LIVE" });
            answer = "accepted";
        }
        catch (ForbiddenException)
        {
            answer = "refused";
        }
        var afterSessionRoute = await scene.StoredAsync();

        var wrong = new List<string>();
        var expected = locked ? RefreshRate.Slow : RefreshRate.Live;
        if (afterSave.Rate != expected)
            wrong.Add($"the full save stored rate {afterSave.Rate}, expected {expected}");
        if (!afterSave.SharpCorners)
            wrong.Add("the full save dropped the guest's other fields");
        if (afterSessionRoute.Rate != expected)
            wrong.Add($"the per-session route was {answer} and stored rate {afterSessionRoute.Rate}, expected {expected}");
        Assert.True(
            wrong.Count == 0,
            $"own lock {ownLock?.ToString() ?? "unset"}, every-guest lock {everyGuestLock}: {string.Join("; ", wrong)}");
    }

    private async Task<Scene> SceneAsync(bool? ownLock, bool everyGuestLock)
    {
        Directory.CreateDirectory(_root);
        var database = await TestDatabase.CreateAsync();
        var guest = AccountResetTests.Session(null, "live");
        guest.Preferences = new UserPreferences
        {
            RefreshRate = RefreshRate.Slow,
            RefreshRateLocked = ownLock,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserSessions.Add(guest);
            await seed.SaveChangesAsync();
        }
        var state = StateTestMethods.CreateStateService(_root);
        state.SetGuestRefreshRateLocked(everyGuestLock);

        var configuration = new ConfigurationBuilder().Build();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var app = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(state)
            .AddSingleton(notifications)
            .AddSingleton(new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, database.Factory))
            .AddSingleton(new SessionService(database.Factory, apiKeyService: null!, NullLogger<SessionService>.Instance, state, notifications, configuration))
            .AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = app, Items = { ["Session"] = guest } };
        var preferences = ActivatorUtilities.CreateInstance<UserPreferencesController>(app);
        preferences.ControllerContext = new ControllerContext { HttpContext = http };
        var sessions = ActivatorUtilities.CreateInstance<SessionsController>(app);
        sessions.ControllerContext = new ControllerContext { HttpContext = http };
        return new Scene(database, app, guest, preferences, sessions);
    }

    private sealed record Scene(
        TestDatabase Database,
        ServiceProvider App,
        UserSession Guest,
        UserPreferencesController Preferences,
        SessionsController Sessions) : IAsyncDisposable
    {
        public async Task<(RefreshRate? Rate, bool SharpCorners)> StoredAsync()
        {
            await using var read = await Database.Factory.CreateDbContextAsync();
            var row = await read.UserPreferences.AsNoTracking().SingleAsync(preferences => preferences.SessionId == Guest.Id);
            return (row.RefreshRate, row.SharpCorners);
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
