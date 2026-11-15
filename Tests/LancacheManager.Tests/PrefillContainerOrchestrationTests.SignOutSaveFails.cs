using System.Security.Cryptography;
using System.Text;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    private const string SessionCookieName = "LancacheManager.Session";

    // Signing out tells the browser it is signed out whatever the server answers, so when the session row
    // cannot be marked ended the browser must still leave without a cookie that works: otherwise the next
    // page load signs the person straight back in after they were told they had signed out.
    [Fact]
    public async Task SignOut_RevokeSaveFails_ReloadIsNotSignedBackIn()
    {
        var writes = new SessionWrites { HoldRevocation = true, FailRevocation = true };
        writes.ReleaseRevocation.TrySetResult();
        var (_, contexts) = NewDatabase(writes);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var daemons = new PrefillDaemonServiceBase[]
        {
            new TestSteamDaemon(deps, new RecordingContainerGateway()), new TestEpicDaemon(deps, new RecordingContainerGateway()),
            new TestXboxDaemon(deps, new RecordingContainerGateway()), new TestBattleNetDaemon(deps, new RecordingContainerGateway()),
            new TestRiotDaemon(deps, new RecordingContainerGateway())
        };
        var setup = CreateGuestControllers(contexts, deps, daemons);
        using var services = setup.Services;
        const string rawToken = "guest-sign-out-token";
        await SeedGuestAsync(contexts, guest => guest.SessionTokenHash =
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))));
        var request = new DefaultHttpContext { RequestServices = services };
        request.Request.Headers.Cookie = $"{SessionCookieName}={rawToken}";
        var controller = new AuthController(
            setup.Auth,
            NullLogger<AuthController>.Instance,
            contexts,
            stateService: null!,
            deps.Notifications,
            apiKeyService: null!,
            passwordHasher: null!,
            accountLockout: null!,
            identityAuditService: null!)
        {
            ControllerContext = new ControllerContext { HttpContext = request }
        };

        ActionResult<LogoutResponse>? answer = null;
        var failure = await Record.ExceptionAsync(async () => answer = await controller.LogoutAsync());

        // An action that throws is answered 500 by the error handler with none of its headers, so the
        // browser keeps the cookie it sent.
        var held = failure is null ? CookieHeldAfter(request, rawToken) : rawToken;
        var reload = held is null ? null : await setup.Auth.ValidateSessionAsync(held);
        Assert.True(
            reload is null,
            $"after signing out, a reload with the cookie the browser still holds is signed in as session {reload?.Id} (sign-out failure: {failure?.GetType().Name ?? "none"})");

        // The browser warns that the server did not end the session only when the answer is not a
        // success, so a session that stays valid on the server must be answered 500.
        var status = failure is not null
            ? StatusCodes.Status500InternalServerError
            : answer?.Result is IStatusCodeActionResult result ? result.StatusCode : null;
        Assert.True(
            status == StatusCodes.Status500InternalServerError,
            $"a sign-out whose session could not be ended on the server answered {answer?.Result?.GetType().Name} with status {status}, so the browser shows no warning");
    }

    // What the browser holds after the response: the cookie it sent, unless a Set-Cookie for the session
    // cookie replaced it (an empty value is the cookie being deleted).
    private static string? CookieHeldAfter(HttpContext request, string sent)
    {
        var written = request.Response.Headers.SetCookie
            .Select(header => header ?? string.Empty)
            .LastOrDefault(header => header.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal));
        if (written is null) return sent;
        var value = written[$"{SessionCookieName}=".Length..].Split(';')[0];
        return value.Length == 0 ? null : value;
    }
}
