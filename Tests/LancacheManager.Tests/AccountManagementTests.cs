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
/// The account-management rules: the primary administrator owns the installation, ordinary users
/// share one account level, and the owner is hidden from every other account holder and cannot be
/// taken away from whoever holds it. New accounts are always ordinary users.
///
/// Every caller below is built by seeding an account row and a session that names it, rather than by
/// taking another session and flipping its type: the
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
    /// A user sees every ordinary account while the owner remains hidden.
    /// </summary>
    [Fact]
    public async Task AUserIsAnsweredTheAccountListWithoutTheAdministrators()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
        var caller = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        await SeedAccountAsync(database.Factory, "other-reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var listed = AccountsOf(await controller.GetAccountsAsync());

        Assert.Equal(
            new[] { "other-reader", "reader", "second-admin" },
            listed.Select(a => a.Username).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// An ordinary account is answered every account except the owner. Seeing that
    /// row is what used to let them name it on edit, disable, delete and the session list.
    /// </summary>
    [Fact]
    public async Task AnAdministratorIsAnsweredEveryAccountExceptTheOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
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
        await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
        await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var listed = AccountsOf(await controller.GetAccountsAsync());

        Assert.Equal(
            new[] { "owner", "reader", "second-admin" },
            listed.Select(a => a.Username).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.True(listed.Single(a => a.Username == "owner").IsMainAdmin);
    }

    [Fact]
    public async Task MainAdministratorOwnershipDoesNotDependOnTheLegacyRole()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);

        var ownerController = await NewControllerAsync(database.Factory, owner);
        Assert.Equal(
            new[] { "owner", "second-admin" },
            AccountsOf(await ownerController.GetAccountsAsync())
                .Select(account => account.Username)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        var administratorController = await NewControllerAsync(database.Factory, administrator);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await administratorController.GetAccountAsync(owner.Id)));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await ownerController.WipeAccountsAsync()));
    }

    /// <summary>
    /// Every verb, not only the list. An account an ordinary caller is not shown is also an
    /// account they cannot name.
    /// </summary>
    [Fact]
    public async Task ASecondAdministratorCannotReachTheOwnerByItsId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var id = owner.Id;

        var refusals = new (string Verb, ActionResult? Result)[]
        {
            ("get", (await controller.GetAccountAsync(id)).Result),
            ("edit", (await controller.EditAccountAsync(
                id, new EditAccountRequest { Username = "taken-over", Password = NewPassword })).Result),
            ("disable", (await controller.SetDisabledAsync(
                id, new SetAccountDisabledRequest { Disabled = true })).Result),
            ("delete", (await controller.DeleteAccountAsync(id)).Result)
        };

        foreach (var (verb, result) in refusals)
        {
            var status = StatusOf(result);
            Assert.True(
                status == StatusCodes.Status404NotFound,
                $"{verb} answered an ordinary caller {status} for the owner, not 404.");
            Assert.Equal(AccountRefusalResponse.AccountNotFound, StageKeyOf(result));
        }

        var stored = await ReadAccountAsync(database, owner.Id);
        Assert.Equal("owner", stored.Username);
        Assert.True(stored.IsMainAdmin);
        Assert.False(stored.IsDisabled);
    }

    /// <summary>
    /// Every ordinary verb reaches another ordinary account from a user account. Separate targets
    /// keep the destructive delete from affecting the other assertions.
    /// </summary>
    [Fact]
    public async Task AUserCannotReachAnAdministratorAccountByItsId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var caller = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        var controller = await NewControllerAsync(database.Factory, caller);
        var read = await SeedAccountAsync(database.Factory, "read-admin", SessionType.User);
        var edited = await SeedAccountAsync(database.Factory, "edit-admin", SessionType.User);
        var disabled = await SeedAccountAsync(database.Factory, "disable-admin", SessionType.User);
        var deleted = await SeedAccountAsync(database.Factory, "delete-admin", SessionType.User);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.GetAccountAsync(read.Id)));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.EditAccountAsync(
                edited.Id, new EditAccountRequest { Username = "taken-over", Password = NewPassword })));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(
                disabled.Id, new SetAccountDisabledRequest { Disabled = true })));

        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.DeleteAccountAsync(deleted.Id)));

        Assert.Equal("taken-over", (await ReadAccountAsync(database, edited.Id)).Username);
        Assert.True((await ReadAccountAsync(database, disabled.Id)).IsDisabled);
        await using var context = database.Factory.CreateDbContext();
        Assert.False(await context.UserAccounts.AnyAsync(account => account.Id == deleted.Id));
    }

    /// <summary>
    /// Nine attempts on the account that owns the installation: edit, delete and disable, tried by
    /// two ordinary users and by that account itself. All nine are refused,
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
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
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
                ("disable", StatusOf(await controller.SetDisabledAsync(owner.Id, new SetAccountDisabledRequest { Disabled = true })))
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
    /// The caller below carries an ordinary account session, creates only an ordinary account and
    /// still cannot perform owner-only work.
    /// </summary>
    [Fact]
    public async Task TheAdministratorRoleCheckReadsTheCallersAccountRowRatherThanItsClaim()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);

        var session = await SeedSessionAsync(database.Factory, caller);
        Assert.Equal(SessionType.User, session.SessionType);

        var controller = NewController(database.Factory, session);
        var created = await controller.CreateAccountAsync(NewAccountRequest("minted-by-claim"));

        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
        Assert.Equal(SessionType.User, AccountOf(created).Role);
        Assert.False(AccountOf(created).IsMainAdmin);
        var wipe = await controller.WipeAccountsAsync();
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(wipe));
        Assert.Equal(AccountRefusalResponse.WipeRequiresMainAdmin, StageKeyOf(wipe));
    }

    /// <summary>
    /// With authentication turned off the shared accountless session keeps account-management
    /// compatibility but creates only ordinary user rows. The real owner remains protected.
    /// </summary>
    [Fact]
    public async Task WithAuthenticationDisabledACallerWithNoAccountCreatesAdministrators()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, account: null);

        var controller = NewController(database.Factory, session, authenticationEnabled: false);

        var created = await controller.CreateAccountAsync(NewAccountRequest("minted-with-auth-off"));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
        Assert.Equal(SessionType.User, AccountOf(created).Role);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await controller.DeleteAccountAsync(owner.Id)));
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            StatusOf(await controller.SetDisabledAsync(owner.Id, new SetAccountDisabledRequest { Disabled = true })));
        // The caller proved nothing but the configuration: it has no account, so the actor half of the
        // row is empty rather than the write throwing.
        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();
        Assert.Equal(IdentityAuditEvent.AccountCreated, entry.Event);
        Assert.Null(entry.PerformedByAccountId);
        Assert.Equal(session.Id, entry.PerformedBySessionId);
    }

    /// <summary>
    /// A direct-controller accountless compatibility call creates an ordinary row. Live authenticated
    /// routes still require a real session.
    /// </summary>
    [Fact]
    public async Task WithAuthenticationEnabledACallerWithNoAccountCannotCreateAnAdministrator()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, account: null);

        var controller = NewController(database.Factory, session);

        var administrator = await controller.CreateAccountAsync(NewAccountRequest("minted-with-a-key"));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(administrator));
        Assert.Equal(SessionType.User, AccountOf(administrator).Role);

        var reader = await controller.CreateAccountAsync(NewAccountRequest("reader-with-a-key"));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(reader));
        Assert.Equal(SessionType.User, AccountOf(reader).Role);
    }

    /// <summary>
    /// The four real mutations name their caller and target.
    /// </summary>
    [Fact]
    public async Task TheFiveAccountEventsAreRecordedAgainstTheCallerAndTheTarget()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database.Factory, owner);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NotificationCalls>();
        var notificationCalls = (NotificationCalls)(object)notifications;
        var controller = NewController(database.Factory, session, notifications: notifications);
        var before = DateTime.UtcNow;

        var created = await controller.CreateAccountAsync(NewAccountRequest("audited"));
        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
        var target = AccountOf(created).Id;

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(target, new SetAccountDisabledRequest { Disabled = true })));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(target, new SetAccountDisabledRequest { Disabled = false })));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.DeleteAccountAsync(target)));
        Assert.Equal(4, notificationCalls.Count);

        await using var context = database.Factory.CreateDbContext();
        var entries = await context.IdentityAuditEntries.OrderBy(e => e.Id).ToListAsync();

        Assert.Equal(
            new[]
            {
                IdentityAuditEvent.AccountCreated,
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
    /// Delete and disable, tried by an ordinary account on itself. Both remain protected.
    ///
    /// The same two verbs on somebody else's row are attempted afterwards, because a guard that
    /// refused everybody would pass the first half of this test on its own.
    /// </summary>
    [Fact]
    public async Task AnAccountCannotBeDeletedDisabledOrDemotedByItself()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
        var controller = await NewControllerAsync(database.Factory, caller);

        var refusals = new (string Verb, ActionResult? Result)[]
        {
            ("delete", (await controller.DeleteAccountAsync(caller.Id)).Result),
            ("disable", (await controller.SetDisabledAsync(
                caller.Id, new SetAccountDisabledRequest { Disabled = true })).Result)
        };

        foreach (var (verb, result) in refusals)
        {
            var status = StatusOf(result);
            Assert.True(
                status == StatusCodes.Status403Forbidden,
                $"An account was answered {status} when it tried to {verb} itself, not 403.");
            Assert.Equal(AccountRefusalResponse.SelfProtected, StageKeyOf(result));
        }

        var unchanged = await ReadAccountAsync(database, caller.Id);
        Assert.Equal(SessionType.User, unchanged.Role);
        Assert.False(unchanged.IsDisabled);

        // Renaming and setting a password on your own account are what the two refusals must leave
        // open: neither signs the caller out, and both are how a person maintains their own account.
        var renamed = await controller.EditAccountAsync(
            caller.Id, new EditAccountRequest { Username = "renamed-itself", Password = NewPassword });
        Assert.Equal(StatusCodes.Status200OK, StatusOf(renamed));
        Assert.Equal("renamed-itself", (await ReadAccountAsync(database, caller.Id)).Username);

        var disabled = await SeedAccountAsync(database.Factory, "reader", SessionType.User);
        var deleted = await SeedAccountAsync(database.Factory, "another-reader", SessionType.User);

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

        var created = await controller.CreateAccountAsync(NewAccountRequest("audit-is-broken"));

        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.UserAccounts.AnyAsync(a => a.Username == "audit-is-broken"));
        Assert.Empty(context.IdentityAuditEntries);
    }

    /// <summary>
    /// Disabling and deleting are real mutations and end the target's sessions.
    /// </summary>
    [Fact]
    public async Task ChangingTheRoleDisablingAndDeletingAllEndTheAccountsSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var callerSession = await SeedSessionAsync(database.Factory, owner);
        var controller = NewController(database.Factory, callerSession);

        var disabled = await SeedAccountAsync(database.Factory, "disabled", SessionType.User);
        var deleted = await SeedAccountAsync(database.Factory, "deleted", SessionType.User);

        var disabledSession = await SeedSessionAsync(database.Factory, disabled);
        var deletedSession = await SeedSessionAsync(database.Factory, deleted);

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await controller.SetDisabledAsync(disabled.Id, new SetAccountDisabledRequest { Disabled = true })));
        Assert.Equal(StatusCodes.Status200OK, StatusOf(await controller.DeleteAccountAsync(deleted.Id)));

        await using var context = database.Factory.CreateDbContext();
        foreach (var sessionId in new[] { disabledSession.Id, deletedSession.Id })
        {
            Assert.True(
                (await context.UserSessions.SingleAsync(s => s.Id == sessionId)).IsRevoked,
                $"Session {sessionId} outlived the change to the account it belongs to.");
        }

        // The caller's own session is untouched: neither change was about the caller.
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
            new CreateAccountRequest { Username = "weak", Password = "short" });
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(weak));
        Assert.Equal(AccountRefusalResponse.CredentialsRejected, StageKeyOf(weak));

        var created = await controller.CreateAccountAsync(NewAccountRequest("renamed-later"));
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
    /// The owner's account is withheld from an ordinary account and the unique index still
    /// refuses a second row holding its name, so both duplicate-name paths answer that name the
    /// way they answer a name a visible account holds. The probe behind them reads the whole table
    /// on purpose: narrowed to the visible accounts, a taken name would answer 500.
    /// </summary>
    [Fact]
    public async Task ANameTheOwnerHoldsIsRefusedLikeAnyOtherTakenName()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
        var visible = await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);

        var hiddenName = await controller.CreateAccountAsync(NewAccountRequest("owner"));
        var visibleName = await controller.CreateAccountAsync(NewAccountRequest("reader"));

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(hiddenName));
        Assert.Equal(StatusOf(visibleName), StatusOf(hiddenName));
        Assert.Equal(AccountRefusalResponse.UsernameTaken, StageKeyOf(hiddenName));
        Assert.Equal(StageKeyOf(visibleName), StageKeyOf(hiddenName));

        var renamed = await controller.EditAccountAsync(
            visible.Id, new EditAccountRequest { Username = "owner" });

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(renamed));
        Assert.Equal(AccountRefusalResponse.UsernameTaken, StageKeyOf(renamed));
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
        var administrator = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
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
    /// An ordinary account is still refused: the check reads IsMainAdmin on the stored row, not the
    /// session type. The table is left as it was.
    /// </summary>
    [Fact]
    public async Task ANonMainAdministratorCannotWipeAccounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var owner = await SeedAccountAsync(database.Factory, "owner", SessionType.Admin, mainAdmin: true);
        var caller = await SeedAccountAsync(database.Factory, "second-admin", SessionType.User);
        await SeedAccountAsync(database.Factory, "reader", SessionType.User);

        var controller = await NewControllerAsync(database.Factory, caller);
        var wiped = await controller.WipeAccountsAsync();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(wiped));
        Assert.Equal(AccountRefusalResponse.WipeRequiresMainAdmin, StageKeyOf(wiped));

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
        Assert.Equal(AccountRefusalResponse.WipeRequiresMainAdmin, StageKeyOf(wiped));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(2, await context.UserAccounts.CountAsync());
    }

    /// <summary>
    /// A caller with no account row is the API-key-only or shared-authentication shape. Account
    /// compatibility may admit it, but wipe must not: emptying the table is not a standing right of
    /// an accountless session.
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
        Assert.Equal(AccountRefusalResponse.WipeRequiresMainAdmin, StageKeyOf(refusedOn));

        var withAuthOff = NewController(database.Factory, session, authenticationEnabled: false);
        var refusedOff = await withAuthOff.WipeAccountsAsync();
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refusedOff));
        Assert.Equal(AccountRefusalResponse.WipeRequiresMainAdmin, StageKeyOf(refusedOff));

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
        AccountClaimWindow? claimWindow = null,
        ISignalRNotificationService? notifications = null)
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
            notifications ?? DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
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

    private static CreateAccountRequest NewAccountRequest(string username) =>
        new() { Username = username, Password = NewPassword };

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

    public class NotificationCalls : DispatchProxy
    {
        public int Count { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Count++;
            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }
}
