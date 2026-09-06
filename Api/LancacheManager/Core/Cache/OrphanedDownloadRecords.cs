using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Cache;

/// <summary>
/// Download records that no log entry backs any more, typically left behind by a log purge, a
/// cache clear or a reprocess. The eviction scan probes a download through its log entries, so
/// these rows can never be verified or flagged evicted; they are listed here, grouped the way the
/// Downloads page groups them, so the user can remove the ones they do not want to keep.
/// </summary>
public static class OrphanedDownloadRecords
{
    /// <summary>
    /// A download the log processor is still filling in has no log entries for a moment; rows that
    /// ended inside this window are left alone.
    /// </summary>
    private static readonly TimeSpan _settleWindow = TimeSpan.FromMinutes(5);

    public static async Task<List<OrphanedDownloadGroup>> ListAsync(AppDbContext context, CancellationToken ct)
    {
        var rows = await Orphans(context)
            .Select(d => new OrphanRow(
                d.Id,
                d.Service,
                d.GameAppId,
                d.DepotId,
                d.EpicAppId,
                d.XboxProductId,
                d.GameName,
                d.EndTimeUtc,
                d.CacheHitBytes + d.CacheMissBytes))
            .ToListAsync(ct);

        var groups = rows
            .GroupBy(r => (r.Service, r.GameAppId, r.EpicAppId, r.XboxProductId, r.GameName))
            .Select(g => new GroupNameRow
            {
                Service = g.Key.Service,
                GameAppId = g.Key.GameAppId,
                DepotId = g.First().DepotId,
                EpicAppId = g.Key.EpicAppId,
                XboxProductId = g.Key.XboxProductId,
                GameName = g.Key.GameName,
                Rows = g.ToList()
            })
            .ToList();

        // Same read-time resolution as every Downloads view, so a row whose stored name was wiped
        // still lists under its title rather than under a bare service tag.
        await GameNameResolver.ResolveAsync(context, groups, ct);

        return groups
            .Select(g => new OrphanedDownloadGroup
            {
                Key = string.Join('|', g.Service, g.GameAppId, g.EpicAppId, g.XboxProductId, g.Rows[0].GameName),
                Service = g.Service,
                GameName = g.GameName ?? g.Service,
                DownloadCount = g.Rows.Count,
                TotalBytes = g.Rows.Sum(r => r.TotalBytes),
                LastSeenUtc = g.Rows.Max(r => r.EndTimeUtc),
                DownloadIds = g.Rows.Select(r => r.Id).ToList()
            })
            .OrderByDescending(g => g.TotalBytes)
            .ToList();
    }

    /// <summary>
    /// Deletes the chosen rows, re-checking each one still has no log entries: a row that gained
    /// history between the listing and the click is a real download again and stays.
    /// </summary>
    public static async Task<int> RemoveAsync(AppDbContext context, IReadOnlyCollection<long> downloadIds, CancellationToken ct)
    {
        var removed = 0;
        foreach (var batch in downloadIds.Chunk(500))
        {
            var rows = await Orphans(context)
                .Where(d => batch.Contains(d.Id))
                .ToListAsync(ct);
            context.Downloads.RemoveRange(rows);
            removed += rows.Count;
        }

        if (removed > 0)
        {
            await context.SaveChangesAsync(ct);
        }

        return removed;
    }

    // Evicted rows are excluded on purpose: they already have their own removal path (Remove
    // evicted deletes their detection rows too), and deleting them here would strand those rows
    // in the Evicted Items list.
    private static IQueryable<Download> Orphans(AppDbContext context)
    {
        var settledBeforeUtc = DateTime.UtcNow - _settleWindow;
        return context.Downloads.Where(d => !d.IsActive
                                          && !d.IsEvicted
                                          && d.EndTimeUtc < settledBeforeUtc
                                          && !context.LogEntries.Any(le => le.DownloadId == d.Id));
    }

    private sealed record OrphanRow(
        long Id,
        string Service,
        long? GameAppId,
        long? DepotId,
        string? EpicAppId,
        string? XboxProductId,
        string? GameName,
        DateTime EndTimeUtc,
        long TotalBytes);

    private sealed class GroupNameRow : IGameNameRow
    {
        public required string Service { get; init; }
        public long? GameAppId { get; set; }
        public long? DepotId { get; init; }
        public string? EpicAppId { get; init; }
        public string? XboxProductId { get; init; }
        public string? GameName { get; set; }
        public required List<OrphanRow> Rows { get; init; }
    }
}
