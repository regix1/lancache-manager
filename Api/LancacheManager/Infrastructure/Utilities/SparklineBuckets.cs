namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Picks a UTC bucket width from the hours actually present, then fills every slot in the
/// window so a one-day event still has a readable series instead of a single daily point.
/// </summary>
internal static class SparklineBuckets
{
    public readonly record struct Bucket(DateTime Start, long CacheHitBytes, long CacheMissBytes);

    public static int ResolveMinutes(double rangeHours)
    {
        if (rangeHours <= 2)
        {
            return 15;
        }

        if (rangeHours <= 13)
        {
            return 30;
        }

        if (rangeHours <= 25)
        {
            return 60;
        }

        if (rangeHours <= 240)
        {
            return 180;
        }

        return 1440;
    }

    public static DateTime AlignStart(DateTime utc, int bucketMinutes)
    {
        var t = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (bucketMinutes >= 1440)
        {
            return new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc);
        }

        if (bucketMinutes >= 60)
        {
            var hours = bucketMinutes / 60;
            var hour = t.Hour / hours * hours;
            return new DateTime(t.Year, t.Month, t.Day, hour, 0, 0, DateTimeKind.Utc);
        }

        var minute = t.Minute / bucketMinutes * bucketMinutes;
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, minute, 0, DateTimeKind.Utc);
    }

    public static List<Bucket> Fill(
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        int bucketMinutes,
        IReadOnlyCollection<Bucket> present)
    {
        if (present.Count == 0 || bucketMinutes <= 0)
        {
            return [];
        }

        var byStart = new Dictionary<DateTime, Bucket>(present.Count);
        foreach (var bucket in present)
        {
            byStart[bucket.Start] = bucket;
        }

        var start = AlignStart(windowStartUtc, bucketMinutes);
        var end = DateTime.SpecifyKind(windowEndUtc, DateTimeKind.Utc);

        var filled = new List<Bucket>();
        for (var cursor = start; cursor <= end; cursor = cursor.AddMinutes(bucketMinutes))
        {
            filled.Add(
                byStart.TryGetValue(cursor, out var hit) ? hit : new Bucket(cursor, 0, 0)
            );
        }

        return filled;
    }

    public static List<int> SharedElapsedMinutes(
        IEnumerable<(DateTime Start, DateTime End)> windows,
        int bucketMinutes)
    {
        var max = 0;
        foreach (var (start, end) in windows)
        {
            var aligned = AlignStart(start, bucketMinutes);
            var last = AlignStart(end, bucketMinutes);
            if (last < aligned)
            {
                last = aligned;
            }

            var span = (int)(last - aligned).TotalMinutes;
            if (span > max)
            {
                max = span;
            }
        }

        var points = new List<int>();
        for (var minutes = 0; minutes <= max; minutes += bucketMinutes)
        {
            points.Add(minutes);
        }

        return points;
    }

    public static List<double?> ProjectElapsed(
        IReadOnlyList<Bucket> filled,
        DateTime eventStart,
        int bucketMinutes,
        IReadOnlyList<int> sharedElapsed,
        Func<Bucket, long> value)
    {
        var aligned = AlignStart(eventStart, bucketMinutes);
        var byElapsed = new Dictionary<int, long>(filled.Count);
        var lastElapsed = -1;
        foreach (var bucket in filled)
        {
            var elapsed = (int)(bucket.Start - aligned).TotalMinutes;
            byElapsed[elapsed] = value(bucket);
            if (elapsed > lastElapsed)
            {
                lastElapsed = elapsed;
            }
        }

        var projected = new List<double?>(sharedElapsed.Count);
        foreach (var minutes in sharedElapsed)
        {
            if (minutes > lastElapsed)
            {
                projected.Add(null);
            }
            else if (byElapsed.TryGetValue(minutes, out var amount))
            {
                projected.Add(amount);
            }
            else
            {
                projected.Add(0);
            }
        }

        return projected;
    }
}
