using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Controllers;
using LancacheManager.Middleware;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// With sign-in on and the database down, a sign-out cannot end the session, but the server still
/// clears the browser's cookie and answers 500. The page that clicked Sign out has to tell the other
/// windows of the browser that they are signed out too, since none of them holds a cookie any more;
/// it can only do that when the answer says the cookie was cleared. A 500 whose failure came before
/// the sign-out ran (the error handler's answer) leaves the cookie in place, so the page must be able
/// to tell the two apart from typed fields, never from the English sentence.
/// </summary>
public sealed class SignOutDatabaseDownAnswerTests
{
    private const string SessionCookieName = "LancacheManager.Session";

    // The wire the browser reads: the REST JSON options `Program.cs` sets on the controllers.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Text for a person to read, or an echo of the status line and the request: none of them can tell
    // the page what happened to the cookie without matching prose.
    private static readonly HashSet<string> Untyped = new(StringComparer.Ordinal)
    {
        "error", "details", "message", "statusCode", "traceId", "context"
    };

    [Fact]
    public async Task SignOutWithTheDatabaseDown_AnswerTellsThePageTheCookieWasCleared()
    {
        const string rawToken = "sign-out-answer-while-database-down";
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

        var answer = await controller.LogoutAsync();

        var setCookie = request.Response.Headers.SetCookie.ToString();
        Assert.True(
            setCookie.Contains(SessionCookieName, StringComparison.Ordinal),
            $"the sign-out with the database down did not clear the session cookie (Set-Cookie: '{setCookie}')");
        var reply = Assert.IsAssignableFrom<ObjectResult>(answer.Result);
        var signOutBody = JsonSerializer.Serialize(reply.Value, reply.Value?.GetType() ?? typeof(object), Wire);
        var signOutTyped = TypedFields(signOutBody);

        foreach (var environmentName in new[] { Environments.Production, Environments.Development })
        {
            var handlerBody = await ErrorHandlerAnswerAsync(environmentName);
            Assert.True(
                signOutTyped.Length > 0 && !string.Equals(signOutTyped, TypedFields(handlerBody), StringComparison.Ordinal),
                $"the sign-out answer with the database down ({reply.StatusCode}: {signOutBody}) carries no typed field that tells "
                + $"it apart from the error handler's 500 in {environmentName} ({handlerBody}), so the page cannot tell that this browser's "
                + "cookie was cleared, posts no signed-out message, and the browser's other windows stay signed in with live "
                + "connections until their next request is refused");
        }
    }

    /// <summary>
    /// The 500 the error handler answers when a request fails before the sign-out runs, which leaves
    /// the browser's cookie as it was.
    /// </summary>
    private static async Task<string> ErrorHandlerAnswerAsync(string environmentName)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/auth/logout";
        var body = new MemoryStream();
        context.Response.Body = body;
        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException("context creation failed"),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            new HostingEnvironment { EnvironmentName = environmentName });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    /// <summary>The body's fields that are not prose, each as its raw JSON, in a stable order.</summary>
    private static string TypedFields(string json)
    {
        using var document = JsonDocument.Parse(json);
        return string.Join(
            ",",
            document.RootElement.EnumerateObject()
                .Where(property => !Untyped.Contains(property.Name) && property.Value.ValueKind != JsonValueKind.Null)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}={property.Value.GetRawText()}"));
    }
}
