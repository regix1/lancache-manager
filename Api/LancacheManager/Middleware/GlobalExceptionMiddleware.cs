using System.Net;
using System.Text.Json;

namespace LancacheManager.Middleware;

/// <summary>
/// Base for the answers this API gives on purpose. Carries the i18n key naming the reason so the
/// browser can show it in the reader's language; the English message stays on the wire as the
/// fallback for a build whose locale has no words for that key yet.
/// </summary>
public abstract class ApiException : Exception
{
    protected ApiException(string message) : base(message) { }

    /// <summary>
    /// i18n key for the localized reason (e.g. <c>"errors.corruption.scanIdRequired"</c>). Null where
    /// the throw site has not been keyed yet, which is every site the English sentence still answers.
    /// </summary>
    public string? StageKey { get; init; }

    /// <summary>Substitution values for the localized <see cref="StageKey"/> template.</summary>
    public Dictionary<string, object?>? Context { get; init; }
}

/// <summary>
/// Custom exception for 404 Not Found responses
/// </summary>
public class NotFoundException : ApiException
{
    public NotFoundException(string resource) : base($"{resource} not found") { }
}

/// <summary>
/// Custom exception for 400 Bad Request validation errors
/// </summary>
public class ValidationException : ApiException
{
    public ValidationException(string message) : base(message) { }

    /// <summary>
    /// Only a subclass may name itself: a code is a promise to the caller that this refusal is
    /// one it can recognize, and that promise belongs to the type rather than to a call site.
    /// </summary>
    protected ValidationException(string message, string code) : base(message) => Code = code;

    /// <summary>
    /// A stable token naming this refusal, written onto the error body so a caller can tell one
    /// 400 apart from another without matching prose. Null for the ordinary case, where the
    /// sentence is the whole answer.
    /// </summary>
    public string? Code { get; }
}

/// <summary>
/// Custom exception for 409 Conflict responses (e.g. a resource already exists or an operation
/// is already running and cannot be started again). The message is developer-authored and safe to
/// surface to the client.
/// </summary>
public class ConflictException : ApiException
{
    public ConflictException(string message) : base(message) { }
}

/// <summary>
/// Custom exception for 403 Forbidden responses (the caller is authenticated but not permitted).
/// The message is developer-authored and safe to surface to the client.
/// </summary>
public class ForbiddenException : ApiException
{
    public ForbiddenException(string message) : base(message) { }
}

/// <summary>
/// Custom exception for 503 Service Unavailable responses (something we depend on is down or not
/// reachable, such as Docker or a remote download host). Nothing the caller sent is wrong, so a 4xx
/// would be a lie and a 500 would hide the one thing that helps: which dependency to bring back up.
/// The message is developer-authored and safe to surface to the client.
/// </summary>
public class ServiceUnavailableException : ApiException
{
    public ServiceUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Global exception handling middleware to eliminate duplicate try-catch blocks across controllers
/// Sanitizes error messages in production to prevent information disclosure
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger, IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (NotFoundException ex)
        {
            await WriteErrorAsync(context, ex, HttpStatusCode.NotFound, ex.Message);
        }
        catch (ValidationException ex)
        {
            await WriteErrorAsync(context, ex, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (ConflictException ex)
        {
            await WriteErrorAsync(context, ex, HttpStatusCode.Conflict, ex.Message);
        }
        catch (ForbiddenException ex)
        {
            _logger.LogWarning(ex, "Forbidden operation attempt");
            await WriteErrorAsync(context, ex, HttpStatusCode.Forbidden, ex.Message);
        }
        catch (ServiceUnavailableException ex)
        {
            _logger.LogWarning(ex, "A dependency is unavailable");
            await WriteErrorAsync(context, ex, HttpStatusCode.ServiceUnavailable, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access attempt");
            await WriteErrorAsync(context, ex, HttpStatusCode.Forbidden, "Access denied", "errors.http.accessDenied");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument provided");
            await WriteErrorAsync(context, ex, HttpStatusCode.BadRequest, "Invalid request parameters", "errors.http.invalidParameters");
        }
        catch (InvalidOperationException ex)
        {
            // Reclassified to 500 (was 400) per approved decision §6.2: most InvalidOperationExceptions
            // are server-state faults, not client mistakes. Controllers that need a genuine client 4xx
            // now throw ValidationException (400), ConflictException (409), or ForbiddenException (403)
            // instead (Wave 2 migrates the affected call sites).
            _logger.LogError(ex, "Invalid operation");
            await WriteErrorAsync(context, ex, HttpStatusCode.InternalServerError, "An unexpected error occurred", "errors.http.unexpected");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error occurred");
            await WriteErrorAsync(context, ex, HttpStatusCode.InternalServerError, "A file system error occurred", "errors.http.fileSystem");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Request timeout");
            await WriteErrorAsync(context, ex, HttpStatusCode.RequestTimeout, "Request timed out", "errors.http.timeout");
        }
        catch (OperationCanceledException)
        {
            // Request was cancelled - don't log as error
            context.Response.StatusCode = 499; // Client Closed Request
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception occurred at {Path}", context.Request.Path);
            await WriteErrorAsync(context, ex, HttpStatusCode.InternalServerError, "An unexpected error occurred", "errors.http.unexpected");
        }
    }

    private Task WriteErrorAsync(
        HttpContext context,
        Exception exception,
        HttpStatusCode statusCode,
        string safeMessage,
        string? stageKey = null)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        // In development, include exception details for debugging
        // In production, use generic safe messages to prevent information disclosure
        var isDevelopment = _environment.IsDevelopment();

        // A deliberate answer names its own reason; the framework exceptions are named by the branch
        // that caught them, since their own text is not safe to show. In development that text is
        // shown, and the branch key would hide it again: the browser renders the key and falls back
        // to the message only when there is no key.
        var answered = exception as ApiException;

        var response = new
        {
            error = isDevelopment ? exception.Message : safeMessage,
            details = isDevelopment ? exception.Message : (string?)null,
            // Same field, same value, as the routes that return the refusal directly rather than
            // throwing it: both paths exist for the download refusal, and a caller cannot be asked
            // to know which one answered it. Omitted when null, which is every other exception.
            code = (exception as ValidationException)?.Code,
            stageKey = answered?.StageKey ?? (isDevelopment ? null : stageKey),
            context = answered?.Context,
            statusCode = (int)statusCode,
            traceId = context.TraceIdentifier
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(response, options));
    }
}

/// <summary>
/// Extension method to easily add the global exception middleware
/// </summary>
public static class GlobalExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<GlobalExceptionMiddleware>();
    }
}
