using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// The never-expires move for an older account session is read in two more places than the stored row:
/// the session the same request runs on (the sign-in status reads its expiry), and the guest sessions it
/// must leave alone, whose expiry is the guest session length the admin chose.
/// </summary>
// The shared session is process-wide (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountSessionExpiryBackfillReadersTests
{
    [Fact]
    public async Task AnOlderUserSession_RunsItsRequestAsNeverExpiring_AndAGuestKeepsItsLengthAsync()
    {
        await using var installation = await SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var account = SignInModeScene.Account(Guid.NewGuid(), "member", SessionType.User, mainAdmin: false);
        await installation.SeedAsync(account);
        var (_, fresh) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);
        var (olderToken, older) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);
        var guest = await installation.Sessions.CreateGuestSessionAsync(new DefaultHttpContext());
        Assert.True(guest is not null, "guest access is off in this installation, so the guest half proves nothing");
        var (guestToken, guestSession) = guest.GetValueOrDefault();

        // The older version wrote a 30-day expiry on the user's session. The guest's stored expiry is read
        // back rather than taken from the created row, because the database keeps microseconds only.
        DateTime guestExpiryBefore;
        await using (var context = await installation.Database.Factory.CreateDbContextAsync())
        {
            var row = await context.UserSessions.SingleAsync(session => session.Id == older.Id);
            row.ExpiresAtUtc = DateTime.UtcNow.AddDays(30);
            await context.SaveChangesAsync();
            guestExpiryBefore = await context.UserSessions
                .Where(session => session.Id == guestSession.Id)
                .Select(session => session.ExpiresAtUtc)
                .SingleAsync();
        }

        var validated = Assert.IsType<UserSession>(await installation.Sessions.ValidateSessionAsync(olderToken));
        Assert.True(
            validated.ExpiresAtUtc == fresh.ExpiresAtUtc,
            $"the request of an older user session runs with expiry {validated.ExpiresAtUtc:O}, not the {fresh.ExpiresAtUtc:O} its row now holds");

        Assert.NotNull(await installation.Sessions.ValidateSessionAsync(guestToken));
        await using var read = await installation.Database.Factory.CreateDbContextAsync();
        var guestExpiry = await read.UserSessions
            .Where(session => session.Id == guestSession.Id)
            .Select(session => session.ExpiresAtUtc)
            .SingleAsync();
        Assert.True(
            guestExpiry == guestExpiryBefore,
            $"a guest session's expiry moved from {guestExpiryBefore:O} to {guestExpiry:O} after its request");
    }
}
