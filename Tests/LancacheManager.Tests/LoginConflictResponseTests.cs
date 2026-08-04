using System.Text;
using System.Text.Json;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// What a caller that loses the race for a session's login lock actually receives over HTTP.
///
/// <c>PrefillDaemonServiceBase.StartLoginEntryAsync</c> throws
/// <see cref="ConflictException"/> with the sentence asserted below when the bounded wait on
/// <c>DaemonSession.LoginLock</c> expires, and <see cref="GlobalExceptionMiddleware"/> is what turns
/// that into a response. The two tests here are a matched pair covering the same sentence thrown as
/// the two different exception types, because the type is the entire difference between a caller who
/// is told a login is already running and one who is told nothing at all:
/// <see cref="ConflictException"/> yields 409 with the sentence intact, while a plain
/// <see cref="InvalidOperationException"/> yields 500 and the sentence is replaced with the generic
/// "An unexpected error occurred".
///
/// The environment is Production in both, since that is where the middleware sanitizes messages. In
/// Development every exception message is echoed back, so a Development-only check would prove
/// nothing about what a user sees.
/// </summary>
public class LoginConflictResponseTests
{
    private const string LoginInProgressMessage =
        "A login attempt is already in progress for session 4f2c8ab19d0e7c35.";

    private const string GenericServerMessage = "An unexpected error occurred";

    [Fact]
    public async Task LoginLockConflict_ThrownAsConflict_Returns409WithTheRealMessage()
    {
        var response = await RunMiddlewareAsync(new ConflictException(LoginInProgressMessage));

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Equal(LoginInProgressMessage, response.Error);
        Assert.DoesNotContain(GenericServerMessage, response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginLockConflict_ThrownAsInvalidOperation_Returns500AndLosesTheMessage()
    {
        var response = await RunMiddlewareAsync(new InvalidOperationException(LoginInProgressMessage));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(GenericServerMessage, response.Error);
        Assert.DoesNotContain("login attempt", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs one request through <see cref="GlobalExceptionMiddleware"/> whose inner delegate throws
    /// <paramref name="thrown"/>, and reads back the status code and the body's <c>error</c> field.
    /// </summary>
    private static async Task<ErrorResponse> RunMiddlewareAsync(Exception thrown)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/prefill/persistent/login";
        var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new GlobalExceptionMiddleware(
            _ => throw thrown,
            NullLogger<GlobalExceptionMiddleware>.Instance,
            new TestHostEnvironment("Production"));

        await middleware.InvokeAsync(context);

        var json = Encoding.UTF8.GetString(body.ToArray());
        using var document = JsonDocument.Parse(json);
        return new ErrorResponse(
            context.Response.StatusCode,
            document.RootElement.GetProperty("error").GetString() ?? string.Empty);
    }

    /// <summary>The two parts of the middleware's reply this file asserts on.</summary>
    private sealed record ErrorResponse(int StatusCode, string Error);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "LancacheManager";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
