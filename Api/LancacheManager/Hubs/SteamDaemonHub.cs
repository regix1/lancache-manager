using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Steam daemon sessions.
/// Only admin sessions can connect.
/// </summary>
public class SteamDaemonHub : PrefillDaemonHubBase<SteamDaemonService>
{
    public SteamDaemonHub(
        SteamDaemonService daemonService,
        SessionService sessionService,
        ILogger<SteamDaemonHub> logger)
        : base(daemonService, sessionService, logger)
    {
    }

    protected override string HubDisplayName => "Steam daemon";

    protected override DateTime? GetPrefillExpiry(UserSession session)
        => session.SteamPrefillExpiresAtUtc;
}
