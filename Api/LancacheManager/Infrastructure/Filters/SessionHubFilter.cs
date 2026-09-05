using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Infrastructure.Filters;

/// <summary>
/// Tracks every browser hub connection by its authenticated session and rechecks the special shared
/// management session before each invocation. A saved secure mode can therefore close all browsers
/// that shared anonymous management access without disturbing account-backed connections.
/// </summary>
public sealed class SessionHubFilter : IHubFilter
{
    private const string SessionKey = "TrackedSession";
    private readonly ConnectionTrackingService _connections;
    private readonly SessionService _sessions;
    private readonly ILogger<SessionHubFilter> _logger;

    public SessionHubFilter(
        ConnectionTrackingService connections,
        SessionService sessions,
        ILogger<SessionHubFilter> logger)
    {
        _connections = connections;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next)
    {
        var session = context.Context.GetHttpContext()?.Items["Session"] as UserSession;
        if (session is null)
        {
            await next(context);
            return;
        }

        if (session is { SessionType: SessionType.Admin, AccountId: null }
            && !_sessions.CanManage(session))
        {
            context.Context.Abort();
            return;
        }

        context.Context.Items[SessionKey] = session;
        _connections.RegisterConnection(session.Id, context.Context.ConnectionId, context.Context.Abort);
        if (context.Context.ConnectionAborted.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await next(context);
        }
        catch
        {
            _connections.UnregisterConnection(context.Context.ConnectionId);
            throw;
        }

        if (context.Context.ConnectionAborted.IsCancellationRequested)
        {
            _connections.UnregisterConnection(context.Context.ConnectionId);
        }
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (invocationContext.Context.Items.TryGetValue(SessionKey, out var value)
            && value is UserSession session
            && (_connections.IsDisconnected(session.Id)
                || session is { SessionType: SessionType.Admin, AccountId: null }
                    && !_sessions.CanManage(session)))
        {
            _logger.LogWarning(
                "SignalR invocation rejected after its session ended: ConnectionId={ConnectionId}",
                invocationContext.Context.ConnectionId);
            invocationContext.Context.Abort();
            throw new HubException("Authentication required");
        }

        return await next(invocationContext);
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        _connections.UnregisterConnection(context.Context.ConnectionId);
        await next(context, exception);
    }
}
