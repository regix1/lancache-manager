using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Security;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Battle.net Prefill daemon sessions.
/// Only admin sessions can connect.
/// </summary>
public class BattleNetDaemonHub : PrefillDaemonHubBase<BattleNetDaemonService>
{
    public BattleNetDaemonHub(
        BattleNetDaemonService daemonService,
        SessionService sessionService,
        ILogger<BattleNetDaemonHub> logger)
        : base(daemonService, sessionService, logger)
    {
    }

    protected override string HubDisplayName => "Battle.net Prefill";

    protected override DateTime? GetPrefillExpiry(UserSession session)
        => session.BattleNetPrefillExpiresAtUtc;
}
