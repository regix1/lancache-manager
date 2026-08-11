using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Signing in with an API key, a username and a password; what happens when any one of them is wrong;
/// the per-account limit on guessing; and changing your own password.
/// [38][39][40][41][42][43][43b][44][45][46][47][49g][49h]
/// </summary>
public sealed class AccountLoginTests : IDisposable
{
    private const string Password = "Correct-Horse-9";
    private const string OtherPassword = "Different-Horse-4";

    /// <summary>
    /// The application configures 220,000 iterations, which costs a few hundred milliseconds a hash.
    /// These tests are about which answers come back rather than how long the hashing takes, and a run
    /// that signs in thirty times at the real count spends most of a minute doing arithmetic.
    /// </summary>
    private const int TestIterationCount = 1_000;

    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;
    private readonly PasswordHasher<UserAccount> _passwordHasher;

    public AccountLoginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-login-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            })
            .Build();
        _apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, _configuration, pathResolver: null!);
        _passwordHasher = NewHasher(TestIterationCount);
    }

    // --- Signing in ---------------------------------------------------------------------------

    /// <summary>
    /// All three together, and the session that comes back is the one CreateAdminSessionAsync writes:
    /// the never-expires sentinel it stamps on every admin session, and the cookie
    /// SessionService.SetSessionCookie sets, with the flags it sets. [38]
    /// </summary>
    [Fact]
    public async Task SignInWithKeyUsernameAndPasswordCreatesASession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        var result = await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()));

        var body = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Success);
        Assert.Equal(SessionType.Admin, body.SessionType);

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserSessions.SingleAsync();
        Assert.False(stored.IsRevoked);
        Assert.Equal(new DateTime(2099, 12, 31, 0, 0, 0), stored.ExpiresAtUtc);
        Assert.Equal(account.Id, stored.AccountId);

        var cookie = Assert.Single(sign.Request.Response.Headers.SetCookie!);
        Assert.NotNull(cookie);
        Assert.StartsWith("LancacheManager.Session=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The session the sign-in creates names the account it signed in as, which is the column every
    /// account write keys off when it revokes an identity's live sessions. Nothing wrote it before
    /// this. [49g]
    /// </summary>
    [Fact]
    public async Task SignInStampsTheAccountOnTheSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(account.Id, (await context.UserSessions.SingleAsync()).AccountId);
    }

    /// <summary>
    /// The account's role is what the session carries, not the name of the method that writes it. A
    /// user signs in to a user session. [49g]
    /// </summary>
    [Fact]
    public async Task SignInGivesAUserAccountAUserSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "helper", role: SessionType.User);
        var sign = NewSignIn(database);

        var result = await sign.Controller.LoginAsync(NewLogin("helper", Password, _apiKeyService.GetApiKey()));

        var body = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(SessionType.User, body.SessionType);

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(SessionType.User, (await context.UserSessions.SingleAsync()).SessionType);
    }

    /// <summary>
    /// One of the three missing or wrong is a refusal. The key on its own was a sign-in until this
    /// change, which is why the wrong-password case with a perfectly good key is the one that matters
    /// most here. [39]
    /// </summary>
    [Theory]
    [InlineData("operator", "wrong-password", true)]
    [InlineData("nobody", Password, true)]
    [InlineData("operator", Password, false)]
    [InlineData("operator", "", true)]
    [InlineData("", Password, true)]
    public async Task SignInIsRefusedWhenAnyOneOfTheThreeIsWrong(string username, string password, bool goodKey)
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        var result = await sign.Controller.LoginAsync(
            NewLogin(username, password, goodKey ? _apiKeyService.GetApiKey() : "not-the-key"));

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(context.UserSessions);
    }

    /// <summary>
    /// An absent key is refused like a wrong one. Nothing about the request body distinguishes the two
    /// callers, and the endpoint is reachable by anyone who can reach the port. [39]
    /// </summary>
    [Fact]
    public async Task SignInIsRefusedWithNoKeyAtAll()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        var result = await sign.Controller.LoginAsync(
            new LoginRequest { Username = "operator", Password = Password });

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result));
        Assert.Equal(CredentialRefusalResponse.InvalidCredentials, StageKeyOf(result));
    }

    /// <summary>
    /// Every way this fails answers the same status and the same body, so a caller cannot use the
    /// refusals to learn which usernames exist, whether a password was close, or whether an account is
    /// switched off. The locked case is in here too: telling somebody an account is locked confirms it
    /// exists, which is the one thing the shared message is for. [44]
    /// </summary>
    [Fact]
    public async Task EveryFailureAnswersIdentically()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        await NewAccountAsync(database, "switched-off", disabled: true);
        var locked = await NewAccountAsync(database, "locked-out");
        var sign = NewSignIn(database);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            sign.Lockout.RecordFailure(locked.Id);
        }

        var refusals = new[]
        {
            await sign.Controller.LoginAsync(NewLogin("nobody", Password, _apiKeyService.GetApiKey())),
            await sign.Controller.LoginAsync(NewLogin("operator", "wrong-password", _apiKeyService.GetApiKey())),
            await sign.Controller.LoginAsync(NewLogin("operator", Password, "not-the-key")),
            await sign.Controller.LoginAsync(NewLogin("switched-off", Password, _apiKeyService.GetApiKey())),
            await sign.Controller.LoginAsync(NewLogin("locked-out", Password, _apiKeyService.GetApiKey()))
        };

        Assert.Single(refusals.Select(StatusOf).Distinct());
        Assert.Single(refusals.Select(StageKeyOf).Distinct());
        Assert.Single(refusals.Select(ErrorOf).Distinct());
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(refusals[0]));
        Assert.Equal(CredentialRefusalResponse.InvalidCredentials, StageKeyOf(refusals[0]));
    }

    /// <summary>
    /// The key is checked by the one comparison that was written to be constant-time, and the
    /// controller holds no second one of its own. [40]
    /// </summary>
    [Fact]
    public async Task TheKeyIsCheckedByTheExistingConstantTimeCompare()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, "not-the-key"))));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));

        var controller = await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), "Api", "LancacheManager", "Controllers", "AuthController.cs"));
        Assert.Contains("_apiKeyService.ValidateApiKey(request.ApiKey)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("SequenceEqual", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("FixedTimeEquals", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey ==", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("== request.ApiKey", controller, StringComparison.Ordinal);
    }

    /// <summary>
    /// A guest still asks for nothing. The three-factor sign-in is the account door; the guest door is
    /// the one an anonymous visitor on the LAN uses and it did not move. [41]
    /// </summary>
    [Fact]
    public async Task GuestSignInStillNeedsNoCredential()
    {
        await using var database = await TestDatabase.CreateAsync();
        var state = CreateStateService(_root);
        state.SetSetupCompleted(true);
        var sign = NewSignIn(database, state);

        var result = await sign.Controller.StartGuestAsync();

        var body = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Success);
        Assert.Equal(SessionType.Guest, body.SessionType);

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserSessions.SingleAsync();
        Assert.Equal(SessionType.Guest, stored.SessionType);
        Assert.Null(stored.AccountId);
    }

    // --- The stored hash ----------------------------------------------------------------------

    /// <summary>
    /// An installation whose iteration count was stepped up carries hashes written at the old one. The
    /// sign-in that verifies such a hash is the only moment the password is in hand, so that is when it
    /// is written again at the new count - together with the last-login stamp, in one save. [42]
    /// </summary>
    [Fact]
    public async Task SignInRewritesAHashStoredAtALowerIterationCount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator", hasher: NewHasher(TestIterationCount / 2));
        var sign = NewSignIn(database);

        var before = await StoredHashAsync(database, account.Id);
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.NotEqual(before, stored.PasswordHash);
        Assert.Equal(
            PasswordVerificationResult.Success,
            _passwordHasher.VerifyHashedPassword(stored, stored.PasswordHash, Password));
        Assert.NotNull(stored.LastLoginAtUtc);
    }

    /// <summary>
    /// The other half: a hash already written at the configured count is left exactly as it is, and the
    /// sign-in is still stamped. A rewrite every time would be work for nothing and would churn the row
    /// on every request. [42]
    /// </summary>
    [Fact]
    public async Task SignInLeavesACurrentHashAloneAndStillStampsTheLogin()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        var before = await StoredHashAsync(database, account.Id);
        await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()));

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal(before, stored.PasswordHash);
        Assert.NotNull(stored.LastLoginAtUtc);
    }

    // --- The per-account limit ----------------------------------------------------------------

    /// <summary>
    /// Enough failures inside the window and the account stops accepting the right password. The limit
    /// follows the account rather than the caller, which is what the per-IP limiter cannot do: an
    /// attack spread over many addresses passes that one untouched. [43]
    /// </summary>
    [Fact]
    public async Task EnoughFailedAttemptsLockTheAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                StatusCodes.Status401Unauthorized,
                StatusOf(await sign.Controller.LoginAsync(
                    NewLogin("operator", "wrong-password", _apiKeyService.GetApiKey()))));
        }

        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(context.UserSessions);
    }

    /// <summary>
    /// And it locks that account only. A limit that anybody could trip against anybody else's name
    /// would be a way to switch off any account you can name. [43]
    /// </summary>
    [Fact]
    public async Task LockingOneAccountLeavesAnotherAlone()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        await NewAccountAsync(database, "colleague");
        var sign = NewSignIn(database);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await sign.Controller.LoginAsync(NewLogin("operator", "wrong-password", _apiKeyService.GetApiKey()));
        }

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("colleague", Password, _apiKeyService.GetApiKey()))));
    }

    /// <summary>
    /// A wrong key cannot be attributed to anyone, so it must not count against the account whose name
    /// happened to be in the same request. Counting it would let anybody lock any account they can name
    /// without ever holding the key. [43]
    /// </summary>
    [Fact]
    public async Task AWrongKeyDoesNotCountAgainstTheAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await sign.Controller.LoginAsync(NewLogin("operator", Password, "not-the-key"));
        }

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));
    }

    /// <summary>
    /// Signing in successfully forgets the failures before it, so a run of mistypes followed by the
    /// right password does not leave the account one mistake from being locked next week. [43]
    /// </summary>
    [Fact]
    public async Task ASuccessfulSignInForgetsTheFailuresBeforeIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await sign.Controller.LoginAsync(NewLogin("operator", "wrong-password", _apiKeyService.GetApiKey()));
        }

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await sign.Controller.LoginAsync(NewLogin("operator", "wrong-password", _apiKeyService.GetApiKey()));
        }

        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));
    }

    /// <summary>
    /// The bypass this closes: an attacker holding any live session guesses at the change-password
    /// endpoint instead of the sign-in screen. Both feed the same count, so the account locks either
    /// way. [43b]
    /// </summary>
    [Fact]
    public async Task WrongCurrentPasswordsOnTheChangeEndpointLockTheAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);
        var signedIn = await SignInAsync(sign, "operator");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await sign.Controller.ChangePasswordAsync(
                new ChangePasswordRequest { CurrentPassword = "wrong-password", NewPassword = OtherPassword });
            Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(refused));
        }

        Assert.True(sign.Lockout.IsLocked(account.Id));

        var login = NewSignIn(database, lockout: sign.Lockout);
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(await login.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));
        Assert.NotNull(signedIn.RawToken);
    }

    /// <summary>
    /// One count for the whole process, which is the part every other test here is blind to. They build
    /// the counter by hand and hand the same instance to both controllers, so a registration that gave
    /// each request its own would reset the count between attempts and never reach the limit - and all
    /// of them would still pass while no account could be locked at all. [43][43b]
    /// </summary>
    [Fact]
    public async Task TheCountIsSharedAcrossRequests()
    {
        var startup = await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), "Api", "LancacheManager", "Program.cs"));

        Assert.Contains("AddSingleton<AccountLockout>()", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("AddScoped<AccountLockout>", startup, StringComparison.Ordinal);
        Assert.DoesNotContain("AddTransient<AccountLockout>", startup, StringComparison.Ordinal);
    }

    // --- Disabling and changing a password ----------------------------------------------------

    /// <summary>
    /// Switching an account off has to reach the sessions it already has, not only the sign-ins it has
    /// not made yet. A session that outlived the disable is somebody still working. [45]
    /// </summary>
    [Fact]
    public async Task DisablingAnAccountStopsItsExistingSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);
        var signedIn = await SignInAsync(sign, "operator");
        Assert.NotNull(await sign.Sessions.ValidateSessionAsync(signedIn.RawToken));

        await using (var context = database.Factory.CreateDbContext())
        {
            (await context.UserAccounts.SingleAsync(a => a.Id == account.Id)).IsDisabled = true;
            await context.SaveChangesAsync();
        }

        Assert.Null(await sign.Sessions.ValidateSessionAsync(signedIn.RawToken));
    }

    /// <summary>
    /// The two Phase 2 guarantees, re-run against a session a real sign-in produced rather than one a
    /// test stamped by hand. Until the sign-in filled the column in, both of them were true only of
    /// rows that never occurred. [49g]
    /// </summary>
    [Fact]
    public async Task ASignedInSessionIsRevokedWithItsAccountAndRejectedWhenItIsDeleted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        await NewAccountAsync(database, "colleague");
        var sign = NewSignIn(database);

        var signedIn = await SignInAsync(sign, "operator");
        var other = await SignInAsync(NewSignIn(database), "colleague");

        Assert.Equal(1, await sign.Sessions.RevokeAccountSessionsAsync(account.Id));
        Assert.Null(await sign.Sessions.ValidateSessionAsync(signedIn.RawToken));
        Assert.NotNull(await sign.Sessions.ValidateSessionAsync(other.RawToken));

        var deleted = await SignInAsync(NewSignIn(database), "operator");
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Remove(await context.UserAccounts.SingleAsync(a => a.Id == account.Id));
            await context.SaveChangesAsync();
        }

        Assert.Null(await sign.Sessions.ValidateSessionAsync(deleted.RawToken));
    }

    /// <summary>
    /// Changing the password replaces the session's token, and the one the caller arrived with stops
    /// working immediately rather than after the rotation grace period. A token obtained under the old
    /// password does not outlive it. [46]
    /// </summary>
    [Fact]
    public async Task ChangingThePasswordReplacesTheSessionToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);
        var signedIn = await SignInAsync(sign, "operator");

        var result = await sign.Controller.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = Password, NewPassword = OtherPassword });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Null(await sign.Sessions.ValidateSessionAsync(signedIn.RawToken));

        var replacement = SessionTokenFromCookies(sign.Request);
        Assert.NotNull(replacement);
        Assert.NotEqual(signedIn.RawToken, replacement);
        Assert.NotNull(await sign.Sessions.ValidateSessionAsync(replacement!));
    }

    /// <summary>
    /// And the new password is the one that works afterwards, which is the part a token change would
    /// otherwise hide. [46]
    /// </summary>
    [Fact]
    public async Task AfterAChangeTheNewPasswordSignsInAndTheOldOneDoesNot()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);
        await SignInAsync(sign, "operator");

        await sign.Controller.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = Password, NewPassword = OtherPassword });

        var login = NewSignIn(database);
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(await login.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await NewSignIn(database).Controller.LoginAsync(
                NewLogin("operator", OtherPassword, _apiKeyService.GetApiKey()))));
    }

    /// <summary>
    /// A caller with a session but no account behind it is refused with an authorization status, not
    /// the 400 the preferences controller answers a session-less caller with. A caller with no session
    /// at all never reaches the method: the controller carries [Authorize]. [47]
    /// </summary>
    [Fact]
    public async Task ChangingThePasswordIsRefusedWhenTheSessionHasNoAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sign = NewSignIn(database);
        var keyOnly = await sign.Sessions.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), sign.Request);
        Assert.NotNull(keyOnly);
        sign.Request.Items["Session"] = keyOnly!.Value.Session;

        var result = await sign.Controller.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = Password, NewPassword = OtherPassword });

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        Assert.Equal(CredentialRefusalResponse.AccountRequired, StageKeyOf(result));
    }

    /// <summary>
    /// The endpoint carries [Authorize], which is what answers a caller holding no session at all, and
    /// it is not [AllowAnonymous]. That is the difference between a 401 and the 400 criterion 47 names.
    /// [47]
    /// </summary>
    [Fact]
    public void ChangingThePasswordRequiresAuthorization()
    {
        var method = typeof(AuthController).GetMethod(nameof(AuthController.ChangePasswordAsync))!;

        Assert.Empty(method.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        Assert.NotEmpty(
            typeof(AuthController).GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>());
    }

    /// <summary>
    /// A replacement password has to pass the rules an account is created under, or a password that
    /// could not be chosen at sign-up could be arrived at by changing to it. [26]
    /// </summary>
    [Fact]
    public async Task AWeakReplacementPasswordIsRefused()
    {
        await using var database = await TestDatabase.CreateAsync();
        await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);
        await SignInAsync(sign, "operator");

        var result = await sign.Controller.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = Password, NewPassword = "short" });

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Equal(CredentialRefusalResponse.PasswordRejected, StageKeyOf(result));

        var login = NewSignIn(database);
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await login.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));
    }

    // --- The audit trail ----------------------------------------------------------------------

    /// <summary>
    /// A sign-in is recorded, with the account that made it and the session it produced. [49h]
    /// </summary>
    [Fact]
    public async Task ASuccessfulSignInIsRecorded()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()));

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries
            .SingleAsync(e => e.Event == IdentityAuditEvent.LoginSucceeded);
        Assert.Equal(account.Id, entry.PerformedByAccountId);
        Assert.Equal(account.Id, entry.TargetAccountId);
        Assert.Equal((await context.UserSessions.SingleAsync()).Id, entry.PerformedBySessionId);
        Assert.NotEqual(default, entry.PerformedAtUtc);
    }

    /// <summary>
    /// A refused sign-in is recorded too, and the actor columns stay empty when the caller has no
    /// account and no session to name. A row that could not be written for an anonymous caller would be
    /// no record of the attempts that matter most. [49h][29c]
    /// </summary>
    [Fact]
    public async Task ARefusedSignInIsRecordedWithWhateverActorThereWas()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        var sign = NewSignIn(database);

        await sign.Controller.LoginAsync(NewLogin("operator", "wrong-password", _apiKeyService.GetApiKey()));
        await sign.Controller.LoginAsync(NewLogin("nobody", Password, _apiKeyService.GetApiKey()));

        await using var context = database.Factory.CreateDbContext();
        var entries = await context.IdentityAuditEntries
            .Where(e => e.Event == IdentityAuditEvent.LoginFailed)
            .OrderBy(e => e.Id)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal(account.Id, entries[0].TargetAccountId);
        Assert.Null(entries[0].PerformedBySessionId);
        Assert.Null(entries[1].TargetAccountId);
        Assert.Null(entries[1].PerformedByAccountId);
    }

    /// <summary>
    /// An audit trail that can refuse a sign-in turns a logging fault into an outage. Dropping the
    /// table is what makes the write fail for real rather than being told it did. [29d]
    /// </summary>
    [Fact]
    public async Task ASignInStillSucceedsWhenTheAuditWriteFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var account = await NewAccountAsync(database, "operator");
        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"IdentityAuditEntries\"");
        }

        var sign = NewSignIn(database);

        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusOf(await sign.Controller.LoginAsync(
                NewLogin("operator", "wrong-password", _apiKeyService.GetApiKey()))));
        Assert.Equal(
            StatusCodes.Status200OK,
            StatusOf(await sign.Controller.LoginAsync(NewLogin("operator", Password, _apiKeyService.GetApiKey()))));

        await using var after = database.Factory.CreateDbContext();
        Assert.Equal(account.Id, (await after.UserSessions.SingleAsync()).AccountId);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // --- Building the pieces ------------------------------------------------------------------

    private static PasswordHasher<UserAccount> NewHasher(int iterationCount) =>
        new PasswordHasher<UserAccount>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterationCount
        }));

    /// <summary>
    /// One controller and the request it answers, plus the two pieces of state a test has to reach into
    /// afterwards: the session service that validates the tokens it hands out, and the lockout count it
    /// shares with any other controller built on the same instance.
    /// </summary>
    private sealed record SignIn(
        AuthController Controller,
        DefaultHttpContext Request,
        SessionService Sessions,
        AccountLockout Lockout);

    private SignIn NewSignIn(
        TestDatabase database,
        StateService? stateService = null,
        AccountLockout? lockout = null)
    {
        var sessions = new SessionService(
            database.Factory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            stateService!,
            NewNotifications(),
            _configuration);

        var request = new DefaultHttpContext();
        var sharedLockout = lockout ?? new AccountLockout(NullLogger<AccountLockout>.Instance);

        var controller = new AuthController(
            sessions,
            NullLogger<AuthController>.Instance,
            database.Factory,
            stateService!,
            NewNotifications(),
            _apiKeyService,
            _passwordHasher,
            sharedLockout,
            new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = request }
        };

        return new SignIn(controller, request, sessions, sharedLockout);
    }

    /// <summary>
    /// Signs in through the endpoint and publishes the resulting session the way the request handler
    /// does, so the change-password endpoint on the same controller finds it.
    /// </summary>
    private async Task<(string RawToken, UserSession Session)> SignInAsync(SignIn sign, string username)
    {
        var result = await sign.Controller.LoginAsync(NewLogin(username, Password, _apiKeyService.GetApiKey()));
        Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

        // The cookie is the only place the token is answered in, so it is where the caller's browser and
        // this helper both read it from.
        var rawToken = SessionTokenFromCookies(sign.Request);
        Assert.NotNull(rawToken);

        var session = await sign.Sessions.ValidateSessionAsync(rawToken!);
        Assert.NotNull(session);

        // The request handler publishes the session it resolved, which is where the change-password
        // endpoint on this controller reads it from. Clearing the sign-in's own Set-Cookie leaves the
        // header holding only what the next call writes.
        sign.Request.Items["Session"] = session;
        sign.Request.Response.Headers.Remove("Set-Cookie");
        return (rawToken!, session!);
    }

    private async Task<UserAccount> NewAccountAsync(
        TestDatabase database,
        string username,
        SessionType role = SessionType.Admin,
        bool disabled = false,
        IPasswordHasher<UserAccount>? hasher = null)
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            Role = role,
            IsDisabled = disabled,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = (hasher ?? _passwordHasher).HashPassword(account, Password);

        await using var context = database.Factory.CreateDbContext();
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private static async Task<string> StoredHashAsync(TestDatabase database, Guid accountId)
    {
        await using var context = database.Factory.CreateDbContext();
        return (await context.UserAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).PasswordHash;
    }

    private static LoginRequest NewLogin(string username, string password, string apiKey) =>
        new() { Username = username, Password = password, ApiKey = apiKey };

    /// <summary>
    /// The token out of the Set-Cookie header the response carries, which is where the caller's browser
    /// would read it from.
    /// </summary>
    private static string? SessionTokenFromCookies(DefaultHttpContext request)
    {
        var cookie = request.Response.Headers.SetCookie
            .FirstOrDefault(value => value != null && value.StartsWith("LancacheManager.Session=", StringComparison.Ordinal));

        return cookie?.Split(';')[0]["LancacheManager.Session=".Length..];
    }

    private static int StatusOf<T>(ActionResult<T> result) =>
        Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode ?? 0;

    private static string StageKeyOf<T>(ActionResult<T> result) =>
        Assert.IsType<CredentialRefusalResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).StageKey;

    private static string ErrorOf<T>(ActionResult<T> result) =>
        Assert.IsType<CredentialRefusalResponse>(
            Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).Error;

    private static ISignalRNotificationService NewNotifications() =>
        DispatchProxy.Create<ISignalRNotificationService, SilentNotifications>();

    /// <summary>
    /// Answers every notification method with a completed task. These tests are about what is written
    /// and what is answered, not about what is broadcast.
    /// </summary>
    public class SilentNotifications : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }
}
