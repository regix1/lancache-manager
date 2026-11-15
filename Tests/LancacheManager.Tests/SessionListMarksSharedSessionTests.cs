using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
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
/// The Users page hides Log Out, Revoke and Delete session on the one session every browser runs on
/// while sign-in is off, and it knows that row only by the list's <c>IsSharedSession</c>. So the list
/// marks that row, and only while sign-in is off; an account session on the same list is never marked.
/// </summary>
// Shares the process-wide shared session (SessionService.cs:44) with SessionAuthenticationHandlerTests.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SessionListMarksSharedSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-shared-mark-{Guid.NewGuid():N}");

    [Fact]
    public async Task TheSessionList_MarksTheSharedSessionOnlyWhileSignInIsOffAsync()
    {
        ResetSharedSession();
        Directory.CreateDirectory(_root);
        await using var database = await TestDatabase.CreateAsync();
        var configuration = new ConfigurationBuilder().Build();
        var state = StateTestMethods.CreateStateService(Path.Combine(_root, "state"));
        SetMode(state, AccountMode.Unauthenticated);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var sessions = new SessionService(
            database.Factory, apiKeyService: null!, NullLogger<SessionService>.Instance, state, notifications, configuration);
        var owner = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "owner",
            Role = SessionType.Admin,
            IsMainAdmin = true,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "seeded"
        };
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserAccounts.Add(owner);
            await seed.SaveChangesAsync();
        }
        var shared = (await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value.Session;
        var (_, ownerSession) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);
        await using var app = new ServiceCollection()
            .AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory)
            .BuildServiceProvider();

        // The shared session's own browser reads the list with sign-in off.
        var signInOff = await ListAsync(sessions, notifications, app, shared);
        var sharedRow = signInOff.Single(row => row.Id == shared.Id);
        var ownerRow = signInOff.Single(row => row.Id == ownerSession.Id);

        SetMode(state, AccountMode.Password);
        var signInOn = await ListAsync(sessions, notifications, app, ownerSession);

        var wrong = new List<string>();
        if (!sharedRow.IsSharedSession || !sharedRow.IsCurrentSession)
            wrong.Add($"with sign-in off the shared session lists itself with IsSharedSession {sharedRow.IsSharedSession} and IsCurrentSession {sharedRow.IsCurrentSession}, so its page draws Log Out, which keeps the session");
        if (ownerRow.IsSharedSession)
            wrong.Add("with sign-in off the owner's account session is marked shared, so its page hides the Log Out that ends it");
        if (signInOn.Any(row => row.IsSharedSession))
            wrong.Add("with sign-in on a row is still marked shared");
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    private static async Task<List<SessionRecord>> ListAsync(
        SessionService sessions,
        ISignalRNotificationService notifications,
        ServiceProvider app,
        UserSession caller)
    {
        var controller = new SessionsController(sessions, notifications, app.GetRequiredService<IServiceScopeFactory>(), stateService: null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = app, Items = { ["Session"] = caller } }
            }
        };
        var list = await controller.GetAllAsync();
        var listed = Assert.IsType<SessionListResponse>(Assert.IsAssignableFrom<ObjectResult>(list.Result).Value);
        return listed.Sessions;
    }

    private static void SetMode(StateService state, AccountMode mode) =>
        state.UpdateState(current =>
        {
            current.Access.Mode = mode;
            current.Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });

    /// <summary>The shared session is process-wide, so each run starts from a fresh process's state.</summary>
    private static void ResetSharedSession()
    {
        StaticField("_authDisabledAdminSession").SetValue(null, null);
        StaticField("_authDisabledRetryAfterUtc").SetValue(null, DateTime.MinValue);
    }

    private static FieldInfo StaticField(string name) =>
        typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SessionService no longer has a static {name}.");

    public void Dispose()
    {
        ResetSharedSession();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
