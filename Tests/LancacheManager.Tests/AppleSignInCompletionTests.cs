using System.Net;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace LancacheManager.Tests;

[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AppleSignInCompletionTests
{
    private const string SessionCookieName = "LancacheManager.Session";
    private const string AppleCookieName = "lcm_apple";

    // Another site sends the browser to the Apple completion route, with no Apple sign-in behind it: no
    // Apple cookie at all, or one that is not a ticket this app protected. The route signs nobody in,
    // ends nothing and gives the browser no session cookie.
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-protected-ticket")]
    public async Task CompletionWithoutAnAppleTicket_ChangesNoSessionAndSetsNoSessionCookie(string? appleCookie)
    {
        using var host = new EndpointAuthorizationHost();
        using var scope = host.Application.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = $"held-{Guid.NewGuid():N}",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.Add(account);
            await context.SaveChangesAsync();
        }
        var heldToken = (await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account)).RawToken;
        var held = Assert.IsType<UserSession>(await sessions.ValidateSessionAsync(heldToken));

        async Task<int> LiveSessionsAsync()
        {
            await using var context = await contexts.CreateDbContextAsync();
            return await context.UserSessions.CountAsync(session => !session.IsRevoked);
        }
        var liveBefore = await LiveSessionsAsync();

        using var client = host.Application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://lancache.example.test")
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/login/apple/complete");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add(
            "Cookie",
            appleCookie is null
                ? $"{SessionCookieName}={heldToken}"
                : $"{SessionCookieName}={heldToken}; {AppleCookieName}={appleCookie}");
        using var response = await client.SendAsync(request);

        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? SetCookieHeaderValue.ParseList(values.ToList())
            : [];
        var what = appleCookie is null ? "no Apple cookie" : "an Apple cookie that is not a protected ticket";
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?oidcError=state", response.Headers.Location?.OriginalString);
        Assert.All(setCookies, cookie => Assert.True(
            cookie.Name.Value == AppleCookieName && cookie.Expires < DateTimeOffset.UtcNow,
            $"a cross-site GET to the Apple completion route with {what} set the cookie '{cookie.Name.Value}'; the only cookie it may write is the expired deletion of {AppleCookieName}"));
        Assert.Equal(held.Id, (await sessions.ValidateSessionAsync(heldToken))?.Id);
        Assert.Equal(liveBefore, await LiveSessionsAsync());
    }
}
