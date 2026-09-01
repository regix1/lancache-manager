using System.Text;
using System.Text.Json;
using LancacheManager.Infrastructure.Filters;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The refusal a caller receives carries an i18n key naming the reason, beside the English sentence
/// that has always been there. The key is the only thing that can be read in another language: the
/// sentence is composed here, in English, and no amount of translation on the browser side can undo
/// that. So what is asserted below is that the key reaches the wire at all, for both kinds of
/// failure the middleware answers.
///
/// A deliberate refusal names itself, by setting <c>StageKey</c> on the exception it throws. A
/// framework exception cannot: its own message may name a path or a column and is withheld in
/// production, so the branch that caught it supplies the key instead.
///
/// The environment is Production unless a test names another one, because that is where the
/// middleware substitutes its safe message. Development echoes the exception text back instead, and
/// the one test that runs there checks that the text is what reaches the reader.
/// </summary>
public class ErrorStageKeyResponseTests
{
    [Fact]
    public async Task ThrownKeyAndValues_ReachTheBodyBesideTheEnglishSentence()
    {
        // The key names a refusal whose locale entry arrives with the prefill area. Until it does,
        // the browser shows the English sentence below, which is what the fallback is for.
        var thrown = new ConflictException(
            "An existing persistent steam container is still being removed or restarting. Please try again shortly.")
        {
            StageKey = "errors.prefill.containerBusy",
            Context = new Dictionary<string, object?> { ["service"] = "steam" }
        };

        var response = await RunMiddlewareAsync(thrown);

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Equal(thrown.Message, response.Error);
        Assert.Equal("errors.prefill.containerBusy", response.StageKey);
        Assert.Equal("{\"service\":\"steam\"}", response.Context);
    }

    [Fact]
    public async Task UnkeyedRefusal_SendsNoKeyAtAll()
    {
        // Every throw site that has not been keyed yet. The English sentence is still the whole
        // answer, and nothing about the body may change until that site names its reason.
        var response = await RunMiddlewareAsync(
            new ValidationException("A corruption scan ID is required"));

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("A corruption scan ID is required", response.Error);
        Assert.Null(response.StageKey);
        Assert.DoesNotContain("stageKey", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrameworkFailures_AreEachNamedByTheBranchThatCaughtThem()
    {
        var thrown = new Exception[]
        {
            new UnauthorizedAccessException("/data/cache is not writable by uid 1000"),
            new ArgumentException("appId must be a positive number"),
            new InvalidOperationException("the reader was disposed"),
            new IOException("the cache volume went away"),
            new TaskCanceledException("the depot request ran long", new TimeoutException()),
            new NotSupportedException("nothing catches this one by type")
        };

        var answered = new List<(int StatusCode, string Error, string? StageKey)>();
        foreach (var exception in thrown)
        {
            var response = await RunMiddlewareAsync(exception);
            answered.Add((response.StatusCode, response.Error, response.StageKey));
        }

        var expected = new (int StatusCode, string Error, string? StageKey)[]
        {
            (StatusCodes.Status403Forbidden, "Access denied", "errors.http.accessDenied"),
            (StatusCodes.Status400BadRequest, "Invalid request parameters", "errors.http.invalidParameters"),
            (StatusCodes.Status500InternalServerError, "An unexpected error occurred", "errors.http.unexpected"),
            (StatusCodes.Status500InternalServerError, "A file system error occurred", "errors.http.fileSystem"),
            (StatusCodes.Status408RequestTimeout, "Request timed out", "errors.http.timeout"),
            (StatusCodes.Status500InternalServerError, "An unexpected error occurred", "errors.http.unexpected")
        };

        Assert.Equal(expected, answered);
    }

    [Fact]
    public async Task ValidationFilter_NamesItsRefusalWithAKey()
    {
        // The heading sentence is the one the browser shows: it reads `error`, and never the
        // per-field list underneath. Keying it is what turns "Validation failed" into words the
        // reader knows.
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor(),
            new ModelStateDictionary());
        actionContext.ModelState.AddModelError("password", "Password cannot exceed 256 characters");

        var executing = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object());

        await new ValidationFilter().OnActionExecutionAsync(
            executing,
            () => throw new InvalidOperationException("the action ran on a request that failed validation"));

        var result = Assert.IsType<BadRequestObjectResult>(executing.Result);
        var body = Assert.IsType<ValidationErrorResponse>(result.Value);

        Assert.Equal("Validation failed", body.Error);
        Assert.Equal("errors.validation.failed", body.StageKey);
        Assert.Equal("Password cannot exceed 256 characters", Assert.Single(body.Errors).Message);
    }

    /// <summary>
    /// In Development the body carries the exception's own text, which is the whole point of that
    /// environment. A branch key beside it takes that away: the browser renders the key and falls
    /// back to the text only when there is no key, so the developer would read "An unexpected
    /// error occurred" instead of what actually went wrong. A deliberate refusal still names
    /// itself, in every environment.
    /// </summary>
    [Fact]
    public async Task Development_KeepsTheExceptionTextInsteadOfTheBranchKey()
    {
        var framework = await RunMiddlewareAsync(
            new InvalidOperationException("the reader was disposed"), "Development");

        Assert.Equal("the reader was disposed", framework.Error);
        Assert.Null(framework.StageKey);

        var refusal = await RunMiddlewareAsync(
            new ValidationException("A corruption scan ID is required")
            {
                StageKey = "errors.validation.failed"
            },
            "Development");

        Assert.Equal("errors.validation.failed", refusal.StageKey);
    }

    /// <summary>
    /// Runs one request through <see cref="GlobalExceptionMiddleware"/> whose inner delegate throws
    /// <paramref name="thrown"/>, and reads back what the caller receives.
    /// </summary>
    private static async Task<Refusal> RunMiddlewareAsync(
        Exception thrown,
        string environmentName = "Production")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/cache/corruption/remove";
        var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new GlobalExceptionMiddleware(
            _ => throw thrown,
            NullLogger<GlobalExceptionMiddleware>.Instance,
            new TestHostEnvironment(environmentName));

        await middleware.InvokeAsync(context);

        var json = Encoding.UTF8.GetString(body.ToArray());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return new Refusal(
            context.Response.StatusCode,
            root.GetProperty("error").GetString() ?? string.Empty,
            root.TryGetProperty("stageKey", out var stageKey) ? stageKey.GetString() : null,
            root.TryGetProperty("context", out var stageContext) ? stageContext.GetRawText() : null,
            json);
    }

    /// <summary>The parts of the middleware's reply this file asserts on, plus the raw body.</summary>
    private sealed record Refusal(
        int StatusCode,
        string Error,
        string? StageKey,
        string? Context,
        string Body);

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
