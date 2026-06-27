using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Shared constants for the scheduled prefill orchestrator (Lane B).
/// </summary>
public static class ScheduledPrefillConstants
{
    /// <summary>
    /// Fixed pseudo-user id used for daemon sessions created by the scheduler.
    /// Matches the string-based <c>ScheduledPrefillAuthContext.UserId</c> contract.
    /// </summary>
    public const string SystemUserId = "scheduled-prefill-system";

    /// <summary>
    /// Stable order in which enabled services are executed by a scheduled run.
    /// </summary>
    public static readonly PrefillPlatform[] ServiceRunOrder =
    {
        PrefillPlatform.Steam,
        PrefillPlatform.Epic,
        PrefillPlatform.Xbox,
        PrefillPlatform.BattleNet,
        PrefillPlatform.Riot
    };

    /// <summary>
    /// Default per-service maximum runtime guard.
    /// </summary>
    public static readonly TimeSpan DefaultMaxServiceRuntime = TimeSpan.FromHours(12);

    /// <summary>
    /// Default stall timeout (no progress) guard.
    /// </summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Default count used by the "Top" preset.
    /// </summary>
    public const int DefaultTopCount = 50;
}
