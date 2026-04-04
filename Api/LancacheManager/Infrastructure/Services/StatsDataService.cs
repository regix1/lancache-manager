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
    private const string PrefillToken = "prefill";

    private readonly AppDbContext _context;
    private readonly ILogger<StatsDataService> _logger;

    public StatsDataService(AppDbContext context, ILogger<StatsDataService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get latest downloads with optional limit.
    /// Uses LEFT JOIN to resolve game names from SteamDepotMappings for downloads
    /// where the game name wasn't available at download time.
    /// </summary>
    /// <param name="limit">Maximum number of downloads to return</param>
    /// <param name="activeOnly">If true, only return active (in-progress) downloads</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<List<Download>> GetLatestDownloadsAsync(int limit = int.MaxValue, bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        // Start with base query applying prefill filter
        var baseQuery = _context.Downloads.AsNoTracking().ApplyPrefillFilter()
            .Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0);

        // Apply active-only filter if requested
        if (activeOnly)
        {
            baseQuery = baseQuery.Where(d => d.IsActive);
        }

        // LEFT JOIN with SteamDepotMappings to resolve missing game names at query time
        var query = from d in baseQuery
                    join m in _context.SteamDepotMappings.Where(mapping => mapping.IsOwner)
                        on d.DepotId equals m.DepotId into mappings
                    from mapping in mappings.DefaultIfEmpty()
                    orderby d.StartTimeUtc descending
                    select new
                    {
                        Download = d,
                        MappedAppName = mapping != null ? mapping.AppName : null,
                        MappedAppId = mapping != null ? (uint?)mapping.AppId : null
                    };

        var results = await query.Take(limit).ToListAsync(cancellationToken);

        var downloads = results.Select(r =>
        {
            var download = r.Download;

            // Fill in missing game info from mapping if available
            if (string.IsNullOrEmpty(download.GameName) && !string.IsNullOrEmpty(r.MappedAppName))
            {
                download.GameName = r.MappedAppName;
                download.GameAppId = r.MappedAppId;
            }

            // Calculate duration from EndTime - StartTime for proper JSON serialization
            if (download.EndTimeUtc != default(DateTime) && download.EndTimeUtc > download.StartTimeUtc)
            {
                download.DurationSeconds = (download.EndTimeUtc - download.StartTimeUtc).TotalSeconds;
            }

            return download;
        }).ToList();

        return downloads.WithUtcMarking();
    }
}
