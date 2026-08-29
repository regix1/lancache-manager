namespace LancacheManager.Core.Services;

/// <summary>
/// Human label for a dashboard stats period: "all" with no bounds, "18h"/"7d" for a bounded
/// range, "since 2026-08-01" with only a start. Shared by the dashboard batch and the stats
/// endpoint so their responses describe the same range the same way.
/// </summary>
internal static class DashboardPeriod
{
    public static string Label(DateTime? cutoffTime, DateTime? endDateTime)
    {
        if (cutoffTime.HasValue && endDateTime.HasValue)
        {
            var duration = endDateTime.Value - cutoffTime.Value;
            return duration.TotalHours <= 24 ? $"{(int)duration.TotalHours}h" : $"{(int)duration.TotalDays}d";
        }

        if (cutoffTime.HasValue)
        {
            return "since " + cutoffTime.Value.ToString("yyyy-MM-dd");
        }

        return "all";
    }
}
