namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Picks a UTC bucket width from the hours actually present, then fills every slot in the
/// window so a one-day event still has a readable series instead of a single daily point.
/// </summary>
internal static class SparklineBuckets
{
    /// <summary>
    /// The most buckets one series may carry. It has to stay above 81, the most any width produces
    /// across the span that selects it, so no picker range is ever trimmed. Past that it is a
    /// drawing bound: a line chart a few hundred pixels wide has nowhere to put more points. [3]
    /// </summary>
    private const int MaxBuckets = 1000;

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

    /// <summary>
    /// The start of the first bucket that begins at or after <paramref name="utc"/>. A partial
    /// bucket holds different amounts of real time per event, so equal offsets would not compare. [13]
    /// </summary>
    private static DateTime AlignForward(DateTime utc, int bucketMinutes)
    {
        var t = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var aligned = AlignStart(t, bucketMinutes);
        return aligned < t ? aligned.AddMinutes(bucketMinutes) : aligned;
    }

    /// <summary>
    /// The bucket-aligned window covering every row between <paramref name="firstUtc"/> and
    /// <paramref name="lastUtc"/>. The width comes from the hours that hold downloads, so the
    /// window must too: one taken from the picker gave 2,881 slots for six points. [3]
    /// </summary>
    public static (DateTime Start, DateTime End) CoveringWindow(
        DateTime firstUtc,
        DateTime lastUtc,
        int bucketMinutes)
    {
        return (
            AlignStart(firstUtc, bucketMinutes),
            AlignStart(lastUtc, bucketMinutes).AddMinutes(bucketMinutes)
        );
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

        // Only whole buckets are emitted: the one still filling at the window end holds less traffic
        // purely because less time has passed, and draws as a drop that never happened. [17]
        var end = DateTime.SpecifyKind(windowEndUtc, DateTimeKind.Utc);
        var start = AlignForward(windowStartUtc, bucketMinutes);
        var last = AlignStart(end.AddMinutes(-bucketMinutes), bucketMinutes);
        if (last < start)
        {
            // No whole bucket has closed, so the filling one carries everything and returning
            // nothing would read as "no traffic" for a window with downloads in it. The
            // under-scaling [17] guards against needs a neighbour, and this bucket has none.
            return [
                new Bucket(
                    start,
                    present.Sum(bucket => bucket.CacheHitBytes),
                    present.Sum(bucket => bucket.CacheMissBytes))
            ];
        }

        var byStart = new Dictionary<DateTime, Bucket>(present.Count);
        foreach (var bucket in present)
        {
            byStart[bucket.Start] = bucket;
        }

        var count = (int)((last - start).TotalMinutes / bucketMinutes) + 1;
        if (count > MaxBuckets)
        {
            // Drop the oldest buckets rather than the newest: a chart missing its most recent points
            // reads as an outage.
            start = last.AddMinutes(-(double)(MaxBuckets - 1) * bucketMinutes);
            count = MaxBuckets;
        }

        var filled = new List<Bucket>(count);
        var cursor = start;
        for (var i = 0; i < count; i++)
        {
            filled.Add(
                byStart.TryGetValue(cursor, out var hit) ? hit : new Bucket(cursor, 0, 0)
            );
            cursor = cursor.AddMinutes(bucketMinutes);
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
            var aligned = AlignForward(start, bucketMinutes);
            var last = AlignStart(
                DateTime.SpecifyKind(end, DateTimeKind.Utc).AddMinutes(-bucketMinutes),
                bucketMinutes);
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
        // Elapsed 0 is the event's first whole bucket, which is the first one Fill emits. Measuring
        // from the aligned start instead gave the event that began mid-bucket a shorter bucket 0. [13]
        var aligned = AlignForward(eventStart, bucketMinutes);
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
