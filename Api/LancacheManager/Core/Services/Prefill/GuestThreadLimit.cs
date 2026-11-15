using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>Thread cap for a guest's prefill on one service: the session's own override, else the guest default for that service. Account holders have no cap.</summary>
public static class GuestThreadLimit
{
    public static int? Resolve(PrefillPlatform platform, UserSession session, UserPreferencesService preferencesService, StateService stateService)
    {
        if (session.SessionType.IsAccountHolder()) return null;
        var prefs = preferencesService.GetPreferences(session.Id);
        return platform switch
        {
            PrefillPlatform.Steam => prefs?.SteamMaxThreadCount ?? stateService.GetDefaultGuestMaxThreadCount(),
            PrefillPlatform.Epic => prefs?.EpicMaxThreadCount ?? stateService.GetEpicDefaultGuestMaxThreadCount(),
            PrefillPlatform.BattleNet => prefs?.BattleNetMaxThreadCount ?? stateService.GetBattleNetDefaultGuestMaxThreadCount(),
            PrefillPlatform.Riot => prefs?.RiotMaxThreadCount ?? stateService.GetRiotDefaultGuestMaxThreadCount(),
            PrefillPlatform.Xbox => prefs?.XboxMaxThreadCount ?? stateService.GetXboxDefaultGuestMaxThreadCount(),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
        };
    }
}
