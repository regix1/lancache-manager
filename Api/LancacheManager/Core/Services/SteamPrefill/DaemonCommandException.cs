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
                : errorCode switch
                {
                    "run-limit" => "The prefill daemon has no available run slots.",
                    "operation-conflict" => "This operation identifier already belongs to a different prefill.",
                    "operation-not-found" => "The daemon no longer has this prefill operation.",
                    "instance-changed" => "The prefill daemon restarted. Its previous runs were interrupted.",
                    "ambiguous-operation" => "Choose which prefill run to cancel.",
                    "outcome-unknown" => "The outcome of this prefill could not be recovered.",
                    _ => "The prefill daemon could not complete the request. Try again."
                })
    {
        ErrorCode = errorCode is "auth-lost" or "game-details-unavailable" or "run-limit"
            or "operation-conflict" or "operation-not-found" or "instance-changed"
            or "ambiguous-operation" or "outcome-unknown" ? errorCode : null;
        RequiresLogin = requiresLogin || ErrorCode == "auth-lost";
        StageKey = RequiresLogin ? "errors.steam.signInLost"
            : ErrorCode == "game-details-unavailable" ? "errors.steam.gameDetailsUnavailable"
            : ErrorCode switch
            {
                "run-limit" => "errors.prefill.runLimit",
                "operation-conflict" => "errors.prefill.operationConflict",
                "operation-not-found" => "errors.prefill.operationNotFound",
                "instance-changed" => "errors.prefill.instanceChanged",
                "ambiguous-operation" => "errors.prefill.ambiguousOperation",
                "outcome-unknown" => "errors.prefill.outcomeUnknown",
                _ => "errors.prefill.requestFailed"
            };
    }
}
