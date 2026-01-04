namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Utility class for parsing time periods and intervals.
/// Centralizes time parsing logic used across controllers and repositories.
/// </summary>
public static class TimeUtils
{
    /// <summary>
    /// Parses a time period string and returns a cutoff DateTime.
    /// Supports formats like "15m", "1h", "24h", "7d", "30d", "1w", "1m", "1y", etc.
    /// </summary>
    /// <param name="period">The time period string (e.g., "24h", "7d", "1w")</param>
    /// <param name="now">The reference time (defaults to DateTime.UtcNow if not specified)</param>
    /// <returns>The cutoff DateTime, or null if period is null/empty/"all"</returns>
    public static DateTime? ParseTimePeriod(string? period, DateTime? now = null)
    {
        var referenceTime = now ?? DateTime.UtcNow;

        return period?.ToLower() switch
        {
            null or "" or "all" => null,
            "15m" => referenceTime.AddMinutes(-15),
            "30m" => referenceTime.AddMinutes(-30),
            "1h" => referenceTime.AddHours(-1),
            "6h" => referenceTime.AddHours(-6),
            "12h" => referenceTime.AddHours(-12),
            "24h" or "1d" => referenceTime.AddDays(-1),
            "48h" or "2d" => referenceTime.AddDays(-2),
            "7d" or "1w" => referenceTime.AddDays(-7),
            "14d" or "2w" => referenceTime.AddDays(-14),
            "30d" or "1m" => referenceTime.AddDays(-30),
            "90d" or "3m" => referenceTime.AddDays(-90),
            "365d" or "1y" => referenceTime.AddDays(-365),
            _ => null
        };
    }

    /// <summary>
    /// Parses a time period string and returns a cutoff DateTime.
    /// Returns DateTime.MinValue for "all" instead of null - useful for repository queries.
    /// </summary>
    /// <param name="period">The time period string (e.g., "24h", "7d")</param>
    /// <param name="now">The reference time</param>
    /// <param name="defaultValue">Default value if period is not recognized (defaults to 24h ago)</param>
    /// <returns>The cutoff DateTime</returns>
    public static DateTime GetCutoffTime(string period, DateTime now, DateTime? defaultValue = null)
    {
        var parsed = ParseTimePeriod(period, now);
        
        // Handle "all" case - return DateTime.MinValue to include all records
        if (period?.ToLower() == "all")
        {
            return DateTime.MinValue;
        }
        
        // Return parsed value, or default to 24 hours ago
        return parsed ?? defaultValue ?? now.AddHours(-24);
    }

    /// <summary>
    /// Parses an interval string and returns the number of minutes.
    /// Supports formats like "5min", "15min", "hourly", "1h", "daily", etc.
    /// </summary>
    /// <param name="interval">The interval string (e.g., "hourly", "15min", "1d")</param>
    /// <param name="defaultMinutes">Default value in minutes if interval is not recognized (defaults to 60)</param>
    /// <returns>The interval in minutes</returns>
    public static int ParseInterval(string interval, int defaultMinutes = 60)
    {
        return interval.ToLower() switch
        {
            "5min" => 5,
            "10min" => 10,
            "15min" => 15,
            "30min" => 30,
            "hourly" or "1h" => 60,
            "2h" => 120,
            "4h" => 240,
            "6h" => 360,
            "12h" => 720,
            "daily" or "1d" => 1440,
            _ => defaultMinutes
        };
    }
}
