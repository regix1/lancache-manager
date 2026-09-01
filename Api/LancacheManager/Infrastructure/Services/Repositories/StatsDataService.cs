using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;


namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for statistics database queries
/// Queries Downloads table directly for consistent data (no caching)
/// </summary>
public class StatsDataService : IStatsDataService
{
    private readonly AppDbContext _context;

    public StatsDataService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Get latest downloads with optional limit.
    /// Resolves game names from SteamDepotMappings for downloads where the name wasn't
    /// available at download time, after the page has been chosen.
    /// </summary>
    /// <param name="limit">Maximum number of downloads to return. Required, so that asking for the
    /// whole table is always a deliberate choice at the call site.</param>
    /// <param name="activeOnly">If true, only return active (in-progress) downloads</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<List<Download>> GetLatestDownloadsAsync(int limit, bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        var baseQuery = _context.Downloads.AsNoTracking()
            .ApplyEmptySessionFilter()
            .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0);

        // Apply active-only filter if requested
        if (activeOnly)
        {
            baseQuery = baseQuery.Where(d => d.IsActive);
        }

        var downloads = await baseQuery
            .OrderByDescending(d => d.StartTimeUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        // Look the owner mappings up separately rather than joining them in SQL. A depot can carry
        // more than one owner row (prefill used to claim shared and DLC depots), and a join would
        // then emit the download once per owner row, double-counting its bytes in the client-side
        // totals and spending two of the limit's slots on one download.
        var unnamedDepotIds = downloads
            .Where(d => string.IsNullOrEmpty(d.GameName) && d.DepotId.HasValue)
            .Select(d => d.DepotId!.Value)
            .Distinct()
            .ToList();

        var depotOwners = unnamedDepotIds.Count > 0
            ? (await _context.SteamDepotMappings.AsNoTracking()
                    .Where(m => m.IsOwner && unnamedDepotIds.Contains(m.DepotId))
                    .ToListAsync(cancellationToken))
                .GroupBy(m => m.DepotId)
                .ToDictionary(group => group.Key, SteamDepotMapping.SelectOwner)
            : [];

        foreach (var download in downloads)
        {
            // Fill in missing game info from mapping if available
            if (string.IsNullOrEmpty(download.GameName)
                && download.DepotId.HasValue
                && depotOwners.TryGetValue(download.DepotId.Value, out var owner)
                && !string.IsNullOrEmpty(owner.AppName))
            {
                download.GameName = owner.AppName;
                download.GameAppId = owner.AppId;
            }

            // Calculate duration from EndTime - StartTime for proper JSON serialization
            if (download.EndTimeUtc != default(DateTime) && download.EndTimeUtc > download.StartTimeUtc)
            {
                download.DurationSeconds = (download.EndTimeUtc - download.StartTimeUtc).TotalSeconds;
            }
        }

        return downloads.WithUtcMarking();
    }
}
