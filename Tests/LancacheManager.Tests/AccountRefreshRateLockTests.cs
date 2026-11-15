using System.Reflection;
using System.Text.Json;
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
/// A user-role account's session has a refresh rate of its own, which an admin may lock. While its
/// session is locked the account cannot change that rate on any route that writes it, and it never
/// writes the lock itself. With no lock of its own the account may change its rate, whatever the lock
/// for every guest says.
/// </summary>
public sealed class AccountRefreshRateLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-account-rate-lock-{Guid.NewGuid():N}");

    [Fact]
    public async Task ALockedAccountSession_KeepsItsRateAndLockOnEveryRouteAsync()
    {
        await using var scene = await SceneAsync(ownLock: true, everyGuestLock: false);
        var wrong = new List<string>();

        var patchRate = await scene.Preferences.UpdatePreferenceAsync("refreshRate", JsonSerializer.SerializeToElement("LIVE"));
        if (patchRate is not ForbidResult)
            wrong.Add($"PATCH own refreshRate answered {patchRate.GetType().Name}, not Forbid");

        try
        {
            await scene.Sessions.UpdateRefreshRateAsync(scene.Account.Id, new RefreshRateRequest { RefreshRate = "LIVE" });
            wrong.Add("the per-session route accepted the rate");
        }
        catch (ForbiddenException)
        {
        }

        var patchLock = await scene.Preferences.UpdatePreferenceAsync("refreshRateLocked", JsonSerializer.SerializeToElement(false));
        if (patchLock is not ForbidResult)
            wrong.Add($"PATCH own refreshRateLocked answered {patchLock.GetType().Name}, not Forbid");

        await scene.Preferences.SavePreferencesAsync(new UserPreferencesDto
        {
            RefreshRate = "LIVE",
            RefreshRateLocked = false,
            SharpCorners = true
        });

        var stored = await scene.StoredAsync();
        if (stored.Rate != RefreshRate.Slow)
            wrong.Add($"the stored rate is {stored.Rate}, not the locked {RefreshRate.Slow}");
        if (stored.Locked != true)
            wrong.Add($"the stored lock is {stored.Locked?.ToString() ?? "unset"}, not the admin's lock");
        if (!stored.SharpCorners)
            wrong.Add("the own save dropped the account's other fields");
        Assert.True(wrong.Count == 0, "a locked user-role account: " + string.Join("; ", wrong));
    }

    [Fact]
    public async Task AnAccountSessionWithNoLock_ChangesItsRateWhileGuestsAreLockedAsync()
    {
        await using var scene = await SceneAsync(ownLock: null, everyGuestLock: true);
        var wrong = new List<string>();

        var patchRate = await scene.Preferences.UpdatePreferenceAsync("refreshRate", JsonSerializer.SerializeToElement("LIVE"));
        if (patchRate is not OkObjectResult)
            wrong.Add($"PATCH own refreshRate answered {patchRate.GetType().Name}, not Ok");
        if ((await scene.StoredAsync()).Rate != RefreshRate.Live)
            wrong.Add("PATCH own refreshRate did not store the rate");

        try
        {
            await scene.Sessions.UpdateRefreshRateAsync(scene.Account.Id, new RefreshRateRequest { RefreshRate = "RELAXED" });
        }
        catch (ForbiddenException)
        {
            wrong.Add("the per-session route refused the rate");
        }
        if ((await scene.StoredAsync()).Rate != RefreshRate.Relaxed)
            wrong.Add("the per-session route did not store the rate");
        Assert.True(wrong.Count == 0, "an unlocked user-role account: " + string.Join("; ", wrong));
    }

    private async Task<Scene> SceneAsync(bool? ownLock, bool everyGuestLock)
    {
        Directory.CreateDirectory(_root);
        var database = await TestDatabase.CreateAsync();
        var account = AccountResetTests.Session(null, "live");
        account.SessionType = SessionType.User;
        account.Preferences = new UserPreferences
        {
            RefreshRate = RefreshRate.Slow,
            RefreshRateLocked = ownLock,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserSessions.Add(account);
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
        var http = new DefaultHttpContext { RequestServices = app, Items = { ["Session"] = account } };
        var preferences = ActivatorUtilities.CreateInstance<UserPreferencesController>(app);
        preferences.ControllerContext = new ControllerContext { HttpContext = http };
        var sessions = ActivatorUtilities.CreateInstance<SessionsController>(app);
        sessions.ControllerContext = new ControllerContext { HttpContext = http };
        return new Scene(database, app, account, preferences, sessions);
    }

    private sealed record Scene(
        TestDatabase Database,
        ServiceProvider App,
        UserSession Account,
        UserPreferencesController Preferences,
        SessionsController Sessions) : IAsyncDisposable
    {
        public async Task<(RefreshRate? Rate, bool? Locked, bool SharpCorners)> StoredAsync()
        {
            await using var read = await Database.Factory.CreateDbContextAsync();
            var row = await read.UserPreferences.AsNoTracking().SingleAsync(preferences => preferences.SessionId == Account.Id);
            return (row.RefreshRate, row.RefreshRateLocked, row.SharpCorners);
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
