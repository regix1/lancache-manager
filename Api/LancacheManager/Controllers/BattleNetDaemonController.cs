using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Controllers.Base;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Battle.net daemon sessions.
/// Battle.net prefill is anonymous (no account login), so no credential exchange occurs;
/// the secure socket handshake is still used for transport security.
/// </summary>
[Route("api/battlenet-daemon")]
[Authorize(Policy = "BattleNetPrefillAccess")]
public class BattleNetDaemonController : DaemonControllerBase<BattleNetDaemonService>
{
    public BattleNetDaemonController(
        BattleNetDaemonService daemonService,
        ILogger<BattleNetDaemonController> logger,
        StateService stateService,
        UserPreferencesService userPreferencesService)
        : base(daemonService, logger, stateService, userPreferencesService, "Battle.net")
    {
    }

    /// <summary>
    /// Gets Battle.net daemon service status.
    /// </summary>
    /// <remarks>
    /// The Battle.net management card polls this over REST because it has no login flow and
    /// therefore no session to carry the status over the hub.
    /// </remarks>
    [HttpGet("status")]
    [ProducesResponseType(typeof(DaemonStatusResponse), StatusCodes.Status200OK)]
    public new ActionResult GetStatus() => base.GetStatus();

    protected override int? ResolveThreadLimit(UserSession session)
    {
        if (session.SessionType == SessionType.Admin) return null;
        return _stateService.GetDefaultGuestMaxThreadCount();
    }
}
