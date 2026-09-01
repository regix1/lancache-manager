using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public partial class DashboardBatchService
{
    internal const int MaxCompareEvents = 8;

    public async Task<EventCompareResponse> GetEventCompareAsync(
        IReadOnlyList<long> eventIds,
        CancellationToken ct)
    {
        var requested = eventIds
            .Where(id => id > 0)
            .Distinct()
            .Take(MaxCompareEvents)
            .ToList();

        if (requested.Count == 0)
        {
            return new EventCompareResponse();
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(ct);
        var events = await context.Events
            .AsNoTracking()
            .Where(e => requested.Contains(e.Id))
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return new EventCompareResponse();
        }

        events.Sort((a, b) => requested.IndexOf(a.Id).CompareTo(requested.IndexOf(b.Id)));

        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();

        // EndTimeUtc is the planned end, so a running event carries a timestamp in the future and a
        // window that runs to it fills buckets for time that has not happened.
        var now = DateTime.UtcNow;
        var windows = new List<(DateTime Start, DateTime End)>(events.Count);
        foreach (var evt in events)
        {
            var plannedEnd = evt.EndTimeUtc.AsUtc();
            windows.Add((evt.StartTimeUtc.AsUtc(), plannedEnd < now ? plannedEnd : now));
        }

        var bucketMinutes = ResolveCompareBucketMinutes(windows);
        var elapsed = SparklineBuckets.SharedElapsedMinutes(windows, bucketMinutes);
        var taggedByEvent = await LoadTaggedDownloadIdsAsync(context, requested, ct);

        var series = new List<EventCompareSeries>(events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            var evt = events[index];
            var (start, end) = windows[index];
            HashSet<long> taggedIds = taggedByEvent.GetValueOrDefault(evt.Id) ?? [];
            // Same overlap-and-spread treatment as the stat-card sparklines: a download still
            // running when the event began served part of its bytes inside the window, and its
            // bytes belong to every bucket it was active in, not to the one it started in.
            var query = BuildBaseDownloadsQuery(context, hiddenClientIps, evictedMode)
                .ApplyEventFilter([evt.Id], taggedIds)
                .Where(d => (d.EndTimeUtc >= start || d.StartTimeUtc >= start) && d.StartTimeUtc <= end)
                .ApplyStatsExcludedClientFilter(statsExcludedOnlyIps);

            var spans = await query
                .Select(d => new HourOfDayBuckets.Span(d.StartTimeUtc, d.EndTimeUtc, d.CacheHitBytes, d.CacheMissBytes))
                .ToListAsync(ct);
            var present = SparklineBuckets.Spread(spans, bucketMinutes, start, end);
            var filled = SparklineBuckets.Fill(start, end, bucketMinutes, present);
            series.Add(
                new EventCompareSeries
                {
                    EventId = evt.Id,
                    Name = evt.Name,
                    ColorIndex = evt.ColorIndex,
                    Served = SparklineBuckets.ProjectElapsed(
                        filled,
                        start,
                        bucketMinutes,
                        elapsed,
                        b => b.CacheHitBytes + b.CacheMissBytes),
                    Saved = SparklineBuckets.ProjectElapsed(
                        filled,
                        start,
                        bucketMinutes,
                        elapsed,
                        b => b.CacheHitBytes),
                    Missed = SparklineBuckets.ProjectElapsed(
                        filled,
                        start,
                        bucketMinutes,
                        elapsed,
                        b => b.CacheMissBytes)
                });
        }

        return new EventCompareResponse
        {
            BucketMinutes = bucketMinutes,
            ElapsedMinutes = elapsed,
            Series = series
        };
    }

    /// <summary>
    /// The bucket width for a comparison, taken from how far the events have actually run rather
    /// than from the length they were booked for. A ten-day event four hours old holds four hours
    /// of traffic, and the width the booking selects leaves that window without a single whole
    /// bucket. Measuring the run keeps the width inside the window.
    /// </summary>
    internal static int ResolveCompareBucketMinutes(IReadOnlyList<(DateTime Start, DateTime End)> windows)
    {
        // An event that has not begun has its end clamped to now and a start after it, so the
        // longest run starts at zero rather than at that window's negative span.
        var longestHours = 0.0;
        foreach (var (start, end) in windows)
        {
            var hours = (end - start).TotalHours;
            if (hours > longestHours)
            {
                longestHours = hours;
            }
        }

        return SparklineBuckets.ResolveMinutes(longestHours);
    }

    /// <summary>
    /// The tagged download ids of every compared event, in one round trip. Asking per event cost a
    /// query and a pooled context each, on an endpoint re-requested whenever the selection
    /// changes.
    /// </summary>
    internal static async Task<Dictionary<long, HashSet<long>>> LoadTaggedDownloadIdsAsync(
        AppDbContext context,
        List<long> eventIds,
        CancellationToken ct)
    {
        var tagged = await context.EventDownloads
            .AsNoTracking()
            .Where(ed => eventIds.Contains(ed.EventId))
            .Select(ed => new { ed.EventId, ed.DownloadId })
            .ToListAsync(ct);

        return tagged
            .GroupBy(row => row.EventId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.DownloadId).ToHashSet());
    }
}
