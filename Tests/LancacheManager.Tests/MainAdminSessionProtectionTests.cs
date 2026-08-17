using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using static LancacheManager.Core.Services.UserPreferencesService;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The owner's sessions are withheld from every other account holder the same way the owner's
/// account row is: a session the list does not show is also a session revoke, delete and
/// preference writes cannot name.
/// </summary>
public sealed class MainAdminSessionProtectionTests
{
    [Fact]
    public async Task AUserAndASecondAdministratorAreAnsweredWithoutTheOwnersSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, role: SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, role: SessionType.User);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        var adminSession = await SeedSessionAsync(database.Factory, administrator);
        var readerSession = await SeedSessionAsync(database.Factory, reader);
        var guestSession = await SeedSessionAsync(database.Factory, account: null, sessionType: SessionType.Guest);
        var revokedOwnerSession = await SeedSessionAsync(database.Factory, owner, revoked: true);

        foreach (var caller in new[] { readerSession, adminSession })
        {
            var listed = await ListSessionsAsync(database, caller);
            var ids = listed.Sessions.Select(s => s.Id).ToArray();
            var historyIds = listed.HistorySessions.Select(s => s.Id).ToArray();

            Assert.DoesNotContain(ownerSession.Id, ids);
            Assert.DoesNotContain(revokedOwnerSession.Id, historyIds);
            Assert.Contains(adminSession.Id, ids);
            Assert.Contains(readerSession.Id, ids);
            Assert.Contains(guestSession.Id, ids);
        }
    }

    [Fact]
    public async Task TheOwnerIsAnsweredEverySession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, role: SessionType.Admin);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        var adminSession = await SeedSessionAsync(database.Factory, administrator);
        var revokedOwnerSession = await SeedSessionAsync(database.Factory, owner, revoked: true);

        var listed = await ListSessionsAsync(database, ownerSession);

        Assert.Contains(listed.Sessions.Select(s => s.Id), id => id == ownerSession.Id);
        Assert.Contains(listed.Sessions.Select(s => s.Id), id => id == adminSession.Id);
        Assert.Contains(listed.HistorySessions.Select(s => s.Id), id => id == revokedOwnerSession.Id);
    }

    [Fact]
    public async Task AUserAndASecondAdministratorCannotRevokeOrDeleteTheOwnersSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, role: SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, role: SessionType.User);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        var adminSession = await SeedSessionAsync(database.Factory, administrator);
        var readerSession = await SeedSessionAsync(database.Factory, reader);

        foreach (var caller in new[] { readerSession, adminSession })
        {
            var controller = NewSessionsController(database, caller);

            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.RevokeAsync(ownerSession.Id)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.DeleteAsync(ownerSession.Id)));
        }

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserSessions.FindAsync(ownerSession.Id);
        Assert.NotNull(stored);
        Assert.False(stored.IsRevoked);
    }

    [Fact]
    public async Task AUserAndASecondAdministratorCannotReadOrWriteTheOwnersSessionPreferences()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, role: SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, role: SessionType.User);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        var adminSession = await SeedSessionAsync(database.Factory, administrator);
        var readerSession = await SeedSessionAsync(database.Factory, reader);

        foreach (var caller in new[] { readerSession, adminSession })
        {
            var controller = NewPreferencesController(database, caller);

            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await controller.GetForSessionAsync(ownerSession.Id)));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await controller.SaveForSessionAsync(ownerSession.Id, new UserPreferencesDto())));
        }
    }

    private static async Task<SessionListResponse> ListSessionsAsync(TestDatabase database, UserSession caller)
    {
        var result = await NewSessionsController(database, caller).GetAllAsync();
        return Assert.IsType<SessionListResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
    }

    private static SessionsController NewSessionsController(TestDatabase database, UserSession caller)
    {
        var configuration = new ConfigurationBuilder().Build();
        var controller = new SessionsController(
            new SessionService(
                database.Factory,
                apiKeyService: null!,
                NullLogger<SessionService>.Instance,
                stateService: null!,
                signalR: DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                configuration),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            scopeFactory: null!,
            stateService: null!);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static UserPreferencesController NewPreferencesController(TestDatabase database, UserSession caller)
    {
        var controller = new UserPreferencesController(
            NullLogger<UserPreferencesController>.Instance,
            new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, database.Factory),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            database.Factory);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static async Task<UserAccount> SeedAccountAsync(
        TestDbContextFactory factory,
        SessionType role = SessionType.Admin,
        bool mainAdmin = false)
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = mainAdmin ? "owner" : Guid.NewGuid().ToString("N")[..12],
            Role = role,
            IsMainAdmin = mainAdmin,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "seeded"
        };

        await using var context = factory.CreateDbContext();
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private static async Task<UserSession> SeedSessionAsync(
        TestDbContextFactory factory,
        UserAccount? account,
        SessionType? sessionType = null,
        bool revoked = false)
    {
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = Guid.NewGuid().ToString("N"),
            SessionType = sessionType ?? account?.Role ?? SessionType.Admin,
            AccountId = account?.Id,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
            IsRevoked = revoked,
            RevokedAtUtc = revoked ? now : null
        };

        await using var context = factory.CreateDbContext();
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private static int StatusOf(ActionResult? result) =>
        Assert.IsAssignableFrom<ObjectResult>(result).StatusCode ?? 0;

    private static int StatusOf<T>(ActionResult<T> result) => StatusOf(result.Result);
}
