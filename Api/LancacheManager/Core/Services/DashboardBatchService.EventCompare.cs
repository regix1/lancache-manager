using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models.Responses;
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

        var longestHours = events.Max(e => Math.Max((e.EndTimeUtc - e.StartTimeUtc).TotalHours, 0));
        var bucketMinutes = SparklineBuckets.ResolveMinutes(longestHours);
        var elapsed = SparklineBuckets.SharedElapsedMinutes(
            events.Select(e => (e.StartTimeUtc.AsUtc(), e.EndTimeUtc.AsUtc())),
            bucketMinutes);

        var series = new List<EventCompareSeries>(events.Count);
        foreach (var evt in events)
        {
            var start = evt.StartTimeUtc.AsUtc();
            var end = evt.EndTimeUtc.AsUtc();
            var taggedIds = await GetEventDownloadIdsAsync([evt.Id], ct);
            await using var eventContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var query = BuildBaseDownloadsQuery(eventContext, hiddenClientIps, evictedMode)
                .ApplyEventFilter([evt.Id], taggedIds)
                .Where(d => d.StartTimeUtc >= start && d.StartTimeUtc <= end);
            if (statsExcludedOnlyIps.Count > 0)
            {
                query = query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));
            }

            var present = await LoadPresentBucketsAsync(query, bucketMinutes, ct);
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
                        b => b.CacheHitBytes)
                });
        }

        return new EventCompareResponse
        {
            BucketMinutes = bucketMinutes,
            ElapsedMinutes = elapsed,
            Series = series
        };
    }
}
