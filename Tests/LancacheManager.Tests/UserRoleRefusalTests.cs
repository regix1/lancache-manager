using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Filters;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// A user-role account changes nobody but itself. Every route that changes another account, another
/// session, every guest at once, a guest default, another person's prefill or a setting the whole
/// installation shares answers it 403, even when the request is sent by hand, and the other account
/// and its session are left as they were.
/// Its own preference save still goes through.
/// </summary>
// The host boots the app on the suite's shared database and shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class UserRoleRefusalTests
{
    [Fact]
    public async Task AUserRoleAccountIsRefusedEveryChangeToOtherPeopleAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var adminClient = await host.CreateAdminClientAsync();
        var otherCredentials = await host.NewAccountAsync();
        using var member = await SignInAsync(host, await host.NewAccountAsync());
        using var otherMember = await SignInAsync(host, otherCredentials);
        var memberStatus = await member.GetFromJsonAsync<JsonElement>("/api/auth/status");
        var otherStatus = await otherMember.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.Equal("user", memberStatus.GetProperty("sessionType").GetString());
        var ownSession = memberStatus.GetProperty("sessionId").GetGuid();
        var otherSession = otherStatus.GetProperty("sessionId").GetGuid();
        var otherAccount = otherStatus.GetProperty("accountId").GetGuid();
        var madeUpPrefillSession = Guid.NewGuid().ToString("N");

        var requests = new (string Route, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("PATCH /api/sessions/{other}/revoke",
                () => member.PatchAsync($"/api/sessions/{otherSession}/revoke", null)),
            ("DELETE /api/sessions/{other}",
                () => member.DeleteAsync($"/api/sessions/{otherSession}")),
            ("POST /api/sessions/bulk/reset-to-defaults",
                () => member.PostAsync("/api/sessions/bulk/reset-to-defaults", null)),
            ("DELETE /api/sessions/bulk/clear-guests",
                () => member.DeleteAsync("/api/sessions/bulk/clear-guests")),
            ("PUT /api/accounts/{other}",
                () => member.PutAsJsonAsync($"/api/accounts/{otherAccount}", new EditAccountRequest { Username = otherCredentials.Username })),
            ("PUT /api/accounts/{other}/disabled",
                () => member.PutAsJsonAsync($"/api/accounts/{otherAccount}/disabled", new SetAccountDisabledRequest { Disabled = true })),
            ("DELETE /api/accounts/{other}",
                () => member.DeleteAsync($"/api/accounts/{otherAccount}")),
            ("POST /api/prefill-admin/sessions/{id}/terminate",
                () => member.PostAsJsonAsync($"/api/prefill-admin/sessions/{madeUpPrefillSession}/terminate", new TerminateSessionRequest())),
            ("POST /api/prefill-admin/sessions/terminate-all",
                () => member.PostAsJsonAsync("/api/prefill-admin/sessions/terminate-all", new TerminateSessionRequest())),
            ("POST /api/prefill-admin/bans/by-session/{id}",
                () => member.PostAsJsonAsync($"/api/prefill-admin/bans/by-session/{madeUpPrefillSession}", new BanRequest())),
            ("POST /api/prefill-admin/bans",
                () => member.PostAsJsonAsync("/api/prefill-admin/bans", new BanByUsernameRequest { Username = "someone" })),
            ("POST /api/prefill-admin/bans/{id}/lift",
                () => member.PostAsync("/api/prefill-admin/bans/1/lift", null)),
            ("POST /api/auth/guest/config/duration",
                () => member.PostAsJsonAsync("/api/auth/guest/config/duration", new GuestDurationRequest { DurationHours = 6 })),
            ("POST /api/auth/guest/config/lock",
                () => member.PostAsJsonAsync("/api/auth/guest/config/lock", new GuestLockRequest { IsLocked = true })),
            ("POST /api/auth/guest/prefill/config",
                () => member.PostAsJsonAsync("/api/auth/guest/prefill/config", new GuestPrefillConfigRequest { EnabledByDefault = true })),
            ("POST /api/auth/guest/epic-prefill/config",
                () => member.PostAsJsonAsync("/api/auth/guest/epic-prefill/config", new EpicGuestPrefillConfigRequest { EnabledByDefault = true })),
            ("POST /api/auth/guest/battlenet-prefill/config",
                () => member.PostAsJsonAsync("/api/auth/guest/battlenet-prefill/config", new { enabledByDefault = true, durationHours = 2 })),
            ("POST /api/auth/guest/riot-prefill/config",
                () => member.PostAsJsonAsync("/api/auth/guest/riot-prefill/config", new { enabledByDefault = true, durationHours = 2 })),
            ("POST /api/auth/guest/xbox-prefill/config",
                () => member.PostAsJsonAsync("/api/auth/guest/xbox-prefill/config", new XboxGuestPrefillConfigRequest { EnabledByDefault = true })),
            ("PATCH /api/system/default-guest-refresh-rate",
                () => member.PatchAsJsonAsync("/api/system/default-guest-refresh-rate", new SetRefreshRateRequest { RefreshRate = "STANDARD" })),
            ("PATCH /api/system/guest-refresh-rate-lock",
                () => member.PatchAsJsonAsync("/api/system/guest-refresh-rate-lock", new GuestRefreshRateLockRequest { Locked = true })),
            ("PATCH /api/system/default-guest-preferences/allowed-time-formats",
                () => member.PatchAsJsonAsync("/api/system/default-guest-preferences/allowed-time-formats", new SetAllowedTimeFormatsRequest { Formats = ["server-24h", "utc"] })),
            ("PATCH /api/system/default-guest-preferences/clock",
                () => member.PatchAsJsonAsync("/api/system/default-guest-preferences/clock", new UserPreferencesService.ClockPreferences { UseUtcTimezone = true })),
            ("PATCH /api/system/default-guest-preferences/sharpCorners",
                () => member.PatchAsJsonAsync("/api/system/default-guest-preferences/sharpCorners", new SetBoolPreferenceRequest { Value = true })),
            ("PATCH /api/system/prefill-defaults",
                () => member.PatchAsJsonAsync("/api/system/prefill-defaults", new SetPrefillDefaultsRequest { MaxConcurrency = "auto" })),
            ("PUT /api/themes/preferences/guest",
                () => member.PutAsJsonAsync("/api/themes/preferences/guest", new ThemePreferenceRequest { ThemeId = "dark-default" })),
            ("POST /api/metrics/security",
                () => member.PostAsJsonAsync("/api/metrics/security", new SetSecurityRequest { Enabled = true })),
            ("POST /api/metrics/security (Reset to default)",
                () => member.PostAsJsonAsync("/api/metrics/security", new SetSecurityRequest { Enabled = null })),
            ("PATCH /api/system/refresh-rate",
                () => member.PatchAsJsonAsync("/api/system/refresh-rate", new SetRefreshRateRequest { RefreshRate = "LIVE" })),
            ("DELETE /api/prefill-admin/cache",
                () => member.DeleteAsync("/api/prefill-admin/cache?service=steam")),
            ("POST /api/accounts",
                () => member.PostAsJsonAsync("/api/accounts", new CreateAccountRequest { Username = $"made-by-member-{Guid.NewGuid():N}", Password = "Member-Made-9-long" }))
        };

        var wrong = new List<string>();
        foreach (var (route, send) in requests)
        {
            using var answer = await send();
            if (answer.StatusCode != HttpStatusCode.Forbidden)
                wrong.Add($"{route} answered a user-role account {(int)answer.StatusCode}, not 403");
        }

        using var otherAfter = await adminClient.GetAsync($"/api/accounts/{otherAccount}");
        if (otherAfter.StatusCode != HttpStatusCode.OK)
            wrong.Add($"the other account reads {(int)otherAfter.StatusCode} afterwards: it was deleted");
        else if ((await otherAfter.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isDisabled").GetBoolean())
            wrong.Add("the other account was disabled");
        var otherStatusAfter = await otherMember.GetFromJsonAsync<JsonElement>("/api/auth/status");
        if (!otherStatusAfter.TryGetProperty("sessionId", out var stillSignedIn)
            || stillSignedIn.ValueKind != JsonValueKind.String
            || stillSignedIn.GetGuid() != otherSession)
            wrong.Add("the other account's session was ended");

        using var ownSave = await member.PutAsJsonAsync(
            $"/api/user-preferences/session/{ownSession}",
            new { sharpCorners = true, showDatasourceLabels = true });
        if (ownSave.StatusCode != HttpStatusCode.OK)
            wrong.Add($"the user's save of its own session preferences answered {(int)ownSave.StatusCode}, not 200");

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// The handlers that decide for themselves what a user-role account may change: its own account,
    /// its own sessions and preferences, and the owner-only wipe and key rotation, which answer it with
    /// their own refusal.
    /// </summary>
    private static readonly string[] RoutesTheHandlerChecks =
    [
        "AccountsController.EditAccountAsync",
        "AccountsController.SetDisabledAsync",
        "AccountsController.DeleteAccountAsync",
        "AccountsController.WipeAccountsAsync",
        "ApiKeysController.RegenerateApiKeyAsync",
        "SessionsController.RevokeAsync",
        "SessionsController.DeleteAsync",
        "UserPreferencesController.SaveForSessionAsync",
        "AuthController.ToggleGuestPrefillAsync"
    ];

    /// <summary>
    /// Every change on an account-holder route is refused to a user-role account before its handler
    /// runs, apart from the routes whose handler checks the caller itself. The application's own
    /// authorization filters are run against each action, so no handler runs and a cache clear or a
    /// database reset is never sent for real. The token check is left out: a request without the
    /// token is refused by it whoever sends it.
    /// </summary>
    [Fact]
    public async Task EveryAccountHolderChangeIsRefusedToAUserRoleAccountAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();
        var services = scope.ServiceProvider;
        var filters = services.GetRequiredService<IOptions<MvcOptions>>().Value.Filters
            .Select(filter => filter is IFilterFactory factory ? factory.CreateInstance(services) : filter)
            .Where(filter => filter is not AntiforgeryFilter)
            .ToList();
        var member = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.User, AccountId = Guid.NewGuid() };

        var wrong = new List<string>();
        var checkedRoutes = 0;
        foreach (var action in services.GetRequiredService<IActionDescriptorCollectionProvider>()
                     .ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            if (!action.EndpointMetadata.OfType<IAuthorizeData>().Any(authorize => authorize.Policy == "AccountHolder"))
                continue;
            var name = $"{action.ControllerTypeInfo.Name}.{action.MethodInfo.Name}";
            foreach (var verb in action.EndpointMetadata.OfType<IHttpMethodMetadata>().SelectMany(methods => methods.HttpMethods).Distinct())
            {
                if (HttpMethods.IsGet(verb) || HttpMethods.IsHead(verb))
                    continue;
                checkedRoutes++;
                var context = new DefaultHttpContext { RequestServices = services };
                context.Request.Method = verb;
                context.Items["Session"] = member;
                var authorization = new AuthorizationFilterContext(
                    new ActionContext(context, new RouteData(), action),
                    new List<IFilterMetadata>());
                foreach (var filter in filters)
                {
                    if (authorization.Result != null)
                        break;
                    if (filter is IAsyncAuthorizationFilter asynchronous)
                        await asynchronous.OnAuthorizationAsync(authorization);
                    else if (filter is IAuthorizationFilter synchronous)
                        synchronous.OnAuthorization(authorization);
                }

                var refused = authorization.Result is ForbidResult;
                var handlerChecks = RoutesTheHandlerChecks.Contains(name);
                if (refused == handlerChecks)
                    wrong.Add(handlerChecks
                        ? $"{verb} {name} is refused before its handler, which checks the caller itself"
                        : $"{verb} {name} reaches its handler for a user-role account");
            }
        }

        Assert.True(checkedRoutes > 100, $"only {checkedRoutes} account-holder changes were found");
        Assert.True(wrong.Count == 0, $"{wrong.Count} route(s):{Environment.NewLine}{string.Join(Environment.NewLine, wrong)}");
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
