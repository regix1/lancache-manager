using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The three rules the account-management endpoints exist to enforce: a user reaches no
/// administrator account, the account that owns the installation cannot be taken away from whoever
/// holds it, and only that account hands out the administrator role.
///
/// Every caller below is built by seeding an account row with the role under test and a session that
/// names it, rather than by taking an administrator's session and flipping the session row: the
/// checks read the account row, so a caller whose row says something other than its session does
/// would prove nothing about either. [63][64][65][66][66b][66c][66d][29b]
/// </summary>
public sealed class AccountManagementTests : IDisposable
{
    private const string SeedPassword = "Seeded-Walrus-7";
    private const string NewPassword = "Chosen-Badger-3";

    private readonly string _root;

    public AccountManagementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-account-management-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// The list a user is answered carries the accounts that are not administrators and nothing else.
    /// [63]
    /// </summary>
    [Fact]
    public async Task AUserIsAnsweredTheAccountListWithoutTheAdministrators()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        var caller = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        await SeedAccountAsync(database.Factory, "other-reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var listed = AccountsOf(await controller.GetAccountsAsync());

        Assert.Equal(
            new[] { "other-reader", "reader" },
            listed.Select(a => a.Username).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// An administrator is answered everybody, the other administrators and the account that owns the
    /// installation included. [66]
    /// </summary>
    [Fact]
    public async Task AnAdministratorIsAnsweredEveryAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var listed = AccountsOf(await controller.GetAccountsAsync());

        Assert.Equal(
            new[] { "owner", "reader", "second-admin" },
            listed.Select(a => a.Username).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.True(listed.Single(a => a.Username == "owner").IsMainAdmin);
    }

    /// <summary>
    /// Every verb, not only the list. An account a user is not shown is also an account a user cannot
    /// name: leaving read, edit, disable, delete and set-role answering an id the list withholds would
    /// make the withholding a display choice rather than a permission. [64]
    /// </summary>
    [Fact]
    public async Task AUserCannotReachAnAdministratorAccountByItsId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        var caller = await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var id = administrator.Id;

        var refusals = new (string Verb, ActionResult? Result)[]
        {
            ("get", (await controller.GetAccountAsync(id)).Result),
            ("edit", (await controller.EditAccountAsync(
                id, new EditAccountRequest { Username = "taken-over", Password = NewPassword })).Result),
            ("disable", (await controller.SetDisabledAsync(
                id, new SetAccountDisabledRequest { Disabled = true })).Result),
            ("delete", (await controller.DeleteAccountAsync(id)).Result),
            ("set-role", (await controller.SetRoleAsync(
                id, new SetAccountRoleRequest { Role = SessionType.User })).Result)
        };

        foreach (var (verb, result) in refusals)
        {
            var status = StatusOf(result);
            Assert.True(
                status == StatusCodes.Status404NotFound,
                $"{verb} answered a user {status} for an administrator account, not 404.");
            Assert.Equal(AccountRefusalResponse.AccountNotFound, StageKeyOf(result));
        }

        var stored = await ReadAccountAsync(database, administrator.Id);
        Assert.Equal("second-admin", stored.Username);
        Assert.Equal(SessionType.Admin, stored.Role);
        Assert.False(stored.IsDisabled);
    }

    /// <summary>
    /// Nine attempts on the account that owns the installation: delete, disable and demote, tried by a
    /// user, by another administrator and by that account itself. All nine are refused, and the row is
    /// exactly as it was afterwards.
    ///
    /// Closing delete on its own is worth nothing while demoting is open, and closing both is worth
    /// nothing while the account can be disabled, so the three are one rule. [65]
    /// </summary>
    [Fact]
    public async Task TheMainAdministratorCannotBeDeletedDisabledOrDemotedByAnybody()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        foreach (var caller in new[] { reader, administrator, owner })
        {
            var controller = await NewControllerAsync(database.Factory, caller);

            // A user is answered 404 because the account is not one it may see at all, and an
            // administrator is answered 403 because it may see the account and still may not touch it.
            // Both are refusals; which one arrives is the visibility rule, not a second decision.
            var expected = caller.Role == SessionType.User
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status403Forbidden;

            var attempts = new (string Verb, int Status)[]
            {
                ("delete", StatusOf(await controller.DeleteAccountAsync(owner.Id))),
                ("disable", StatusOf(await controller.SetDisabledAsync(owner.Id, new SetAccountDisabledRequest { Disabled = true }))),
                ("demote", StatusOf(await controller.SetRoleAsync(owner.Id, new SetAccountRoleRequest { Role = SessionType.User })))
            };

            foreach (var (verb, status) in attempts)
            {
                Assert.True(
                    status == expected,
                    $"{caller.Username} was answered {status} when it tried to {verb} the main administrator, not {expected}.");
            }
        }

        var stored = await ReadAccountAsync(database, owner.Id);
        Assert.True(stored.IsMainAdmin);
        Assert.Equal(SessionType.Admin, stored.Role);
        Assert.False(stored.IsDisabled);
    }

