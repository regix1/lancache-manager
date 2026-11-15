using System.Net;
using System.Net.Http.Json;
using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A user-role account changes only the sessions its own account holds, so turning on a guest's prefill
/// access is refused to it, even when the request is sent by hand, and the guest is left without the
/// access. An administrator turning it on for the same guest still succeeds.
/// </summary>
// The host boots the app on the suite's shared database and shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class GuestPrefillToggleRoleTests
{
    [Fact]
    public async Task AUserTurningOnAGuestsPrefill_IsRefusedAndAnAdminIsNotAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var adminClient = await host.CreateAdminClientAsync();
        var (username, _) = await host.NewAccountAsync();

        Guid guestSession;
        string userCookie;
        var contexts = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using (var scope = host.Application.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
            Assert.NotNull(guest);
            guestSession = guest!.Value.Session.Id;

            await using var context = await contexts.CreateDbContextAsync();
            var account = await context.UserAccounts.SingleAsync(candidate => candidate.Username == username);
            Assert.Equal(SessionType.User, account.Role);
            var user = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);
            var cookieWriter = new DefaultHttpContext();
            sessions.SetSessionCookie(cookieWriter, user.RawToken, user.Session.ExpiresAtUtc);
            userCookie = cookieWriter.Response.Headers.SetCookie.ToString().Split(';')[0];
        }

        // The client keeps the antiforgery cookie the priming read sets, as a browser does.
        using var userClient = host.Application.CreateClient();
        userClient.DefaultRequestHeaders.Add("Cookie", userCookie);
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(userClient);

        var route = $"/api/auth/guest/prefill/toggle/{guestSession}?service=steam";
        using var refused = await userClient.PostAsJsonAsync(route, new GuestPrefillToggleRequest { Enabled = true });
        DateTime? afterUser;
        await using (var context = await contexts.CreateDbContextAsync())
        {
            afterUser = (await context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == guestSession))
                .SteamPrefillExpiresAtUtc;
        }

        using var granted = await adminClient.PostAsJsonAsync(route, new GuestPrefillToggleRequest { Enabled = true });
        DateTime? afterAdmin;
        await using (var context = await contexts.CreateDbContextAsync())
        {
            afterAdmin = (await context.UserSessions.AsNoTracking().SingleAsync(s => s.Id == guestSession))
                .SteamPrefillExpiresAtUtc;
        }

        Assert.Equal(
            (HttpStatusCode.Forbidden, false, HttpStatusCode.OK, true),
            (refused.StatusCode, afterUser != null, granted.StatusCode, afterAdmin != null));
    }
}
