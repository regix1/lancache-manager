namespace LancacheManager.Models;

/// <summary>Response for the admin "clear all persistent logins" action.</summary>
public sealed class ClearPersistentLoginsResponseDto
{
    /// <summary>Per-service outcome, one entry per registered daemon.</summary>
    public required List<ClearPersistentLoginServiceResultDto> Services { get; init; }
}

/// <summary>Per-service outcome of a "clear all logins" action.</summary>
public sealed class ClearPersistentLoginServiceResultDto
{
    /// <summary>Platform this result applies to.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>
    /// True when a persistent session was running for this service and was logged out in place;
    /// false when there was no running session and its auth volume was targeted instead.
    /// </summary>
    public required bool WasRunning { get; init; }

    /// <summary>
    /// True when the login was actually forgotten - the daemon acknowledged the logout (running case),
    /// or the auth volume was removed / was already absent (stopped case).
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Outcome detail. Running session: "logged-out" (in-place logout verified clean, container still
    /// running), "hard-removed" (escalated - container terminated and its named auth volume deleted),
    /// or "hard-remove-failed" (escalation ran but the volume could not be removed). Stopped/volume
    /// path: the raw <see cref="Core.Services.PersistentVolumeClearResult"/> name (e.g. "InUse").
    /// </summary>
    public string? Detail { get; init; }
}
