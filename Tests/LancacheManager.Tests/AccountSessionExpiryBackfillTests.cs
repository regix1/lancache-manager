using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// An account session now never expires, and the Users page shows "never expires" for it. A session
/// saved by an older version still carries a 30-day expiry, so its row counts down. The first request
/// that session makes moves it to the same never-expires date a new session of that account gets, for
/// a user-role account as for an admin.
/// </summary>
// The shared session is process-wide (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountSessionExpiryBackfillTests
{
    [Theory]
    [InlineData(SessionType.Admin)]
    [InlineData(SessionType.User)]
    public async Task AnOlderAccountSessionNeverExpiresAfterItsNextRequestAsync(SessionType role)
    {
        await using var installation = await SignInModeScene.CreateAsync(new Dictionary<string, string?>(), access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var account = SignInModeScene.Account(Guid.NewGuid(), "member", role, mainAdmin: role == SessionType.Admin);
        await installation.SeedAsync(account);
        var (_, fresh) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);
        var neverExpires = fresh.ExpiresAtUtc;
        var (olderToken, older) = await installation.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);

        // The older version wrote a 30-day expiry on this session.
        var thirtyDays = DateTime.UtcNow.AddDays(30);
        await using (var context = await installation.Database.Factory.CreateDbContextAsync())
        {
            var row = await context.UserSessions.SingleAsync(session => session.Id == older.Id);
            row.ExpiresAtUtc = thirtyDays;
            await context.SaveChangesAsync();
        }

        // The browser holding that session makes a request.
        var validated = await installation.Sessions.ValidateSessionAsync(olderToken);
        Assert.True(validated is not null, "the older session was refused, so the test proves nothing");

        await using var read = await installation.Database.Factory.CreateDbContextAsync();
        var stored = await read.UserSessions.Where(session => session.Id == older.Id).Select(session => session.ExpiresAtUtc).SingleAsync();
        Assert.True(
            stored == neverExpires,
            $"a {role} session saved with a 30-day expiry still expires at {stored:O} after its request, not at {neverExpires:O} as a new {role} session does, so its row keeps counting down");
    }
}
