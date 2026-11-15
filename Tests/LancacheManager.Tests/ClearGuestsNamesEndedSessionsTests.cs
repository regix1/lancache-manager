using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
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
/// Clear guests tells every window which guest sessions it ended, and each window signs out only when
/// its own session is named (<c>Web/src/contexts/AuthContext.tsx:304-308</c>). A guest who signs in while
/// the clear runs holds a session the clear never read, so its window stays signed in; a guest who signed
/// in before the clear read its list is ended with the rest. Both orders are run: the new guest's sign-in
/// lands before the clear reads the guest list, or after it, while the clear is revoking the old guest.
/// </summary>
public sealed class ClearGuestsNamesEndedSessionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-clear-guests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearGuests_NamesExactlyTheSessionsItEndedAsync(bool signsInWhileTheClearRuns)
    {
        Directory.CreateDirectory(_root);
        await using var database = await TestDatabase.CreateAsync();
        var oldGuest = AccountResetTests.Session(null, "live");
        var newGuest = AccountResetTests.Session(null, "live");
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserSessions.Add(oldGuest);
            if (!signsInWhileTheClearRuns)
                seed.UserSessions.Add(newGuest);
            await seed.SaveChangesAsync();
        }

        // The first guest the clear revokes reports itself gone; that is after the clear read its list.
        var activity = DispatchProxy.Create<IActivityRegistry, SignInDuringRevokeProxy>();
        if (signsInWhileTheClearRuns)
        {
            ((SignInDuringRevokeProxy)(object)activity).OnFirstEnd = async () =>
            {
                await using var signIn = await database.Factory.CreateDbContextAsync();
                signIn.UserSessions.Add(newGuest);
                await signIn.SaveChangesAsync();
            };
        }
        var configuration = new ConfigurationBuilder().Build();
        var state = StateTestMethods.CreateStateService(_root);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var sessions = new SessionService(
            database.Factory,
            apiKeyService: null!,
            NullLogger<SessionService>.Instance,
            state,
            notifications,
            configuration,
            activityRegistry: activity);
        await using var app = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(sessions)
            .AddSingleton(notifications)
            .AddSingleton(state)
            .AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory)
            .BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<SessionsController>(app);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = app,
                Items = { ["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin } }
            }
        };

        var cleared = await controller.ClearGuestsAsync();
        Assert.True(cleared.Result is OkObjectResult, $"Clear guests was answered {(cleared.Result as ObjectResult)?.StatusCode}");

        var order = signsInWhileTheClearRuns
            ? "the new guest signed in while the clear was revoking the old guest"
            : "the new guest signed in before the clear read the guest list";
        var expected = signsInWhileTheClearRuns ? new[] { oldGuest.Id } : new[] { oldGuest.Id, newGuest.Id };
        var events = ((RecordingNotificationProxy)(object)notifications).Invocations
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
                && call.Args.Length > 1
                && Equals(call.Args[0], SignalREvents.UserSessionsCleared))
            .Select(call => JsonSerializer.SerializeToElement(call.Args[1], JsonSerializerOptions.Web))
            .ToList();
        var sent = Assert.Single(events);
        Assert.True(
            sent.TryGetProperty("sessionIds", out var named),
            $"{order}: UserSessionsCleared went out as {sent.GetRawText()}, with no session ids, so every guest window signs out, including the new guest's on its live session");
        var namedIds = named.EnumerateArray().Select(id => Guid.Parse(id.GetString()!)).Order().ToArray();
        Assert.True(
            namedIds.SequenceEqual(expected.Order()),
            $"{order}: UserSessionsCleared named {string.Join(", ", namedIds)}, the clear ended {string.Join(", ", expected)}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Runs <see cref="OnFirstEnd"/> when the first session reports itself gone, then answers as usual.</summary>
    internal class SignInDuringRevokeProxy : NullReturningProxy
    {
        public Func<Task>? OnFirstEnd { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IActivityRegistry.ReportAsync)
                && args is [_, _, _, false, ..]
                && OnFirstEnd is { } hook)
            {
                OnFirstEnd = null;
                return hook();
            }
            return base.Invoke(targetMethod, args);
        }
    }
}
