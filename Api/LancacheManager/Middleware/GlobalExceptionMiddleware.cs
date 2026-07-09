using System.Net;
using System.Text.Json;

namespace LancacheManager.Middleware;

/// <summary>
/// Custom exception for 404 Not Found responses
/// </summary>
public class NotFoundException : Exception
{
    public NotFoundException(string resource) : base($"{resource} not found") { }
}

/// <summary>
/// Custom exception for 400 Bad Request validation errors
/// </summary>
public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}

/// <summary>
/// Custom exception for 409 Conflict responses (e.g. a resource already exists or an operation
/// is already running and cannot be started again). The message is developer-authored and safe to
/// surface to the client.
/// </summary>
public class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }
}

/// <summary>
/// Custom exception for 403 Forbidden responses (the caller is authenticated but not permitted).
/// The message is developer-authored and safe to surface to the client.
/// </summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }
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
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access attempt");
            await WriteErrorAsync(context, ex, HttpStatusCode.Forbidden, "Access denied");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument provided");
            await WriteErrorAsync(context, ex, HttpStatusCode.BadRequest, "Invalid request parameters");
        }
        catch (InvalidOperationException ex)
        {
            // Reclassified to 500 (was 400) per approved decision §6.2: most InvalidOperationExceptions
            // are server-state faults, not client mistakes. Controllers that need a genuine client 4xx
            // now throw ValidationException (400), ConflictException (409), or ForbiddenException (403)
            // instead (Wave 2 migrates the affected call sites).
            _logger.LogError(ex, "Invalid operation");
            await WriteErrorAsync(context, ex, HttpStatusCode.InternalServerError, "An unexpected error occurred");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error occurred");
            await WriteErrorAsync(context, ex, HttpStatusCode.InternalServerError, "A file system error occurred");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Request timeout");
            await WriteErrorAsync(context, ex, HttpStatusCode.RequestTimeout, "Request timed out");
        }
        catch (OperationCanceledException)
        {
            // Request was cancelled - don't log as error
            context.Response.StatusCode = 499; // Client Closed Request
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception occurred at {Path}", context.Request.Path);
            await WriteErrorAsync(context, ex, HttpStatusCode.InternalServerError, "An unexpected error occurred");
        }
    }

    private Task WriteErrorAsync(
        HttpContext context,
        Exception exception,
        HttpStatusCode statusCode,
        string safeMessage)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        // In development, include exception details for debugging
        // In production, use generic safe messages to prevent information disclosure
        var isDevelopment = _environment.IsDevelopment();

        var response = new
        {
            error = isDevelopment ? exception.Message : safeMessage,
            details = isDevelopment ? exception.Message : (string?)null,
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
