using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Infrastructure.Filters;

/// <summary>
/// SignalR hub-side parallel to the HTTP <c>GlobalExceptionMiddleware</c>. Any exception a hub method
/// throws that is NOT already a <see cref="HubException"/> is logged here with its full detail and then
/// rethrown as a generic <see cref="HubException"/>, so clients always receive a consistent,
/// non-leaking message rather than SignalR's opaque default (or, with <c>EnableDetailedErrors</c>,
/// an internal stack/message). Deliberate <see cref="HubException"/>s thrown by hub methods for
/// client-visible errors pass through unchanged. Registered in <c>AddSignalR</c> via
/// <c>options.AddFilter&lt;HubExceptionFilter&gt;()</c>.
/// </summary>
public sealed class HubExceptionFilter : IHubFilter
{
    private readonly ILogger<HubExceptionFilter> _logger;

    public HubExceptionFilter(ILogger<HubExceptionFilter> logger)
    {
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (HubException)
        {
            // Deliberate, client-visible hub error — already the consistent contract; pass through
            // unchanged (do not re-wrap or double-log). The throwing hub method owns its own logging.
            throw;
        }
        catch (Exception ex)
        {
            // Never swallow: log the structured exception object with hub/method context, then surface a
            // generic message so no internal detail leaks to the client.
            _logger.LogError(
                ex,
                "Unhandled exception in hub method {Hub}.{Method}",
                invocationContext.Hub.GetType().Name,
                invocationContext.HubMethodName);

            throw new HubException("An error occurred");
        }
    }
}
