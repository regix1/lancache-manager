using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A session cookie alone is not enough to change anything; the caller also has to send back a token it
/// could only have got by reading a cookie from this origin.
///
/// The session cookie is SameSite=Lax, and Lax still sends a cookie less than about two minutes old on a
/// cross-site POST. Every status call rotates the session token and writes the cookie again, which
/// resets its age, so on a session somebody is actually using that window never closes on its own. The
/// token is what closes it: a page served by another origin cannot read this origin's cookies, so it
/// cannot produce the header, so its forged request is refused.
/// </summary>
// The host resolves the process-wide shared sessions, so this runs in the collection that already
// serializes every class touching them.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AntiforgeryTokenTests
{
    private const string SessionCookieName = "LancacheManager.Session";

    /// <summary>
    /// A request that changes something is refused without the token and answered with it, on each of the
    /// four verbs the check applies to. Both halves run against the same routes so the token is the only
    /// difference between them.
    /// </summary>
    [Fact]
    public async Task TheVerbsThatChangeSomethingAreRefusedWithoutTheTokenAndAnsweredWithIt()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = await host.CreateAdminClientAsync();

        // The group the edit and the delete are driven against, so neither can be read as refused when it
        // was only ever missing.
        using var seeded = await client.PostAsJsonAsync("/api/client-groups", NewGroup());
        Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);
        var groupId = (await seeded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var token = client.DefaultRequestHeaders.GetValues(AntiforgeryToken.HeaderName).Single();
        client.DefaultRequestHeaders.Remove(AntiforgeryToken.HeaderName);

        using var refusedPost = await client.PostAsJsonAsync("/api/client-groups", NewGroup());
        using var refusedPut = await client.PutAsJsonAsync($"/api/client-groups/{groupId}", NewRename());
        using var refusedPatch = await client.PatchAsJsonAsync("/api/user-preferences/selectedtheme", "dark");
        using var refusedDelete = await client.DeleteAsync($"/api/client-groups/{groupId}");

        Assert.Equal(HttpStatusCode.BadRequest, refusedPost.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refusedPut.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refusedPatch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refusedDelete.StatusCode);

        client.DefaultRequestHeaders.Add(AntiforgeryToken.HeaderName, token);

        using var post = await client.PostAsJsonAsync("/api/client-groups", NewGroup());
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        using var put = await client.PutAsJsonAsync($"/api/client-groups/{groupId}", NewRename());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var patch = await client.PatchAsJsonAsync("/api/user-preferences/selectedtheme", "dark");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var delete = await client.DeleteAsync($"/api/client-groups/{groupId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    /// <summary>
    /// A read never needs the token, so the protection costs nothing on the calls that make up most of
    /// the traffic and cannot be what breaks a page that only displays things.
    ///
    /// GET is the only read verb this can measure. No controller in the application declares a HEAD or
    /// an OPTIONS action, so a request on either verb is answered 404 by the fallback route before any
    /// MVC filter runs, and an assertion about it would pass whether or not the token check skips
    /// those verbs. The skip itself is still there in AntiforgeryFilter, which is where it belongs.
    /// </summary>
    [Fact]
    public async Task TheReadVerbsAreAnsweredWithNoTokenAtAll()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = await host.CreateAdminClientAsync();
        client.DefaultRequestHeaders.Remove(AntiforgeryToken.HeaderName);

        using var get = await client.GetAsync("/api/client-groups");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    /// <summary>
    /// The rotation this whole phase exists for. Reading the status hands the caller a new session cookie,
    /// which resets the age SameSite=Lax measures, so the cookie is as fresh as it can be and the browser
    /// would send it cross-site. It still buys nothing: the very next request that changes something is
    /// refused for want of the token.
    /// </summary>
    [Fact]
    public async Task ARotatedSessionCookieStillDoesNotBuyARequestWithoutTheToken()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        var signedIn = await SignedInClientAsync(host);
        using var client = signedIn.Client;

        // Reading the status rotates the session token and writes the cookie again. That re-issue is
        // what resets the cookie's age, and a cookie less than about two minutes old is one Lax still
        // sends on a cross-site POST, so on a session anybody is using the age never grows past the
        // carve-out and the cookie by itself can never be what refuses a forged request.
        using var status = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.NotEqual(signedIn.SessionCookie, SessionCookieFrom(status));

        // Straight after that rotation, holding the freshest cookie there is and no token.
        client.DefaultRequestHeaders.Remove(AntiforgeryToken.HeaderName);

        using var refused = await client.PostAsJsonAsync("/api/client-groups", NewGroup());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    /// <summary>
    /// The five routes that prove the caller with the installation's key answer without a token, because
    /// there is nothing there to forge: a page on another origin can neither read the key nor set a
    /// header. They are also the bootstrap and the way back in, so a token requirement would break them in
    /// exactly the states where nothing else works.
    ///
    /// Each is sent a wrong key and no token. The answer is the endpoint's own refusal of the key, which
    /// is only reachable once the request has got past the token check - that check answers 400 and never
    /// runs the handler, which is what the last two requests here show.
    /// </summary>
    [Fact]
    public async Task TheKeyAuthenticatedRoutesAnswerWithoutATokenAndTheOthersDoNot()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = host.Application.CreateClient();

        var credentials = new AccountCredentialsRequest
        {
            Username = "antiforgery-exempt",
            Password = "Antiforgery-Exempt-1",
            ApiKey = "not-the-key"
        };

        using var firstAdmin = await client.PostAsJsonAsync("/api/account-setup/first-admin", credentials);
        using var openRecovery = await client.PostAsJsonAsync(
            "/api/account-setup/open-main-admin-recovery",
            new RecoveryWindowRequest { ApiKey = "not-the-key" });
        using var recovery = await client.PostAsJsonAsync("/api/account-setup/recover-main-admin", credentials);
        using var repairPassword = await client.PostAsJsonAsync("/api/setup/credentials", new SetupCredentialsRequest());
        using var repairExternal = await client.PostAsJsonAsync("/api/setup/external", new SetExternalDbCredentialsRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, firstAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, openRecovery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, recovery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, repairPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, repairExternal.StatusCode);

        // The same client and the same absent token against a route that is not exempt. This is what the
        // five above would answer if the exemption were dropped, and the second call is what shows the
        // 400 is the token check rather than anything the guest endpoint decided.
        using var refusedGuest = await client.PostAsync("/api/auth/guest", null);
        Assert.Equal(HttpStatusCode.BadRequest, refusedGuest.StatusCode);

        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
        using var guest = await client.PostAsync("/api/auth/guest", null);
        Assert.NotEqual(HttpStatusCode.BadRequest, guest.StatusCode);
    }

    /// <summary>
    /// The repair endpoint with the real key, no session and no token: the bootstrap and repair path,
    /// which has to work in the state where there is no database to hold a session and nothing to
    /// issue a token. What comes back is the endpoint's own refusal of the request it was sent, which
    /// only a caller that got all the way to the handler can be given.
    /// </summary>
    [Fact]
    public async Task TheRepairEndpointAnswersTheKeyCallerThatHasNoSessionAndNoToken()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = host.Application.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Api-Key",
            host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey());

        using var response = await client.PostAsJsonAsync(
            "/api/setup/external",
            new SetExternalDbCredentialsRequest());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(refusal);
        Assert.True(IsTheEndpointsOwnRefusal(refusal.Error), refusal.Error);
    }

    /// <summary>
    /// Both setup routes reached with a session and no key. That caller is holding nothing but cookies,
    /// which a browser attaches to a request another origin caused, so it is asked for the token like
    /// every other write - and /api/setup/credentials runs ALTER USER against a role created WITH
    /// SUPERUSER, so a session on its own must never be enough. The last part is the same caller with
    /// the token it read from this origin's cookie, which gets the handler's own answer.
    /// </summary>
    [Fact]
    public async Task TheRepairEndpointRefusesASessionThatHasNoKeyAndNoToken()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = await host.CreateAdminClientAsync();
        var token = client.DefaultRequestHeaders.GetValues(AntiforgeryToken.HeaderName).Single();
        client.DefaultRequestHeaders.Remove(AntiforgeryToken.HeaderName);

        using var refused = await client.PostAsJsonAsync(
            "/api/setup/external",
            new SetExternalDbCredentialsRequest());

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var refusal = await refused.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(refusal);
        Assert.Equal(AntiforgeryToken.MissingTokenMessage, refusal.Error);

        using var refusedCredentials = await client.PostAsJsonAsync(
            "/api/setup/credentials",
            new SetupCredentialsRequest());

        Assert.Equal(HttpStatusCode.BadRequest, refusedCredentials.StatusCode);
        var credentialsRefusal = await refusedCredentials.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(credentialsRefusal);
        Assert.Equal(AntiforgeryToken.MissingTokenMessage, credentialsRefusal.Error);

        client.DefaultRequestHeaders.Add(AntiforgeryToken.HeaderName, token);

        using var answered = await client.PostAsJsonAsync(
            "/api/setup/external",
            new SetExternalDbCredentialsRequest());

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        var handled = await answered.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(handled);
        Assert.True(IsTheEndpointsOwnRefusal(handled.Error), handled.Error);
    }

    /// <summary>
    /// A guest holding the token this origin issued it, at both setup repair routes. That is three
    /// requests a visitor can make on a finished install without signing in, and /api/setup/credentials
    /// runs ALTER USER against a role created WITH SUPERUSER with the role name taken from the request,
    /// so the token must not be the last thing standing between a guest and that statement. The account
    /// holder in the same shape still reaches the handler, which is what makes this a narrowing rather
    /// than a closure.
    /// </summary>
    [Fact]
    public async Task TheRepairEndpointsRefuseAGuestHoldingTheTokenAndStillAnswerAnAccountHolder()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var guestClient = await GuestClientAsync(host);

        using var guestCredentials = await guestClient.PostAsJsonAsync(
            "/api/setup/credentials",
            new SetupCredentialsRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, guestCredentials.StatusCode);

        using var guestExternal = await guestClient.PostAsJsonAsync(
            "/api/setup/external",
            new SetExternalDbCredentialsRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, guestExternal.StatusCode);

        using var accountHolder = await host.CreateAdminClientAsync();

        // The handler's own answer, which only a caller admitted past the gate can be given.
        using var answered = await accountHolder.PostAsJsonAsync(
            "/api/setup/credentials",
            new SetupCredentialsRequest());
        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
    }

    /// <summary>
    /// A client carrying a real guest session and the token this origin issued it, which is everything a
    /// visitor can hold without signing in. The session is minted through the service the guest sign-in
    /// endpoint calls rather than through the endpoint, because that endpoint refuses while setup is
    /// unfinished and every host in this suite is an unfinished install.
    /// </summary>
    private static async Task<HttpClient> GuestClientAsync(EndpointAuthorizationHost host)
    {
        var client = host.Application.CreateClient();

        try
        {
            using var scope = host.Application.Services.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
            Assert.NotNull(guest);

            // Round-tripping the cookie through the writer keeps the cookie name out of this helper.
            var cookieWriter = new DefaultHttpContext();
            sessions.SetSessionCookie(cookieWriter, guest!.Value.RawToken, DateTime.UtcNow.AddDays(1));
            client.DefaultRequestHeaders.Add(
                "Cookie",
                cookieWriter.Response.Headers.SetCookie.ToString().Split(';')[0]);

            using var status = await client.GetAsync("/api/auth/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            client.DefaultRequestHeaders.Add(
                AntiforgeryToken.HeaderName,
                EndpointAuthorizationHost.AntiforgeryTokenFrom(status));

            // A caller with no session at all is refused by the same two answers this client is used to
            // measure, so the session has to be shown to be on the request before either means anything.
            var reported = await status.Content.ReadFromJsonAsync<AuthStatusResponse>();
            Assert.Equal(SessionType.Guest, reported?.SessionType);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether an answer from /api/setup/external came from the endpoint itself rather than the token
    /// check in front of it. Which of the two refusals it gives depends on POSTGRES_MODE: the host
    /// sets it to external, and an empty request then stops at the first missing field, while
    /// ExternalDatabaseSetupGateTests sets and restores the same process-wide variable around its own
    /// calls and can leave the mode check answering instead. Both mean the request reached the
    /// handler, which is the whole of what these two tests are measuring.
    /// </summary>
    private static bool IsTheEndpointsOwnRefusal(string error) =>
        error == "Host is required"
            || error.StartsWith("External-mode endpoint called", StringComparison.Ordinal);

    /// <summary>
    /// With authentication off the installation has no access control at all, so the token check must not
    /// become the one thing still turning callers away.
    /// </summary>
    [Fact]
    public async Task ARequestThatChangesSomethingIsAnsweredWhenAuthenticationIsOff()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = host.Application.CreateClient();
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);

        using var created = await client.PostAsJsonAsync("/api/client-groups", NewGroup());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    /// <summary>
    /// The token's cookie is readable by script and the session cookie is not, which looks the wrong way
    /// round and is not. Script has to read the token to put it in a header, and being able to read it is
    /// no help to another origin, which cannot read this origin's cookies at all. The session cookie is
    /// the credential and stays out of reach.
    /// </summary>
    [Fact]
    public async Task TheTokenCookieIsReadableByScriptAndTheSessionCookieIsNot()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        // The status call has to be the one that rotates, because that is the response carrying both
        // cookies at once, and the server skips a rotation for thirty seconds after the previous one.
        var signedIn = await SignedInClientAsync(host);
        using var client = signedIn.Client;

        using var status = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var issued = status.Headers.GetValues("Set-Cookie").ToArray();
        var tokenCookie = Assert.Single(
            issued,
            cookie => cookie.StartsWith($"{AntiforgeryToken.CookieName}=", StringComparison.Ordinal));
        var sessionCookie = Assert.Single(
            issued,
            cookie => cookie.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal));

        Assert.DoesNotContain("httponly", tokenCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A client signed in the way the page signs in, handed back with the session cookie the sign-in
    /// gave it and without the status call that would normally follow. The shared admin-client helper
    /// makes that call itself, and the server skips a rotation for thirty seconds after the previous
    /// one (SessionService.RotateSessionTokenAsync), so a test that has to watch a rotation happen has
    /// to be the one that spends it.
    /// </summary>
    private static async Task<(HttpClient Client, string SessionCookie)> SignedInClientAsync(
        EndpointAuthorizationHost host)
    {
        var client = host.Application.CreateClient();

        try
        {
            var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();
            var (username, password) = await host.NewAccountAsync();

            await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
            using var login = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest { ApiKey = apiKey, Username = username, Password = password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            return (client, SessionCookieFrom(login));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The session cookie a response hands back.
    /// </summary>
    private static string SessionCookieFrom(HttpResponseMessage response)
    {
        var prefix = $"{SessionCookieName}=";
        var cookie = response.Headers.GetValues("Set-Cookie")
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));

        Assert.NotNull(cookie);
        return cookie![prefix.Length..].Split(';')[0];
    }

    private static CreateClientGroupRequest NewGroup() =>
        new() { Nickname = $"antiforgery-{Guid.NewGuid():N}" };

    private static UpdateClientGroupRequest NewRename() =>
        new() { Nickname = $"antiforgery-{Guid.NewGuid():N}", SeparateMemberRows = false };
}
