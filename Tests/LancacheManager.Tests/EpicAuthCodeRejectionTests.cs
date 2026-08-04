using System.Net;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// What the user gets back after pasting an Epic authorization code that Epic rejects.
///
/// The code is typed in by hand on the Integrations page and expires within minutes, so a rejected
/// exchange is the caller's input to fix, not a server fault. <c>EpicApiDirectClient.ExchangeAuthCodeAsync</c>
/// throws <see cref="ValidationException"/> for that case and it travels untouched to
/// <see cref="GlobalExceptionMiddleware"/>: <c>EpicMappingService.OnAuthCodeReceivedAsync</c> rethrows
/// after reporting, and <c>EpicGameMappingController.CompleteAuthAsync</c> rethrows as well.
///
/// The pair of middleware tests covers the same sentence thrown as the two different types, because
/// the type is the whole difference between being told the code was rejected and being told nothing:
/// <see cref="ValidationException"/> yields 400 with the sentence intact, while a plain
/// <see cref="InvalidOperationException"/> yields 500 and the sentence is replaced.
///
/// The environment is Production in both, since that is where the middleware sanitizes messages. In
/// Development every exception message is echoed back, so a Development-only check would prove nothing
/// about what a user sees.
/// </summary>
public class EpicAuthCodeRejectionTests
{
    private const string GenericServerMessage = "An unexpected error occurred";

    [Fact]
    public async Task RejectedAuthCode_ThrowsValidationException()
    {
        using var client = new HttpClient(new StubResponseHandler(HttpStatusCode.BadRequest));
        var epicClient = new EpicApiDirectClient(client, NullLogger<EpicApiDirectClient>.Instance);

        var thrown = await Assert.ThrowsAsync<ValidationException>(
            () => epicClient.ExchangeAuthCodeAsync("expired-code"));

        Assert.Contains("Check your authorization code", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedAuthCode_ThrownAsValidation_Returns400WithTheRealMessage()
    {
        const string message = "Epic OAuth failed: BadRequest. Check your authorization code.";

        var response = await RunMiddlewareAsync(new ValidationException(message));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(message, response.Error);
        Assert.DoesNotContain(GenericServerMessage, response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedAuthCode_ThrownAsInvalidOperation_Returns500AndLosesTheMessage()
    {
        const string message = "Epic OAuth failed: BadRequest. Check your authorization code.";

        var response = await RunMiddlewareAsync(new InvalidOperationException(message));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(GenericServerMessage, response.Error);
        Assert.DoesNotContain("authorization code", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Runs one request through <see cref="GlobalExceptionMiddleware"/> whose inner delegate throws
    /// <paramref name="thrown"/>, and reads back the status code and the body's <c>error</c> field.
    /// </summary>
    private static async Task<ErrorResponse> RunMiddlewareAsync(Exception thrown)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/epic-game-mapping/auth/complete";
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

    /// <summary>Answers every request with a fixed status and an empty JSON body.</summary>
    private sealed class StubResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public StubResponseHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

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
