using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// Turning a service On under a guest session's prefill access on the Users page gives that guest
/// prefill access for it. The guest's own status read, which the page makes on load and again after the
/// live connection comes back, reports the guest as itself with that service on, for every service the
/// pane lists. The toggle's answer carries what the live event carries: the service on and its expiry.
/// Sign-in is on: with sign-in off no guest session lasts, because every browser that holds a session
/// other than the owner's is handed the shared administrator session (SessionAuthenticationHandler.cs).
/// </summary>
// The host boots the app on the suite's shared database and shared session (SessionService.cs:45).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class GuestPrefillGrantReachesGuestTests
{
    private static readonly (string Service, string StatusField)[] Services =
    [
        ("steam", "steamPrefillEnabled"),
        ("epic", "epicPrefillEnabled"),
        ("battlenet", "battlenetPrefillEnabled"),
        ("riot", "riotPrefillEnabled"),
        ("xbox", "xboxPrefillEnabled")
    ];

    [Fact]
    public async Task TurningAServiceOnForAGuest_ReachesTheGuestsOwnAccessReadAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var adminClient = await host.CreateAdminClientAsync();

        // A guest whose browser holds its own session cookie.
        string guestCookie;
        Guid guestSession;
        using (var scope = host.Application.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
            Assert.NotNull(guest);
            guestSession = guest!.Value.Session.Id;
            var cookieWriter = new DefaultHttpContext();
            sessions.SetSessionCookie(cookieWriter, guest.Value.RawToken, DateTime.UtcNow.AddDays(1));
            guestCookie = cookieWriter.Response.Headers.SetCookie.ToString().Split(';')[0];
        }

        var wrong = new List<string>();
        foreach (var (service, _) in Services)
        {
            using var toggled = await adminClient.PostAsJsonAsync(
                $"/api/auth/guest/prefill/toggle/{guestSession}?service={service}",
                new GuestPrefillToggleRequest { Enabled = true });
            if (toggled.StatusCode != HttpStatusCode.OK)
            {
                wrong.Add($"turning {service} on answered {(int)toggled.StatusCode}");
                continue;
            }
            var answer = await toggled.Content.ReadFromJsonAsync<JsonElement>();
            if (!answer.GetProperty("enabled").GetBoolean()
                || answer.GetProperty("prefillExpiresAt").ValueKind == JsonValueKind.Null)
                wrong.Add($"turning {service} on answered without the grant ({answer})");
        }

        // The guest reloads the page.
        using var guestClient = host.Application.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        guestClient.DefaultRequestHeaders.Add("Cookie", guestCookie);
        using var statusResponse = await guestClient.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionType = status.GetProperty("sessionType").GetString();
        var sessionId = status.TryGetProperty("sessionId", out var id) ? id.ToString() : null;
        if (sessionType != "guest" || sessionId != guestSession.ToString())
            wrong.Add($"the guest's own read answered as session {sessionId} of type {sessionType}, not as the guest {guestSession} the access was granted to");
        foreach (var (service, field) in Services)
        {
            if (!status.TryGetProperty(field, out var enabled) || enabled.ValueKind != JsonValueKind.True)
                wrong.Add($"the guest's own read reports {field} {(status.TryGetProperty(field, out var shown) ? shown.ToString() : "missing")} after {service} was turned on");
        }

        Assert.True(wrong.Count == 0, "the prefill access turned on for a guest does not reach that guest: " + string.Join("; ", wrong));
    }
}
