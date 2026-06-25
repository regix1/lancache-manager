using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Xbox / Microsoft Store Prefill daemon sessions.
/// Only admin sessions (or guests with Xbox prefill access) can connect.
/// </summary>
public class XboxPrefillDaemonHub : PrefillDaemonHubBase<XboxPrefillDaemonService>
{
    public XboxPrefillDaemonHub(
        XboxPrefillDaemonService daemonService,
        SessionService sessionService,
        ILogger<XboxPrefillDaemonHub> logger)
        : base(daemonService, sessionService, logger)
    {
    }

    protected override string HubDisplayName => "Xbox prefill";

    protected override DateTime? GetPrefillExpiry(UserSession session)
        => session.XboxPrefillExpiresAtUtc;
}
