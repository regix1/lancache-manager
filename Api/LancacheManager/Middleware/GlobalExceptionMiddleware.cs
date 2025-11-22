using System.Net;
using System.Text.Json;

namespace LancacheManager.Middleware;

/// <summary>
/// Global exception handling middleware to eliminate duplicate try-catch blocks across controllers
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> _logger)
    {
        _next = next;
        this._logger = _logger;
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
            await HandleExceptionAsync(context, ex, HttpStatusCode.Forbidden);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid argument provided");
            await HandleExceptionAsync(context, ex, HttpStatusCode.BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid operation");
            await HandleExceptionAsync(context, ex, HttpStatusCode.BadRequest);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error occurred");
            await HandleExceptionAsync(context, ex, HttpStatusCode.InternalServerError);
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
            await HandleExceptionAsync(context, ex, HttpStatusCode.InternalServerError);
        }
    }

    private static Task HandleExceptionAsync(
        HttpContext context,
        Exception exception,
        HttpStatusCode statusCode,
        string? customMessage = null)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var response = new
        {
            error = customMessage ?? exception.Message,
            details = exception.Message,
            statusCode = (int)statusCode
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
