using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Pure gate logic for scheduled prefill runs — extracted for unit testing.
/// </summary>
public static class ScheduledPrefillRunGates
{
    /// <summary>
    /// Returns true when a scheduled run for this daemon should skip before creating a session.
    /// Persistent authenticated config containers must not block (Cause 2 / 19).
    /// </summary>
    public static bool ShouldSkipForBusySessions(
        IEnumerable<DaemonSession> sessions,
        Guid systemUserId,
        out string skipMessage)
    {
        var activeSessions = sessions
            .Where(s => s.Status == DaemonSessionStatus.Active)
            .ToList();

        if (activeSessions.Any(s => s.UserId != systemUserId))
        {
            skipMessage = "A manual prefill session is active";
            return true;
        }

        if (activeSessions.Any(s => s.IsPrefilling))
        {
            skipMessage = "A prefill is already in progress";
            return true;
        }

        skipMessage = string.Empty;
        return false;
    }
}
