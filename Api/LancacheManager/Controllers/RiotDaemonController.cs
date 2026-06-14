using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Controllers.Base;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Riot daemon sessions.
/// Riot prefill is anonymous (no account login), so no credential exchange occurs;
/// the secure socket handshake is still used for transport security.
/// </summary>
[Route("api/riot-daemon")]
[Authorize(Policy = "RiotPrefillAccess")]
public class RiotDaemonController : DaemonControllerBase<RiotDaemonService>
{
    public RiotDaemonController(
        RiotDaemonService daemonService,
        ILogger<RiotDaemonController> logger,
        StateService stateService,
        UserPreferencesService userPreferencesService)
        : base(daemonService, logger, stateService, userPreferencesService, "Riot")
    {
    }

    protected override int? ResolveThreadLimit(UserSession session)
    {
        if (session.SessionType == SessionType.Admin) return null;
        return _stateService.GetDefaultGuestMaxThreadCount();
    }
}
