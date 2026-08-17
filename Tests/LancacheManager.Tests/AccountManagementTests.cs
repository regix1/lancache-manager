using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
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
/// administrator account, the owner is hidden from every other account holder and cannot be taken
/// away from whoever holds it, and only that account hands out the administrator role.
///
/// Every caller below is built by seeding an account row with the role under test and a session that
/// names it, rather than by taking an administrator's session and flipping the session row: the
/// checks read the account row, so a caller whose row says something other than its session does
/// would prove nothing about either.
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
    /// An administrator who is not the owner is answered every account except the owner. Seeing that
    /// row is what used to let them name it on edit, disable, delete and the session list.
    /// </summary>
    [Fact]
    public async Task AnAdministratorIsAnsweredEveryAccountExceptTheOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var listed = AccountsOf(await controller.GetAccountsAsync());

        Assert.Equal(
            new[] { "reader", "second-admin" },
            listed.Select(a => a.Username).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(listed, a => a.IsMainAdmin);
    }

    /// <summary>
    /// The owner is answered everybody, themselves included.
    /// </summary>
    [Fact]
    public async Task TheOwnerIsAnsweredEveryAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var caller = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var listed = AccountsOf(await controller.GetAccountsAsync());

        Assert.Equal(
            new[] { "owner", "reader", "second-admin" },
            listed.Select(a => a.Username).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.True(listed.Single(a => a.Username == "owner").IsMainAdmin);
    }

    /// <summary>
    /// Every verb, not only the list. An account a second administrator is not shown is also an
    /// account they cannot name.
    /// </summary>
    [Fact]
    public async Task ASecondAdministratorCannotReachTheOwnerByItsId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);

        var controller = await NewControllerAsync(database.Factory, caller);
        var id = owner.Id;

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
                $"{verb} answered a second administrator {status} for the owner, not 404.");
            Assert.Equal(AccountRefusalResponse.AccountNotFound, StageKeyOf(result));
        }

        var stored = await ReadAccountAsync(database, owner.Id);
        Assert.Equal("owner", stored.Username);
        Assert.True(stored.IsMainAdmin);
        Assert.False(stored.IsDisabled);
    }

    /// <summary>
    /// Every verb, not only the list. An account a user is not shown is also an account a user cannot
    /// name: leaving read, edit, disable, delete and set-role answering an id the list withholds would
    /// make the withholding a display choice rather than a permission.
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
    /// Twelve attempts on the account that owns the installation: edit, delete, disable and demote,
    /// tried by a user, by another administrator and by that account itself. All twelve are refused,
    /// and the row is exactly as it was afterwards.
    ///
    /// A caller who is not the owner is answered 404 because the account is not one they may see at
    /// all. The owner is answered 403 because they may see the row and still may not touch it.
    /// Closing delete on its own is worth nothing while demoting or editing the password is open,
    /// and closing those is worth nothing while the account can be disabled, so the four are one rule.
    /// </summary>
    [Fact]
    public async Task TheMainAdministratorCannotBeEditedDeletedDisabledOrDemotedByAnybody()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        foreach (var caller in new[] { reader, administrator, owner })
        {
            var controller = await NewControllerAsync(database.Factory, caller);

            var expected = caller.IsMainAdmin
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status404NotFound;

            var attempts = new (string Verb, int Status)[]
            {
                ("edit", StatusOf(await controller.EditAccountAsync(
                    owner.Id, new EditAccountRequest { Username = "taken-over", Password = NewPassword }))),
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
        Assert.Equal("owner", stored.Username);
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
    /// being the target, and writing one is how self-promotion gets left open.
    ///
    /// The owner's own promotion is the one attempt of the nine that is refused, because the account
    /// that owns the installation is refused every set-role, which is what stops it being demoted.
    /// It already holds the role it is asking for.
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
    /// would admit every administrator and the rule would mean nothing.
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
    /// installation is still protected from it.
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
        // row is empty rather than the write throwing.
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
    /// rather than a standing right to mint more here.
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
    /// The five events this controller produces. Each one names the account it was done to and the
    /// caller that did it.
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
    /// Delete, disable and set-role, tried by an administrator on its own account. All three end the
    /// caller's own sessions and none of them can be put back by the person who did it: re-creating
    /// an account and granting the admin role both belong to the account that owns the installation.
    ///
    /// The same three verbs on somebody else's row are attempted afterwards, because a guard that
    /// refused everybody would pass the first half of this test on its own.
    /// </summary>
    [Fact]
    public async Task AnAccountCannotBeDeletedDisabledOrDemotedByItself()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        var controller = await NewControllerAsync(database.Factory, caller);

        var refusals = new (string Verb, ActionResult? Result)[]
        {
            ("delete", (await controller.DeleteAccountAsync(caller.Id)).Result),
            ("disable", (await controller.SetDisabledAsync(
                caller.Id, new SetAccountDisabledRequest { Disabled = true })).Result),
            ("demote", (await controller.SetRoleAsync(
                caller.Id, new SetAccountRoleRequest { Role = SessionType.User })).Result)
        };

        foreach (var (verb, result) in refusals)
        {
            var status = StatusOf(result);
            Assert.True(
                status == StatusCodes.Status403Forbidden,
                $"An administrator was answered {status} when it tried to {verb} its own account, not 403.");
            Assert.Equal(AccountRefusalResponse.SelfProtected, StageKeyOf(result));
        }

        var unchanged = await ReadAccountAsync(database, caller.Id);
        Assert.Equal(SessionType.Admin, unchanged.Role);
        Assert.False(unchanged.IsDisabled);

        // Renaming and setting a password on your own account are what the three refusals must leave
        // open: neither signs the caller out, and both are how a person maintains their own account.
        var renamed = await controller.EditAccountAsync(
            caller.Id, new EditAccountRequest { Username = "renamed-itself", Password = NewPassword });
        Assert.Equal(StatusCodes.Status200OK, StatusOf(renamed));
        Assert.Equal("renamed-itself", (await ReadAccountAsync(database, caller.Id)).Username);

        var demoted = await SeedAccountAsync(database.Factory, "third-admin", SessionType.Admin);
        var disabled = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        var deleted = await SeedAccountAsync(database.Factory, "another-reader", SessionType.User);

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetRoleAsync(demoted.Id, new SetAccountRoleRequest { Role = SessionType.User })));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(disabled.Id, new SetAccountDisabledRequest { Disabled = true })));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.DeleteAccountAsync(deleted.Id)));
    }

    /// <summary>
    /// An administrator replacing somebody else's password can sign in as that account afterwards, so
    /// the row that says who did it is the only thing that answers "who could have been signed in as
    /// this account". The actor and the target are different accounts, which is what separates this
    /// from a person changing their own password.
    /// </summary>
    [Fact]
    public async Task ReplacingAnotherAccountsPasswordIsRecordedAgainstTheAdministratorWhoDidIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var target = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        var session = await SeedSessionAsync(database.Factory, owner);
        var controller = NewController(database.Factory, session);

        // A rename on its own leaves the password alone, so it writes no row and the single entry
        // read below is the one the password produced.
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.EditAccountAsync(target.Id, new EditAccountRequest { Username = "renamed" })));

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.EditAccountAsync(
                target.Id, new EditAccountRequest { Username = "renamed", Password = NewPassword })));

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.PasswordChanged, entry.Event);
        Assert.Equal(owner.Id, entry.PerformedByAccountId);
        Assert.Equal(session.Id, entry.PerformedBySessionId);
        Assert.Equal(target.Id, entry.TargetAccountId);
    }

    /// <summary>
    /// The other half of the same event. A person changing their own password is the actor and the
    /// target, so the trail tells the two apart by comparing the two columns rather than by carrying
    /// a second event.
    /// </summary>
    [Fact]
    public async Task ChangingYourOwnPasswordIsRecordedAgainstTheAccountItself()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        var session = await SeedSessionAsync(database.Factory, account);

        var configuration = NewConfiguration(authenticationEnabled: true);
        var controller = NewAuthController(
            database.Factory,
            session,
            new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!),
            new AccountLockout(NullLogger<AccountLockout>.Instance),
            configuration);

        var changed = await controller.ChangePasswordAsync(new ChangePasswordRequest
        {
            CurrentPassword = SeedPassword,
            NewPassword = NewPassword
        });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(changed));

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.PasswordChanged, entry.Event);
        Assert.Equal(account.Id, entry.PerformedByAccountId);
        Assert.Equal(session.Id, entry.PerformedBySessionId);
        Assert.Equal(account.Id, entry.TargetAccountId);
    }

    /// <summary>
    /// An audit trail that can undo the change it is recording turns a logging fault into an account
    /// that half exists. The account is created whether or not the row lands.
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
    /// they sign out. Changing the role, disabling the account and deleting it all end them.
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
    /// second one. Renaming without sending a password does not have to restate one.
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

    /// <summary>
    /// Somebody who locked themselves out is the reason an administrator sets a password for them, so
    /// the count of failures has to go with the password that produced them. The lock and the password
    /// are separate terms in the sign-in refusal, and both refusals read the same by design, so leaving
    /// the count standing sends the person back to what looks like a wrong password.
    /// </summary>
    [Fact]
    public async Task SettingAnAccountsPasswordLetsALockedOutAccountSignInAgain()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var target = await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var lockout = new AccountLockout(NullLogger<AccountLockout>.Instance);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            lockout.RecordFailure(target.Id);
        }

        Assert.True(lockout.IsLocked(target.Id));

        var controller = NewController(
            database.Factory,
            await SeedSessionAsync(database.Factory, owner),
            lockout: lockout);

        var edited = await controller.EditAccountAsync(
            target.Id,
            new EditAccountRequest { Username = "reader", Password = NewPassword });
        Assert.Equal(StatusCodes.Status200OK, StatusOf(edited));

        var configuration = NewConfiguration(authenticationEnabled: true);
        var apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var signIn = NewAuthController(database.Factory, caller: null, apiKeyService, lockout, configuration);

        var signedIn = await signIn.LoginAsync(new LoginRequest
        {
            Username = "reader",
            Password = NewPassword,
            ApiKey = apiKeyService.GetApiKey()
        });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(signedIn));
    }

    /// <summary>
    /// The owner emptying the table is the exception to the rule that the main administrator cannot
    /// be deleted: every row goes, including that one, and every session goes with them so the
    /// first-admin wizard is what the next request sees.
    /// </summary>
    [Fact]
    public async Task TheMainAdministratorCanWipeEveryAccountAndSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        var reader = await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var ownerSession = await SeedSessionAsync(database.Factory, owner);
        await SeedSessionAsync(database.Factory, administrator);
        await SeedSessionAsync(database.Factory, reader);

        var controller = NewController(database.Factory, ownerSession);
        var wiped = await controller.WipeAccountsAsync();

        Assert.Equal(StatusCodes.Status200OK, StatusOf(wiped));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(0, await context.UserAccounts.CountAsync());
        Assert.Equal(0, await context.UserSessions.CountAsync());
    }

    /// <summary>
    /// A second administrator carries the same role as the owner and is still refused: the check
    /// reads IsMainAdmin on the stored row, not the session type. The table is left as it was.
    /// </summary>
    [Fact]
    public async Task ANonMainAdministratorCannotWipeAccounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.Admin);
        await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var wiped = await controller.WipeAccountsAsync();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(wiped));
        Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(wiped));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(3, await context.UserAccounts.CountAsync());
        Assert.True(await context.UserAccounts.AnyAsync(a => a.Id == owner.Id && a.IsMainAdmin));
    }

    /// <summary>
    /// A user is refused the same way, not 404: wipe names no other account, so the visibility
    /// query never runs.
    /// </summary>
    [Fact]
    public async Task AUserCannotWipeAccounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var wiped = await controller.WipeAccountsAsync();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(wiped));
        Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(wiped));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(2, await context.UserAccounts.CountAsync());
    }

    /// <summary>
    /// A caller with no account row is the API-key-only shape. CallerMayGrantAdmin would admit it
    /// when authentication is off; wipe must not, because emptying the table is not a standing
    /// right of the key.
    /// </summary>
    [Fact]
    public async Task ACallerWithNoAccountCannotWipeAccounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, account: null);

        var withAuthOn = NewController(database.Factory, session);
        var refusedOn = await withAuthOn.WipeAccountsAsync();
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refusedOn));
        Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(refusedOn));

        var withAuthOff = NewController(database.Factory, session, authenticationEnabled: false);
        var refusedOff = await withAuthOff.WipeAccountsAsync();
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refusedOff));
        Assert.Equal(AccountRefusalResponse.AdminRoleRequiresMainAdmin, StageKeyOf(refusedOff));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(1, await context.UserAccounts.CountAsync());
    }

    /// <summary>
    /// First-admin creation checks the hour-from-start window, not whether the table is empty. A
    /// wipe on a process that has already been up longer than that hour has to open a new one or
    /// every submit on the next screen is refused until a restart.
    /// </summary>
    [Fact]
    public async Task WipingAccountsReopensTheFirstAdministratorWindow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var ownerSession = await SeedSessionAsync(database.Factory, owner);

        var claimWindow = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        claimWindow.Expire();
        Assert.False(claimWindow.IsOpen);

        var controller = NewController(database.Factory, ownerSession, claimWindow: claimWindow);
        var wiped = await controller.WipeAccountsAsync();

        Assert.Equal(StatusCodes.Status200OK, StatusOf(wiped));
        Assert.True(claimWindow.IsOpen);
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
        IdentityAuditService? auditService = null,
        AccountLockout? lockout = null,
        AccountClaimWindow? claimWindow = null)
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
            lockout ?? new AccountLockout(NullLogger<AccountLockout>.Instance),
            claimWindow ?? new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance),
            NullLogger<AccountsController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// The sign-in controller, built by hand for the same reason the account controller above is: the
    /// two account rules these tests cross into, a lock cleared with a password and the row a password
    /// change writes, are only observable through it. <paramref name="caller"/> is null for the sign-in
    /// path, which reaches the controller with no session yet.
    /// </summary>
    private static AuthController NewAuthController(
        TestDbContextFactory factory,
        UserSession? caller,
        ApiKeyService apiKeyService,
        AccountLockout lockout,
        IConfiguration configuration)
    {
        var controller = new AuthController(
            new SessionService(
                factory,
                apiKeyService,
                NullLogger<SessionService>.Instance,
                stateService: null!,
                signalR: DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                configuration),
            NullLogger<AuthController>.Instance,
            factory,
            stateService: null!,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            apiKeyService,
            new PasswordHasher<UserAccount>(),
            lockout,
            new IdentityAuditService(factory, NullLogger<IdentityAuditService>.Instance));

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
