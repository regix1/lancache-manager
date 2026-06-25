using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Controllers.Base;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Xbox / Microsoft Store daemon sessions.
/// Xbox prefill is login-required (Microsoft OAuth device-code), so the user authenticates in
/// their own browser and no password ever enters the daemon container; the secure socket
/// handshake is still used for transport security.
/// </summary>
[Route("api/xbox-daemon")]
[Authorize(Policy = "XboxPrefillAccess")]
public class XboxDaemonController : DaemonControllerBase<XboxPrefillDaemonService>
{
    public XboxDaemonController(
        XboxPrefillDaemonService daemonService,
        ILogger<XboxDaemonController> logger,
        StateService stateService,
        UserPreferencesService userPreferencesService)
        : base(daemonService, logger, stateService, userPreferencesService, "Xbox")
    {
    }

    protected override int? ResolveThreadLimit(UserSession session)
    {
        if (session.SessionType == SessionType.Admin) return null;
        return _stateService.GetDefaultGuestMaxThreadCount();
    }
}