    /// <summary>
    /// Nine attempts at the administrator role: creating an account with it, moving somebody else onto
    /// it, and moving yourself onto it, tried by a user, by a non-main administrator and by the account
    /// that owns the installation. Only the owner's succeed.
    ///
    /// Promoting yourself runs through the same check as promoting anybody else, so it is attempted
    /// here rather than assumed: a rule that reads the caller's row needs no exception for the caller
    /// being the target, and writing one is how self-promotion gets left open. [66b]
    ///
    /// The owner's own promotion is the one attempt of the nine that is refused, because the account
    /// that owns the installation is refused every set-role, which is what stops it being demoted.
    /// It already holds the role it is asking for. [65]
    /// </summary>
    [Fact]
    public async Task OnlyTheMainAdministratorHandsOutTheAdministratorRole()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        var promoted = await SeedAccountAsync(database.Factory, "promoted", SessionType.User);

        foreach (var caller in new[] { reader, administrator })
        {
            var controller = await NewControllerAsync(database.Factory, caller);

            var created = await controller.CreateAccountAsync(NewAccountRequest($"minted-by-{caller.Username}", SessionType.Admin));
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(created));
            Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(created));

            var promotion = await controller.SetRoleAsync(promoted.Id, new SetAccountRoleRequest { Role = SessionType.Admin });
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(promotion));
            Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(promotion));

            var self = await controller.SetRoleAsync(caller.Id, new SetAccountRoleRequest { Role = SessionType.Admin });
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(self));
            Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(self));
        }

        await using var unchanged = database.Factory.CreateDbContext();
        Assert.Equal(SessionType.User, (await unchanged.UserAccounts.SingleAsync(a => a.Id == promoted.Id)).Role);
        Assert.False(await unchanged.UserAccounts.AnyAsync(a => a.Username.StartsWith("minted-by-")));

        var ownerController = await NewControllerAsync(database.Factory, owner);

        var mintedByOwner = await ownerController.CreateAccountAsync(NewAccountRequest("minted-by-owner", SessionType.Admin));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(mintedByOwner));
        Assert.Equal(SessionType.Admin, AccountOf(mintedByOwner).Role);

        var promotedByOwner = await ownerController.SetRoleAsync(promoted.Id, new SetAccountRoleRequest { Role = SessionType.Admin });
        Assert.Equal(StatusCodes.Status200OK, StatusOf(promotedByOwner));
        Assert.Equal(SessionType.Admin, AccountOf(promotedByOwner).Role);

        var ownerPromotingItself = await ownerController.SetRoleAsync(owner.Id, new SetAccountRoleRequest { Role = SessionType.Admin });
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(ownerPromotingItself));
        Assert.Equal(AccountRefusalResponse.MainAdminProtected, StageKeyOf(ownerPromotingItself));
    }

    /// <summary>
    /// The caller below carries an administrator session - the same session type and the same claim
    /// the account that owns the installation carries - and is refused, because the check reads the
    /// stored row and that row is not the main administrator's. A check written against the claim
    /// would admit every administrator and the rule would mean nothing. [66c]
    /// </summary>
    [Fact]
    public async Task TheAdministratorRoleCheckReadsTheCallersAccountRowRatherThanItsClaim()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);

        var session = await SeedSessionAsync(database.Factory, caller);
        Assert.Equal(SessionType.Admin, session.SessionType);

        var controller = NewController(database.Factory, session);
        var created = await controller.CreateAccountAsync(NewAccountRequest("minted-by-claim", SessionType.Admin));

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(created));
        Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(created));
    }

    /// <summary>
    /// With authentication turned off there is no access control for this rule to contradict, so the
    /// shared session every anonymous caller runs on creates administrators. It has no account row,
    /// which is the state this whole file has to keep working for, and the account that owns the
    /// installation is still protected from it. [66d][27d]
    /// </summary>
    [Fact]
    public async Task WithAuthenticationDisabledACallerWithNoAccountCreatesAdministrators()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, account: null);

        var controller = NewController(database.Factory, session, authenticationEnabled: false);

        var created = await controller.CreateAccountAsync(NewAccountRequest("minted-with-auth-off", SessionType.Admin));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
        Assert.Equal(SessionType.Admin, AccountOf(created).Role);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.DeleteAccountAsync(owner.Id)));
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            StatusOf(await controller.SetDisabledAsync(owner.Id, new SetAccountDisabledRequest { Disabled = true })));
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            StatusOf(await controller.SetRoleAsync(owner.Id, new SetAccountRoleRequest { Role = SessionType.User })));

        // The caller proved nothing but the configuration: it has no account, so the actor half of the
        // row is empty rather than the write throwing. [29c]
        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();
        Assert.Equal(IdentityAuditEvent.AccountCreated, entry.Event);
        Assert.Null(entry.PerformedByAccountId);
        Assert.Equal(session.Id, entry.PerformedBySessionId);
    }

    /// <summary>
    /// With authentication on, a caller holding only the API key still has no account row, and the
    /// role check answers that state from the setting rather than from a missing row: it manages
    /// accounts, and the way it reaches a first administrator is the create-first-admin endpoint
    /// rather than a standing right to mint more here. [66d]
    /// </summary>
    [Fact]
    public async Task WithAuthenticationEnabledACallerWithNoAccountCannotCreateAnAdministrator()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, account: null);

        var controller = NewController(database.Factory, session);

        var administrator = await controller.CreateAccountAsync(NewAccountRequest("minted-with-a-key", SessionType.Admin));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(administrator));
        Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(administrator));

        var reader = await controller.CreateAccountAsync(NewAccountRequest("reader-with-a-key", SessionType.User));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(reader));
        Assert.Equal(SessionType.User, AccountOf(reader).Role);
    }

    /// <summary>
    /// The five events this controller produces, which are the five criterion 29b was still waiting
    /// on. Each one names the account it was done to and the caller that did it. [29b]
    /// </summary>
    [Fact]
    public async Task TheFiveAccountEventsAreRecordedAgainstTheCallerAndTheTarget()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, owner);
        var controller = NewController(database.Factory, session);
        var before = DateTime.UtcNow;

        var created = await controller.CreateAccountAsync(NewAccountRequest("audited", SessionType.User));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
        var target = AccountOf(created).Id;

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetRoleAsync(target, new SetAccountRoleRequest { Role = SessionType.Admin })));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(target, new SetAccountDisabledRequest { Disabled = true })));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(target, new SetAccountDisabledRequest { Disabled = false })));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.DeleteAccountAsync(target)));

        await using var context = database.Factory.CreateDbContext();
        var entries = await context.IdentityAuditEntries.OrderBy(e => e.Id).ToListAsync();

        Assert.Equal(
            new[]
            {
                IdentityAuditEvent.AccountCreated,
                IdentityAuditEvent.RoleChanged,
                IdentityAuditEvent.AccountDisabled,
                IdentityAuditEvent.AccountEnabled,
                IdentityAuditEvent.AccountDeleted
            },
            entries.Select(e => e.Event).ToArray());

        foreach (var entry in entries)
        {
            Assert.Equal(owner.Id, entry.PerformedByAccountId);
            Assert.Equal(session.Id, entry.PerformedBySessionId);
            Assert.Equal(target, entry.TargetAccountId);
            Assert.InRange(entry.PerformedAtUtc, before, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// An audit trail that can undo the change it is recording turns a logging fault into an account
    /// that half exists. The account is created whether or not the row lands. [29d]
    /// </summary>
    [Fact]
    public async Task AFailedAuditWriteStillLeavesTheAccountCreated()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, owner);

        var controller = NewController(
            database.Factory,
            session,
            auditService: new IdentityAuditService(new ThrowingDbContextFactory(), NullLogger<IdentityAuditService>.Instance));

        var created = await controller.CreateAccountAsync(NewAccountRequest("audit-is-broken", SessionType.User));

        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.UserAccounts.AnyAsync(a => a.Username == "audit-is-broken"));
        Assert.Empty(context.IdentityAuditEntries);
    }

    /// <summary>
    /// A session carries its own copy of the role and does not expire while it belongs to an account
    /// holder, so a change that leaves the sessions alone leaves the person with what they had until
    /// they sign out. Changing the role, disabling the account and deleting it all end them. [28]
    /// </summary>
    [Fact]
    public async Task ChangingTheRoleDisablingAndDeletingAllEndTheAccountsSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var callerSession = await SeedSessionAsync(database.Factory, owner);
        var controller = NewController(database.Factory, callerSession);

        var demoted = await SeedAccountAsync(database.Factory, "demoted", SessionType.Admin);
        var disabled = await SeedAccountAsync(database.Factory, "disabled", SessionType.User);
        var deleted = await SeedAccountAsync(database.Factory, "deleted", SessionType.User);

        var demotedSession = await SeedSessionAsync(database.Factory, demoted);
        var disabledSession = await SeedSessionAsync(database.Factory, disabled);
        var deletedSession = await SeedSessionAsync(database.Factory, deleted);

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetRoleAsync(demoted.Id, new SetAccountRoleRequest { Role = SessionType.User })));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(disabled.Id, new SetAccountDisabledRequest { Disabled = true })));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.DeleteAccountAsync(deleted.Id)));

        await using var context = database.Factory.CreateDbContext();
        foreach (var sessionId in new[] { demotedSession.Id, disabledSession.Id, deletedSession.Id })
        {
            Assert.True(
                (await context.UserSessions.SingleAsync(s => s.Id == sessionId)).IsRevoked,
                $"Session {sessionId} outlived the change to the account it belongs to.");
        }

        // The caller's own session is untouched: none of the three changes was about the caller.
        Assert.False((await context.UserSessions.SingleAsync(s => s.Id == callerSession.Id)).IsRevoked);
    }

    /// <summary>
    /// The rules an account's credentials pass at setup are the rules they pass here, so a password
    /// that could not have been chosen for the first account cannot be arrived at by creating a
    /// second one. Renaming without sending a password does not have to restate one. [26]
    /// </summary>
    [Fact]
    public async Task CreatingAndEditingAnAccountRunTheStoredCredentialRules()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var controller = await NewControllerAsync(database.Factory, owner);

        var weak = await controller.CreateAccountAsync(
            new CreateAccountRequest { Username = "weak", Password = "short", Role = SessionType.User });
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(weak));
        Assert.Equal(AccountRefusalResponse.CredentialsRejected, StageKeyOf(weak));

        var created = await controller.CreateAccountAsync(NewAccountRequest("renamed-later", SessionType.User));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
        var target = AccountOf(created).Id;

        var renamed = await controller.EditAccountAsync(target, new EditAccountRequest { Username = "renamed" });
        Assert.Equal(StatusCodes.Status200OK, StatusOf(renamed));
        Assert.Equal("renamed", AccountOf(renamed).Username);

        var taken = await controller.EditAccountAsync(target, new EditAccountRequest { Username = "owner" });
        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(taken));
        Assert.Equal(AccountRefusalResponse.UsernameTaken, StageKeyOf(taken));

        var stored = await ReadAccountAsync(database, target);
        Assert.Equal("renamed", stored.Username);
        Assert.Equal(owner.Id, (await ReadAccountAsync(database, owner.Id)).Id);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<AccountsController> NewControllerAsync(TestDbContextFactory factory, UserAccount caller)
    {
        return NewController(factory, await SeedSessionAsync(factory, caller));
    }

    private AccountsController NewController(
        TestDbContextFactory factory,
        UserSession caller,
        bool authenticationEnabled = true,
        IdentityAuditService? auditService = null)
    {
        var configuration = NewConfiguration(authenticationEnabled);
        var controller = new AccountsController(
            factory,
            new PasswordHasher<UserAccount>(),
            new SessionService(
                factory,
                new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!),
                NullLogger<SessionService>.Instance,
                stateService: null!,
                signalR: null!,
                configuration),
            auditService ?? new IdentityAuditService(factory, NullLogger<IdentityAuditService>.Instance),
            NullLogger<AccountsController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private IConfiguration NewConfiguration(bool authenticationEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, $"api_key-{authenticationEnabled}.txt"),
                ["Security:EnableAuthentication"] = authenticationEnabled.ToString()
            })
            .Build();

    private static CreateAccountRequest NewAccountRequest(string username, SessionType role) =>
        new() { Username = username, Password = NewPassword, Role = role };

    private static async Task<UserAccount> SeedAccountAsync(
        TestDbContextFactory factory,
        string username,
        SessionType role,
        bool mainAdmin = false)
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            Role = role,
            IsMainAdmin = mainAdmin,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(account, SeedPassword);

        await using var context = factory.CreateDbContext();
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    /// <summary>
    /// A stored session for an account, or the shape both account-less callers have - a request
    /// carrying only the API key, and the shared session used while authentication is disabled - when
    /// <paramref name="account"/> is null.
    /// </summary>
    private static async Task<UserSession> SeedSessionAsync(TestDbContextFactory factory, UserAccount? account)
    {
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = Guid.NewGuid().ToString("N"),
            SessionType = account?.Role ?? SessionType.Admin,
            AccountId = account?.Id,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1)
        };

        await using var context = factory.CreateDbContext();
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private static async Task<UserAccount> ReadAccountAsync(TestDatabase database, Guid id)
    {
        await using var context = database.Factory.CreateDbContext();
        return await context.UserAccounts.SingleAsync(a => a.Id == id);
    }

    private static int StatusOf(ActionResult? result) =>
        Assert.IsAssignableFrom<ObjectResult>(result).StatusCode ?? 0;

    private static string StageKeyOf(ActionResult? result) =>
        Assert.IsType<AccountRefusalResponse>(Assert.IsAssignableFrom<ObjectResult>(result).Value).StageKey;

    private static int StatusOf<T>(ActionResult<T> result) => StatusOf(result.Result);

    private static string StageKeyOf<T>(ActionResult<T> result) => StageKeyOf(result.Result);

    private static AccountResponse AccountOf(ActionResult<AccountResponse> result) =>
        Assert.IsType<AccountResponse>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);

    private static List<AccountResponse> AccountsOf(ActionResult<List<AccountResponse>> result) =>
        Assert.IsType<List<AccountResponse>>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value);
}
