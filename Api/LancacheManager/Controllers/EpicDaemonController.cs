using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Controllers.Base;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Epic daemon sessions.
/// Uses secure encrypted credential exchange for authentication.
/// </summary>
[Route("api/epic-daemon")]
public class EpicDaemonController : DaemonControllerBase<EpicPrefillDaemonService>
{
    public EpicDaemonController(
        EpicPrefillDaemonService daemonService,
        ILogger<EpicDaemonController> logger,
        StateService stateService,
        UserPreferencesService userPreferencesService)
        : base(daemonService, logger, stateService, userPreferencesService, "Epic")
    {
    }

    protected override int? ResolveEffectiveThreadLimit(UserSession session)
    {
        if (session.SessionType == "admin") return null;
        var prefs = _userPreferencesService.GetPreferences(session.Id);
        return prefs?.EpicMaxThreadCount ?? _stateService.GetEpicDefaultGuestMaxThreadCount();
    }
}
