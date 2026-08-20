using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Counts downloads into the 24 hours of the day on the reader's clock. A download is counted in
/// the hour it began, but its bytes are spread over the hours it was actually serving: a 50 GB
/// game that starts at 2:59 PM did not serve 50 GB in the 2 PM hour, and start-hour attribution
/// is how one large title invents a false peak.
/// </summary>
internal static class HourOfDayBuckets
{
    /// <summary>One download's active window and what it served over that window.</summary>
    internal sealed record Span(DateTime StartUtc, DateTime EndUtc, long CacheHitBytes, long CacheMissBytes);

    /// <summary>
    /// The 24 buckets, always complete and in hour order. <paramref name="rangeStartUtc"/> and
    /// <paramref name="rangeEndUtc"/> clip the bytes to the selected range: a download straddling
    /// a range edge contributes only the share it served inside it, and one that began before the
    /// range contributes bytes but no count, so the count keeps meaning "downloads started".
    /// </summary>
    internal static List<HourlyActivityItem> Build(
        IReadOnlyList<Span> spans,
        TimeZoneInfo zone,
        DateTime? rangeStartUtc,
        DateTime? rangeEndUtc)
    {
        var downloads = new int[24];
        var hitBytes = new double[24];
        var missBytes = new double[24];

        foreach (var span in spans)
        {
            var start = span.StartUtc;
            // An end that was never written, or sits behind the start, reads as instantaneous.
            var end = span.EndUtc > start ? span.EndUtc : start;

            var startedInRange = (rangeStartUtc is null || start >= rangeStartUtc)
                && (rangeEndUtc is null || start <= rangeEndUtc);
            if (startedInRange)
            {
                downloads[HourAt(start, zone)]++;
            }

            if (end == start)
            {
                if (startedInRange)
                {
                    var hour = HourAt(start, zone);
                    hitBytes[hour] += span.CacheHitBytes;
                    missBytes[hour] += span.CacheMissBytes;
                }
                continue;
            }

            var from = rangeStartUtc is { } lower && lower > start ? lower : start;
            var to = rangeEndUtc is { } upper && upper < end ? upper : end;
            if (to <= from)
            {
                // The active window lies entirely outside the range.
                continue;
            }

            var windowSeconds = (end - start).TotalSeconds;
            foreach (var (hour, seconds) in HourSlices(from, to, zone))
            {
                var share = seconds / windowSeconds;
                hitBytes[hour] += span.CacheHitBytes * share;
                missBytes[hour] += span.CacheMissBytes * share;
            }
        }

        return Enumerable.Range(0, 24)
            .Select(hour =>
            {
                var hit = (long)Math.Round(hitBytes[hour]);
                var miss = (long)Math.Round(missBytes[hour]);
                return new HourlyActivityItem
                {
                    Hour = hour,
                    Downloads = downloads[hour],
                    CacheHitBytes = hit,
                    CacheMissBytes = miss,
                    BytesServed = hit + miss
                };
            })
            .ToList();
    }

    private static int HourAt(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Hour;

    /// <summary>
    /// The window cut at the reader's hour boundaries, each piece labeled with its local hour.
    /// The step to the next boundary is recomputed from the zone at every piece, so a
    /// daylight-saving shift mid-window relabels the following piece instead of skewing it, and a
    /// half-hour zone like India cuts at its own :30 rather than at UTC's :00.
    /// </summary>
    private static IEnumerable<(int Hour, double Seconds)> HourSlices(
        DateTime fromUtc, DateTime toUtc, TimeZoneInfo zone)
    {
        var at = fromUtc;
        while (at < toUtc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(at, zone);
            var next = at.AddTicks(TimeSpan.TicksPerHour - local.TimeOfDay.Ticks % TimeSpan.TicksPerHour);
            if (next > toUtc)
            {
                next = toUtc;
            }
            yield return (local.Hour, (next - at).TotalSeconds);
            at = next;
        }
    }
}
