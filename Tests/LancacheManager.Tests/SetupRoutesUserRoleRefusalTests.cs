using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// The embedded PostgreSQL role password and the database the installation connects to belong to
/// the whole installation, so a signed-in user-role account is refused both writes, as it is every
/// other installation change, while an admin session still reaches them. Each request sent here is
/// one the handler turns down on its input once its caller check lets it through, so a request that
/// gets past that check changes no database role and writes no file.
/// </summary>
// The host boots the app on the suite's shared database and shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SetupRoutesUserRoleRefusalTests
{
    [Fact]
    public async Task AUserRoleAccountCannotChangeTheDatabasePasswordOrConnectionAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var adminClient = await host.CreateAdminClientAsync();
        using var member = await SignInAsync(host, await host.NewAccountAsync());

        // A password the strength rule refuses, and a connection request the handler refuses because
        // this installation is not in external mode (or, in external mode, for that same password).
        var requests = new (string Route, object Body)[]
        {
            ("/api/setup/credentials", new SetupCredentialsRequest { Password = "short" }),
            ("/api/setup/external", new SetExternalDbCredentialsRequest { Host = "db.example.test", Password = "short" })
        };

        var wrong = new List<string>();
        foreach (var (route, body) in requests)
        {
            using var asMember = await member.PostAsJsonAsync(route, body);
            if (asMember.StatusCode != HttpStatusCode.Forbidden)
                wrong.Add($"POST {route} let a user-role account through to the handler, which answered {(int)asMember.StatusCode} {await asMember.Content.ReadAsStringAsync()}, not the 403 refusal");

            using var asAdmin = await adminClient.PostAsJsonAsync(route, body);
            if (asAdmin.StatusCode != HttpStatusCode.BadRequest)
                wrong.Add($"POST {route} answered an admin session {(int)asAdmin.StatusCode} {await asAdmin.Content.ReadAsStringAsync()}, so the admin no longer reaches the handler's own check");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// The password sign-in the user-role tests already use, private to
    /// InstallSettingsUserRoleRefusalTests.cs:92, run through reflection rather than copied.
    /// </summary>
    private static Task<HttpClient> SignInAsync(EndpointAuthorizationHost host, (string Username, string Password) account)
        => (Task<HttpClient>)typeof(InstallSettingsUserRoleRefusalTests)
            .GetMethod("SignInAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [host, account])!;
}
