using LancacheManager.Middleware;

namespace LancacheManager.Core.Services.SteamPrefill;

public sealed class DaemonCommandException : ServiceUnavailableException
{
    public string? ErrorCode { get; }
    public bool RequiresLogin { get; }

    public DaemonCommandException(string? errorCode = null, bool requiresLogin = false)
        : base(errorCode == "auth-lost" || requiresLogin
            ? "Steam is no longer signed in. Sign in again, then retry the prefill."
            : errorCode == "game-details-unavailable"
                ? "Steam did not return game details. Try again."
                : "The prefill daemon could not complete the request. Try again.")
    {
        ErrorCode = errorCode is "auth-lost" or "game-details-unavailable" ? errorCode : null;
        RequiresLogin = requiresLogin || ErrorCode == "auth-lost";
        StageKey = RequiresLogin ? "errors.steam.signInLost"
            : ErrorCode == "game-details-unavailable" ? "errors.steam.gameDetailsUnavailable"
            : "errors.prefill.requestFailed";
    }
}
