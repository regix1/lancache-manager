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

namespace LancacheManager.Tests;

/// <summary>
/// An administrator who locks a guest's refresh rate decides how often that guest's page polls. The
/// guest page writes its rate through <c>PATCH /api/user-preferences/refreshRate</c>
/// (<c>setRefreshRate</c> in <c>Web/src/contexts/RefreshRateContext.tsx</c>), so that route keeps the locked rate too,
/// whether the lock is set on the guest's own session or on every guest.
/// </summary>
public sealed class GuestRefreshRateLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-guest-rate-lock-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("the guest's own session")]
    [InlineData("every guest")]
    public async Task AGuestsPatch_LeavesTheRateTheAdministratorLockedAsync(string lockedFor)
    {
        Directory.CreateDirectory(_root);
        await using var database = await TestDatabase.CreateAsync();
        var perSession = lockedFor == "the guest's own session";
        var guest = AccountResetTests.Session(null, "live");
        guest.Preferences = new UserPreferences
        {
            RefreshRate = RefreshRate.Slow,
            RefreshRateLocked = perSession ? true : null,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserSessions.Add(guest);
            await seed.SaveChangesAsync();
        }
        var state = StateTestMethods.CreateStateService(_root);
        state.SetGuestRefreshRateLocked(!perSession);

        var configuration = new ConfigurationBuilder().Build();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        await using var app = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(state)
            .AddSingleton(notifications)
            .AddSingleton(new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, database.Factory))
            .AddSingleton(new SessionService(database.Factory, apiKeyService: null!, NullLogger<SessionService>.Instance, state, notifications, configuration))
            .AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory)
            .BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<UserPreferencesController>(app);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = app, Items = { ["Session"] = guest } }
        };

        // The guest picks the fastest rate on its own page.
        string answer;
        try
        {
            var patched = await controller.UpdatePreferenceAsync("refreshRate", JsonSerializer.SerializeToElement("LIVE"));
            answer = $"answered {(patched as ObjectResult)?.StatusCode ?? (patched as StatusCodeResult)?.StatusCode}";
        }
        catch (ForbiddenException refused)
        {
            answer = $"refused ({refused.Message})";
        }

        await using var read = await database.Factory.CreateDbContextAsync();
        var stored = await read.UserPreferences.Where(preferences => preferences.SessionId == guest.Id).Select(preferences => preferences.RefreshRate).SingleAsync();
        Assert.True(
            stored == RefreshRate.Slow,
            $"with the refresh rate locked for {lockedFor}, the guest's PATCH /api/user-preferences/refreshRate was {answer} and stored {stored}, so the guest's page polls at a rate the administrator locked away");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
