using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Epic Games Prefill daemon sessions.
/// Only admin sessions can connect.
/// </summary>
public class EpicPrefillDaemonHub : PrefillDaemonHubBase<EpicPrefillDaemonService>
{
    public EpicPrefillDaemonHub(
        EpicPrefillDaemonService daemonService,
        SessionService sessionService,
        ILogger<EpicPrefillDaemonHub> logger)
        : base(daemonService, sessionService, logger)
    {
    }

    protected override string HubDisplayName => "Epic prefill";

    protected override DateTime? GetPrefillExpiry(UserSession session)
        => session.EpicPrefillExpiresAtUtc;
}
