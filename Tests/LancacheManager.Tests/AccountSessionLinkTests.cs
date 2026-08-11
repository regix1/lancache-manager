using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the link between a session and the account it signed in as. The session carries its own copy of
/// the role, so a role change, a disable or a delete that left the session alone would keep the old role
/// working until the person logged out.
///
/// Nothing writes UserSession.AccountId yet - the login that will is Phase 4's - so these tests set the
/// column directly, the way the login path will.
/// </summary>
public sealed class AccountSessionLinkTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;

    public AccountSessionLinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-account-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            })
            .Build();
        _apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            _configuration,
            pathResolver: null!);
    }

    /// <summary>
    /// The whole point of the column: an account's live sessions can be found and revoked. Every account
    /// write that changes an identity - the role, the disabled flag, the delete - goes through this.
    /// </summary>
    [Fact]
    public async Task RevokingAnAccountsSessions_RevokesThatAccountsAndNoOthers()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var demoted = await NewAccountAsync(database, "demoted");
        var other = await NewAccountAsync(database, "other");

        var first = await NewAccountSessionAsync(database, service, demoted.Id);
        var second = await NewAccountSessionAsync(database, service, demoted.Id);
        var untouched = await NewAccountSessionAsync(database, service, other.Id);
        var accountLess = await service.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(accountLess);

        Assert.Equal(2, await service.RevokeAccountSessionsAsync(demoted.Id));

        await using var context = database.Factory.CreateDbContext();
        foreach (var revokedId in new[] { first.Session.Id, second.Session.Id })
        {
            var stored = await context.UserSessions.SingleAsync(s => s.Id == revokedId);
            Assert.True(stored.IsRevoked);
            Assert.NotNull(stored.RevokedAtUtc);
        }

        Assert.False((await context.UserSessions.SingleAsync(s => s.Id == untouched.Session.Id)).IsRevoked);
        Assert.False((await context.UserSessions.SingleAsync(s => s.Id == accountLess!.Value.Session.Id)).IsRevoked);
    }

    /// <summary>
    /// The criterion is about what the next request carries, not about a column. A revoked session stops
    /// validating, so the request after the change arrives with no session and no role at all.
    /// </summary>
    [Fact]
    public async Task AfterTheAccountsSessionsAreRevoked_ItsTokenNoLongerValidates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var account = await NewAccountAsync(database, "demoted");
        var signedIn = await NewAccountSessionAsync(database, service, account.Id);
        Assert.NotNull(await service.ValidateSessionAsync(signedIn.RawToken));

        await service.RevokeAccountSessionsAsync(account.Id);

        Assert.Null(await service.ValidateSessionAsync(signedIn.RawToken));
    }

    /// <summary>
    /// Revoking an account's sessions and deleting the account row are separate writes, so a fault between
    /// them leaves a live session pointing at an account that is gone. It has to be rejected rather than
    /// carrying on with the role the deleted account had.
    /// </summary>
    [Fact]
    public async Task SessionWhoseAccountRowWasDeleted_IsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var account = await NewAccountAsync(database, "deleted");
        var signedIn = await NewAccountSessionAsync(database, service, account.Id);
        Assert.NotNull(await service.ValidateSessionAsync(signedIn.RawToken));

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Remove(await context.UserAccounts.SingleAsync(a => a.Id == account.Id));
            await context.SaveChangesAsync();
        }

        Assert.Null(await service.ValidateSessionAsync(signedIn.RawToken));
    }

    /// <summary>
    /// The dangerous failure here is a silent downgrade rather than a rejection: a deleted
    /// admin who keeps a working guest session has been logged in, not logged out. Nothing on the reject
    /// path may rewrite the session or mint a replacement.
    /// </summary>
    [Fact]
    public async Task SessionWhoseAccountRowWasDeleted_DoesNotBecomeAGuest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var account = await NewAccountAsync(database, "deleted");
        var signedIn = await NewAccountSessionAsync(database, service, account.Id);

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Remove(await context.UserAccounts.SingleAsync(a => a.Id == account.Id));
            await context.SaveChangesAsync();
        }

        Assert.Null(await service.ValidateSessionAsync(signedIn.RawToken));

        await using var after = database.Factory.CreateDbContext();
        Assert.Equal(0, await after.UserSessions.CountAsync(s => s.SessionType == SessionType.Guest));
        var stored = await after.UserSessions.SingleAsync(s => s.Id == signedIn.Session.Id);
        Assert.Equal(SessionType.Admin, stored.SessionType);
        Assert.Equal(account.Id, stored.AccountId);
    }

    /// <summary>
    /// A disable is expected to revoke the account's sessions, and RevokeAccountSessionsAsync is there to
    /// do it, but nothing outside these tests calls it yet. Validation reads the flag off the account row
    /// as well, so a session belonging to a disabled account stops working on its next request whether or
    /// not the disable remembered to revoke it.
    /// </summary>
    [Fact]
    public async Task SessionWhoseAccountWasDisabled_IsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var account = await NewAccountAsync(database, "disabled");
        var signedIn = await NewAccountSessionAsync(database, service, account.Id);
        Assert.NotNull(await service.ValidateSessionAsync(signedIn.RawToken));

        await using (var context = database.Factory.CreateDbContext())
        {
            var stored = await context.UserAccounts.SingleAsync(a => a.Id == account.Id);
            stored.IsDisabled = true;
            await context.SaveChangesAsync();
        }

        Assert.Null(await service.ValidateSessionAsync(signedIn.RawToken));
    }

    /// <summary>
    /// The other half of the flag. An account that is present and not disabled has to keep validating, or
    /// the rejection above would be reading the row rather than the flag.
    /// </summary>
    [Fact]
    public async Task SessionWhoseAccountIsEnabled_StillValidates()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var account = await NewAccountAsync(database, "enabled");
        var signedIn = await NewAccountSessionAsync(database, service, account.Id);

        var validated = await service.ValidateSessionAsync(signedIn.RawToken);

        Assert.NotNull(validated);
        Assert.Equal(account.Id, validated!.AccountId);
    }

    /// <summary>
    /// The regression the column could cause. A guest has no account, and the admin row builder that both
    /// shared sessions go through - the X-Api-Key one and the authentication-disabled one - leaves the
    /// column null too, so all three have to be created and validated exactly as before.
    /// </summary>
    [Fact]
    public async Task GuestSession_IsStillCreatedAndValidatedWithNoAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, CreateStateService(_root));

        var created = await service.CreateGuestSessionAsync(new DefaultHttpContext());

        Assert.NotNull(created);
        Assert.Null(created!.Value.Session.AccountId);
        var validated = await service.ValidateSessionAsync(created.Value.RawToken);
        Assert.NotNull(validated);
        Assert.Equal(SessionType.Guest, validated!.SessionType);
        Assert.Null(validated.AccountId);
    }

    /// <summary>
    /// Companion to the guest case for the two shared admin sessions. Both are written by the single row
    /// builder this method reaches, so this covers the row they get and the validation they go through
    /// without touching the process-wide caches their own tests depend on.
    /// </summary>
    [Fact]
    public async Task AdminSession_IsStillCreatedAndValidatedWithNoAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var created = await service.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), new DefaultHttpContext());

        Assert.NotNull(created);
        Assert.Null(created!.Value.Session.AccountId);
        var validated = await service.ValidateSessionAsync(created.Value.RawToken);
        Assert.NotNull(validated);
        Assert.Equal(SessionType.Admin, validated!.SessionType);
        Assert.Null(validated.AccountId);
    }

    private async Task<UserAccount> NewAccountAsync(TestDatabase database, string username)
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "hash",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };

        await using var context = database.Factory.CreateDbContext();
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    /// <summary>
    /// Stands in for the login Phase 4 will add: an admin session written the ordinary way, then stamped
    /// with the account it belongs to.
    /// </summary>
    private async Task<(string RawToken, UserSession Session)> NewAccountSessionAsync(
        TestDatabase database, SessionService service, Guid accountId)
    {
        var created = await service.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);

        await using var context = database.Factory.CreateDbContext();
        var stored = await context.UserSessions.SingleAsync(s => s.Id == created!.Value.Session.Id);
        stored.AccountId = accountId;
        await context.SaveChangesAsync();

        return created!.Value;
    }

    private SessionService NewSessionService(
        IDbContextFactory<AppDbContext> dbContextFactory, StateService? stateService = null) =>
        new(
            dbContextFactory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            stateService!,
            signalR: null!,
            _configuration);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
