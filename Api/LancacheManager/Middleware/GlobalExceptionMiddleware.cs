using System.Net;
using System.Text.Json;

namespace LancacheManager.Middleware;

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
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized access attempt");
            await HandleExceptionAsync(context, ex, HttpStatusCode.Forbidden, "Access denied");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument provided");
            await HandleExceptionAsync(context, ex, HttpStatusCode.BadRequest, "Invalid request parameters");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid operation");
            await HandleExceptionAsync(context, ex, HttpStatusCode.BadRequest, "Invalid operation");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error occurred");
            await HandleExceptionAsync(context, ex, HttpStatusCode.InternalServerError, "A file system error occurred");
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Request timeout");
            await HandleExceptionAsync(context, ex, HttpStatusCode.RequestTimeout, "Request timed out");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Operation was cancelled");
            await HandleExceptionAsync(context, ex, HttpStatusCode.BadRequest, "Operation was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception occurred at {Path}", context.Request.Path);
            await HandleExceptionAsync(context, ex, HttpStatusCode.InternalServerError, "An unexpected error occurred");
        }
    }

    private Task HandleExceptionAsync(
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
            statusCode = (int)statusCode
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
