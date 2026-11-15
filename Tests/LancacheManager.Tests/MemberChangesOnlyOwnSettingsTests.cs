using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A member (an account with the User role) changes its own settings and nobody else's. The Users page
/// sends two writes that name another session, the preference save and the refresh-rate change, and the
/// server refuses both with 403 when a member names another account's session, even when the request
/// is sent by hand. The stored settings of that other account stay as they were.
/// </summary>
// The host boots the app on the suite's shared database and shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class MemberChangesOnlyOwnSettingsTests
{
    [Fact]
    public async Task AMemberChangingAnotherAccountsSettings_IsRefusedAndTheirSettingsStayAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var adminClient = await host.CreateAdminClientAsync();
        using var member = await SignInAsync(host, await host.NewAccountAsync());
        using var otherMember = await SignInAsync(host, await host.NewAccountAsync());
        var memberStatus = await member.GetFromJsonAsync<JsonElement>("/api/auth/status");
        var otherStatus = await otherMember.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.Equal("user", memberStatus.GetProperty("sessionType").GetString());
        Assert.Equal("user", otherStatus.GetProperty("sessionType").GetString());
        var otherSession = otherStatus.GetProperty("sessionId").GetGuid();
        var before = await adminClient.GetFromJsonAsync<JsonElement>($"/api/user-preferences/session/{otherSession}");
        var sharpCornersBefore = before.GetProperty("sharpCorners").GetBoolean();
        var refreshRateBefore = before.TryGetProperty("refreshRate", out var rate) ? rate.ToString() : null;

        var writes = new (string Route, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("PUT /api/user-preferences/session/(other member's session)",
                () => member.PutAsJsonAsync(
                    $"/api/user-preferences/session/{otherSession}",
                    new { sharpCorners = !sharpCornersBefore, showDatasourceLabels = true, refreshRate = "30s" })),
            ("PATCH /api/sessions/(other member's session)/refresh-rate",
                () => member.PatchAsJsonAsync(
                    $"/api/sessions/{otherSession}/refresh-rate",
                    new RefreshRateRequest { RefreshRate = "30s" }))
        };
        Assert.NotEmpty(writes);

        var wrong = new List<string>();
        foreach (var (route, send) in writes)
        {
            using var answer = await send();
            if (answer.StatusCode != HttpStatusCode.Forbidden)
                wrong.Add($"{route} from a member answered {(int)answer.StatusCode}, not 403");
        }

        var after = await adminClient.GetFromJsonAsync<JsonElement>($"/api/user-preferences/session/{otherSession}");
        if (after.GetProperty("sharpCorners").GetBoolean() != sharpCornersBefore)
            wrong.Add("the other member's sharp-corners setting was changed by the member");
        var refreshRateAfter = after.TryGetProperty("refreshRate", out var changedRate) ? changedRate.ToString() : null;
        if (refreshRateAfter != refreshRateBefore)
            wrong.Add($"the other member's refresh rate was changed by the member ({refreshRateBefore} to {refreshRateAfter})");
        Assert.True(wrong.Count == 0, "a member changed another account's settings: " + string.Join("; ", wrong));
    }

    /// <summary>A client signed in with the account's password, the way the sign-in form does it.</summary>
    private static async Task<HttpClient> SignInAsync(EndpointAuthorizationHost host, (string Username, string Password) account)
    {
        var client = host.Application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        try
        {
            await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
            using var signIn = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest
                {
                    ApiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey(),
                    Username = account.Username,
                    Password = account.Password
                });
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            // The antiforgery token is tied to the caller, which has just changed.
            await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
