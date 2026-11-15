using LancacheManager.Controllers;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // Signing out while the database cannot be reached: the session the cookie names cannot be read, so
    // it cannot be ended either, but the browser is still told it signed out. It must leave without the
    // cookie, or the next page load once the database is back signs the person straight back in.
    [Fact]
    public async Task SignOut_SessionReadFails_ReloadDoesNotKeepTheCookie()
    {
        const string rawToken = "sign-out-while-database-down";
        var contexts = new ThrowingDbContextFactory();
        var sessions = new SessionService(
            contexts,
            apiKeyService: null!,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            configuration: new ConfigurationBuilder().Build());
        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = $"{SessionCookieName}={rawToken}";
        var controller = new AuthController(
            sessions,
            NullLogger<AuthController>.Instance,
            contexts,
            stateService: null!,
            signalR: null!,
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
        Assert.True(
            held is null,
            $"a sign-out while the database is down left the browser holding its session cookie (sign-out failure: {failure?.GetType().Name ?? "none"}, answer: {answer?.Result?.GetType().Name ?? "none"})");
    }
}
