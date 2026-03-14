using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Controllers.Base;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Steam daemon sessions.
/// Uses secure encrypted credential exchange for authentication.
/// </summary>
[Route("api/steam-daemon")]
public class SteamDaemonController : DaemonControllerBase<SteamDaemonService>
{
    public SteamDaemonController(
        SteamDaemonService daemonService,
        ILogger<SteamDaemonController> logger,
        StateService stateService,
        UserPreferencesService userPreferencesService)
        : base(daemonService, logger, stateService, userPreferencesService, "Steam")
    {
    }

    protected override int? ResolveEffectiveThreadLimit(UserSession session)
    {
        if (session.SessionType == "admin") return null;
        var prefs = _userPreferencesService.GetPreferences(session.Id);
        return prefs?.SteamMaxThreadCount ?? _stateService.GetDefaultGuestMaxThreadCount();
    }
}
