using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// The first request an older account session makes moves its 30-day expiry to never-expires. When
/// the database answers reads but refuses that one write, the request is still the account's own:
/// the cookie signs it in as that session, and the move happens on a later request once the write
/// goes through again.
/// </summary>
// The shared session is process-wide (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountSessionBackfillWriteRefusedTests
{
    [Fact]
    public async Task AUserRoleSessionStaysSignedInWhenItsExpiryCannotBeMovedAsync()
    {
        await using var installation = await SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var account = SignInModeScene.Account(Guid.NewGuid(), "member", SessionType.User, mainAdmin: false);
        await installation.SeedAsync(account);
        var (_, fresh) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);
        var neverExpires = fresh.ExpiresAtUtc;
        var (olderToken, older) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);

        // The older version wrote a 30-day expiry on this session.
        await using (var context = await installation.Database.Factory.CreateDbContextAsync())
        {
            var row = await context.UserSessions.SingleAsync(session => session.Id == older.Id);
            row.ExpiresAtUtc = DateTime.UtcNow.AddDays(30);
            await context.SaveChangesAsync();
        }

        // From here the database serves every read and refuses any write that changes a session's expiry.
        await using (var context = await installation.Database.Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION refuse_session_expiry_write() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'session expiry writes are refused';
                END
                $$;
                CREATE TRIGGER refuse_session_expiry_write BEFORE UPDATE OF "ExpiresAtUtc" ON "UserSessions"
                    FOR EACH ROW EXECUTE FUNCTION refuse_session_expiry_write();
                """);
        }

        var (refused, refusedRequest) = await installation.AuthenticateAsync($"LancacheManager.Session={olderToken}");
        var signedInAs = refusedRequest.GetUserSession();
        Assert.True(
            refused.Succeeded && signedInAs?.Id == older.Id,
            $"with the expiry write refused, the user-role account's request was answered as signed out (succeeded {refused.Succeeded}, session {signedInAs?.Id}, the account's session is {older.Id})");

        // The write goes through again, and the next request moves the expiry.
        await using (var context = await installation.Database.Factory.CreateDbContextAsync())
        {
            await context.Database.ExecuteSqlRawAsync("""DROP TRIGGER refuse_session_expiry_write ON "UserSessions";""");
        }

        var (next, _) = await installation.AuthenticateAsync($"LancacheManager.Session={olderToken}");
        Assert.True(next.Succeeded, "the request after the write recovered was answered as signed out");
        await using var read = await installation.Database.Factory.CreateDbContextAsync();
        var stored = await read.UserSessions.Where(session => session.Id == older.Id).Select(session => session.ExpiresAtUtc).SingleAsync();
        Assert.True(
            stored == neverExpires,
            $"after the write recovered the session still expires at {stored:O}, not at {neverExpires:O} as a new session of the account does");
    }
}
