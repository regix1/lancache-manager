using LancacheManager.Middleware;

namespace LancacheManager.Core.Services.SteamPrefill;

public sealed class DaemonCommandException : ServiceUnavailableException
{
    public string? ErrorCode { get; }
    public bool RequiresLogin { get; }

    public DaemonCommandException(
        string? errorCode = null,
        bool requiresLogin = false,
        string loginStageKey = "errors.steam.signInLost")
        : base(ResolveMessage(errorCode, requiresLogin, loginStageKey))
    {
        ErrorCode = errorCode is "auth-lost" or "game-details-unavailable" or "run-limit"
            or "operation-conflict" or "operation-not-found" or "instance-changed"
            or "ambiguous-operation" or "outcome-unknown" ? errorCode : null;
        RequiresLogin = requiresLogin || ErrorCode == "auth-lost";
        StageKey = RequiresLogin
            ? loginStageKey
            : ErrorCode == "game-details-unavailable" ? "errors.steam.gameDetailsUnavailable"
            : StageKeyForCode(ErrorCode) switch { { } key => key, null => "errors.prefill.requestFailed" };
    }

    private static string ResolveMessage(string? errorCode, bool requiresLogin, string loginStageKey)
    {
        if (errorCode == "auth-lost" || requiresLogin)
        {
            return loginStageKey == "errors.prefill.signInLost"
                ? "The service account was signed out before this finished. An always-on container may sign in again by itself with its saved login. If it is still signed out, log in again, then run it again."
                : "Steam is no longer signed in. Sign in again, then retry the prefill.";
        }

        return errorCode == "game-details-unavailable"
            ? "Steam did not return game details. Try again."
            : errorCode switch
            {
                "run-limit" => "The prefill daemon has no available run slots.",
                "operation-conflict" => "This operation identifier already belongs to a different prefill.",
                "operation-not-found" => "The daemon no longer has this prefill operation.",
                "instance-changed" => "The prefill daemon restarted. Its previous runs were interrupted.",
                "ambiguous-operation" => "Choose which prefill run to cancel.",
                "outcome-unknown" => "The outcome of this prefill could not be recovered.",
                _ => "The prefill daemon could not complete the request. Try again."
            };
    }

    internal static string? StageKeyForCode(string? errorCode) => errorCode switch
    {
        "run-limit" => "errors.prefill.runLimit",
        "operation-conflict" => "errors.prefill.operationConflict",
        "operation-not-found" => "errors.prefill.operationNotFound",
        "instance-changed" => "errors.prefill.instanceChanged",
        "ambiguous-operation" => "errors.prefill.ambiguousOperation",
        "outcome-unknown" => "errors.prefill.outcomeUnknown",
        _ => null
    };
}
