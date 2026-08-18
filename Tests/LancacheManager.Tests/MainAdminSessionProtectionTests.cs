using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// A prefill session row carries the auth session that started it, which is the id the session
    /// list withholds, so the owner's rows are withheld on the same rule and the two by-id routes
    /// answer as if the session were not there.
    /// </summary>
    /// <remarks>
    /// The list is checked through the service rather than the controller, whose five daemon
    /// services are concrete types with no seam. The two by-id routes return before reaching them.
    /// </remarks>
    [Fact]
    public async Task AUserAndASecondAdministratorAreAnsweredWithoutTheOwnersPrefillSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, role: SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, role: SessionType.User);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        var adminSession = await SeedSessionAsync(database.Factory, administrator);
        var readerSession = await SeedSessionAsync(database.Factory, reader);
        var guestSession = await SeedSessionAsync(database.Factory, account: null, sessionType: SessionType.Guest);

        await SeedPrefillSessionAsync(database.Factory, ownerSession, "ownerdaemon00001");
        await SeedPrefillSessionAsync(database.Factory, adminSession, "admindaemon00001");
        await SeedPrefillSessionAsync(database.Factory, guestSession, "guestdaemon00001");

        var prefillSessions = new PrefillSessionService(
            database.Factory, NullLogger<PrefillSessionService>.Instance);

        foreach (var caller in new[] { readerSession, adminSession })
        {
            var (listed, totalCount) = await prefillSessions.GetSessionsAsync(caller);
            var ids = listed.Select(s => s.SessionId).ToArray();

            Assert.DoesNotContain("ownerdaemon00001", ids);
            Assert.Contains("admindaemon00001", ids);
            Assert.Contains("guestdaemon00001", ids);
            Assert.Equal(2, totalCount);

            var controller = NewPrefillAdminController(database, caller);

            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await controller.GetSessionHistoryAsync("ownerdaemon00001")));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await controller.BanBySessionAsync("ownerdaemon00001", new BanRequest())));
        }

        var (ownerListed, ownerTotal) = await prefillSessions.GetSessionsAsync(ownerSession);

        Assert.Equal(3, ownerTotal);
        Assert.Contains("ownerdaemon00001", ownerListed.Select(s => s.SessionId));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await NewPrefillAdminController(database, ownerSession)
                .GetSessionHistoryAsync("ownerdaemon00001")));
    }

    /// <summary>
    /// The test above runs on SQLite, which settles the LINQ shape and nothing about the provider
    /// that ships. <c>ToQueryString()</c> compiles it through Npgsql without a connection:
    /// <see cref="PrefillSession.CreatedByAccountId"/> has to be compared in the SQL, and rows with
    /// no account have to be named explicitly, because PostgreSQL answers <c>NULL != id</c> with
    /// unknown and would drop every guest and API-key row.
    /// </summary>
    [Fact]
    public void ThePrefillVisibilityFilterTranslatesToSql()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=prefill_visibility_translation_smoke_test")
                .Options);

        var hiddenAccountId = Guid.NewGuid();

        var sql = context.PrefillSessions
            .AsNoTracking()
            .Where(s => s.CreatedByAccountId == null || s.CreatedByAccountId != hiddenAccountId)
            .ToQueryString();

        Assert.Contains("\"CreatedByAccountId\"", sql, StringComparison.Ordinal);
        Assert.Contains("IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains($"'{hiddenAccountId}'", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ban records the acting administrator's auth session id and the ban list answers every
    /// administrator, so the owner's id is withheld from it. The ban record itself stays.
    /// </summary>
    [Fact]
    public async Task ASecondAdministratorIsAnsweredWithoutTheOwnersSessionIdOnABan()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, role: SessionType.Admin);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        var adminSession = await SeedSessionAsync(database.Factory, administrator);

        await using (var context = database.Factory.CreateDbContext())
        {
            context.BannedPrefillUsers.Add(new BannedPrefillUser
            {
                Username = "prefilluser",
                BannedBySessionId = ownerSession.Id.ToString(),
                BannedBy = "admin",
                BannedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var forAdministrator = Assert.Single(await BansSeenByAsync(database, adminSession));
        var forOwner = Assert.Single(await BansSeenByAsync(database, ownerSession));

        Assert.Null(forAdministrator.BannedBySessionId);
        Assert.Equal("prefilluser", forAdministrator.Username);
        Assert.Equal(ownerSession.Id, forOwner.BannedBySessionId);
    }

    private static async Task<List<BannedPrefillUserDto>> BansSeenByAsync(
        TestDatabase database,
        UserSession caller)
    {
        var result = await NewPrefillAdminController(database, caller).GetBansAsync();

        return Assert.IsAssignableFrom<List<BannedPrefillUserDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    /// <summary>
    /// The owning account is recorded on the prefill row, so the filter does not depend on which
    /// session rows still exist. Resolving the owner through the session rows published every
    /// prefill row whose session the owner had deleted.
    /// </summary>
    [Fact]
    public async Task ThePrefillFilterHoldsAfterTheOwnersSessionRowIsDeleted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, role: SessionType.Admin);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        var adminSession = await SeedSessionAsync(database.Factory, administrator);
        await SeedPrefillSessionAsync(database.Factory, ownerSession, "ownerdaemon00002");

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserSessions.Remove(
                await context.UserSessions.FirstAsync(s => s.Id == ownerSession.Id));
            await context.SaveChangesAsync();
        }

        var prefillSessions = new PrefillSessionService(
            database.Factory, NullLogger<PrefillSessionService>.Instance);

        var (listed, totalCount) = await prefillSessions.GetSessionsAsync(adminSession);

        Assert.Equal(0, totalCount);
        Assert.DoesNotContain("ownerdaemon00002", listed.Select(s => s.SessionId));
        Assert.False(await prefillSessions.CallerMaySeeSessionAsync(adminSession, "ownerdaemon00002"));
        Assert.True(await prefillSessions.CallerMaySeeSessionAsync(ownerSession, "ownerdaemon00002"));
    }

    /// <summary>
    /// The guest prefill toggle writes the named session's row and reads it back, so ungated it
    /// both changes a row the caller cannot see and confirms the row is there.
    /// </summary>
    [Fact]
    public async Task AUserAndASecondAdministratorCannotToggleTheOwnersGuestPrefill()
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
            var toggled = await NewAuthController(database, caller)
                .ToggleGuestPrefillAsync(ownerSession.Id, new GuestPrefillToggleRequest { Enabled = true });

            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(toggled));
        }

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserSessions.FindAsync(ownerSession.Id);

        Assert.NotNull(stored);
        Assert.Null(stored.SteamPrefillExpiresAtUtc);
    }

    private static async Task<SessionListResponse> ListSessionsAsync(TestDatabase database, UserSession caller)
    {
        var result = await NewSessionsController(database, caller).GetAllAsync();
        return Assert.IsType<SessionListResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
    }

    private static SessionsController NewSessionsController(TestDatabase database, UserSession caller)
    {
        var controller = new SessionsController(
            NewSessionService(database),
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
            NewSessionService(database));

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static PrefillAdminController NewPrefillAdminController(TestDatabase database, UserSession caller)
    {
        var controller = new PrefillAdminController(
            new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance),
            steamDaemonService: null!,
            epicDaemonService: null!,
            battleNetDaemonService: null!,
            riotDaemonService: null!,
            xboxDaemonService: null!,
            cacheService: null!,
            NullLogger<PrefillAdminController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static AuthController NewAuthController(TestDatabase database, UserSession caller)
    {
        var controller = new AuthController(
            NewSessionService(database),
            NullLogger<AuthController>.Instance,
            database.Factory,
            stateService: null!,
            signalR: null!,
            apiKeyService: null!,
            passwordHasher: null!,
            accountLockout: null!,
            identityAuditService: null!);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static SessionService NewSessionService(TestDatabase database)
    {
        return new SessionService(
            database.Factory,
            apiKeyService: null!,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new ConfigurationBuilder().Build());
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

    private static async Task SeedPrefillSessionAsync(
        TestDbContextFactory factory,
        UserSession createdBy,
        string sessionId)
    {
        var now = DateTime.UtcNow;

        await using var context = factory.CreateDbContext();
        context.PrefillSessions.Add(new PrefillSession
        {
            SessionId = sessionId,
            CreatedBySessionId = createdBy.Id,
            CreatedByAccountId = createdBy.AccountId,
            Platform = PrefillPlatform.Steam,
            Status = PrefillSessionStatus.Active,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(1)
        });
        await context.SaveChangesAsync();
    }

    private static int StatusOf(ActionResult? result) =>
        Assert.IsAssignableFrom<ObjectResult>(result).StatusCode ?? 0;

    private static int StatusOf<T>(ActionResult<T> result) => StatusOf(result.Result);
}
