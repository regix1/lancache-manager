using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Riot Prefill daemon sessions.
/// Only admin sessions can connect.
/// </summary>
public class RiotDaemonHub : PrefillDaemonHubBase<RiotDaemonService>
{
    public RiotDaemonHub(
        RiotDaemonService daemonService,
        SessionService sessionService,
        ILogger<RiotDaemonHub> logger)
        : base(daemonService, sessionService, logger)
    {
    }

    protected override string HubDisplayName => "Riot Prefill";

    protected override DateTime? GetPrefillExpiry(UserSession session)
        => session.RiotPrefillExpiresAtUtc;
}
