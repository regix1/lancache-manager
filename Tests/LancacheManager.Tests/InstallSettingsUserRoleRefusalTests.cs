using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A user-role account changes nobody but itself, and a setting the whole installation shares changes
/// everyone: the `/metrics` access rule, the refresh rate every account holder polls at, and the
/// prefill cache record every account's and guest's prefill reads. Each of those writes answers a
/// user-role account 403, even when the request is sent by hand, and the setting the admin reads
/// afterwards is the one it was before.
/// </summary>
// The host boots the app on the suite's shared database and shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class InstallSettingsUserRoleRefusalTests
{
    [Fact]
    public async Task AUserRoleAccountCannotChangeOrResetMetricsAccessAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var adminClient = await host.CreateAdminClientAsync();
        using var member = await SignInAsync(host, await host.NewAccountAsync());

        // The admin has made /metrics require the API key.
        using (var secured = await adminClient.PostAsJsonAsync("/api/metrics/security", new SetSecurityRequest { Enabled = true }))
            Assert.Equal(HttpStatusCode.OK, secured.StatusCode);

        var wrong = new List<string>();
        foreach (var (change, enabled) in new (string, bool?)[] { ("switching /metrics to Public", false), ("Reset to default", null) })
        {
            using var answer = await member.PostAsJsonAsync("/api/metrics/security", new SetSecurityRequest { Enabled = enabled });
            if (answer.StatusCode != HttpStatusCode.Forbidden)
                wrong.Add($"{change} answered a user-role account {(int)answer.StatusCode}, not 403");
            var after = await adminClient.GetFromJsonAsync<JsonElement>("/api/metrics/security");
            if (!after.GetProperty("requiresAuthentication").GetBoolean() || after.GetProperty("source").GetString() != "ui")
                wrong.Add($"after {change} by a user-role account the admin reads /metrics as {after}");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public async Task AUserRoleAccountCannotChangeTheInstallRefreshRateAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var adminClient = await host.CreateAdminClientAsync();
        using var member = await SignInAsync(host, await host.NewAccountAsync());

        using (var set = await adminClient.PatchAsJsonAsync("/api/system/refresh-rate", new SetRefreshRateRequest { RefreshRate = "RELAXED" }))
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var wrong = new List<string>();
        using (var answer = await member.PatchAsJsonAsync("/api/system/refresh-rate", new SetRefreshRateRequest { RefreshRate = "LIVE" }))
        {
            if (answer.StatusCode != HttpStatusCode.Forbidden)
                wrong.Add($"PATCH /api/system/refresh-rate answered a user-role account {(int)answer.StatusCode}, not 403");
        }
        var after = await adminClient.GetFromJsonAsync<JsonElement>("/api/system/refresh-rate");
        if (after.GetProperty("refreshRate").GetString() != "RELAXED")
            wrong.Add($"the admin now polls at {after.GetProperty("refreshRate").GetString()}, set by a user-role account, not the RELAXED the admin chose");

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public async Task AUserRoleAccountCannotClearTheInstallPrefillCacheAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var adminClient = await host.CreateAdminClientAsync();
        using var member = await SignInAsync(host, await host.NewAccountAsync());

        using var answer = await member.DeleteAsync("/api/prefill-admin/cache?service=steam");

        Assert.True(
            answer.StatusCode == HttpStatusCode.Forbidden,
            $"DELETE /api/prefill-admin/cache answered a user-role account {(int)answer.StatusCode} ({await answer.Content.ReadAsStringAsync()}), not 403, so it wiped every account's and guest's cached-game record for Steam");
    }

    /// <summary>A client signed in with the account's password, the way the sign-in form does it.</summary>
    // The same sign-in is private to UserRoleRefusalTests.cs:127 and MemberChangesOnlyOwnSettingsTests.cs:72.
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
            var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
            Assert.Equal("user", status.GetProperty("sessionType").GetString());
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
