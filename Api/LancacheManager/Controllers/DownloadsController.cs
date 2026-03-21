using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for download history
/// Handles retrieval of latest and active downloads
/// </summary>
[ApiController]
[Route("api/downloads")]
[Authorize]
public class DownloadsController : ControllerBase
{
    private const string PrefillToken = "prefill";

    private readonly AppDbContext _context;
    private readonly StatsDataService _statsService;
    private readonly IStateService _stateRepository;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        AppDbContext context,
        StatsDataService statsService,
        IStateService stateRepository,
        ILogger<DownloadsController> logger)
    {
        _context = context;
        _statsService = statsService;
        _stateRepository = stateRepository;
        _logger = logger;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestAsync([FromQuery] int count = int.MaxValue, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null, [FromQuery] int? eventId = null)
    {
        const int maxRetries = 3;

        // Convert single eventId to list for filtering
        var eventIdList = eventId.HasValue
            ? new List<int> { eventId.Value }
            : new List<int>();
        var excludedClientIps = _stateRepository.GetExcludedClientIps();

        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                List<Download> downloads;

                // If no time filtering and no event filter, use cached service method
                if (!startTime.HasValue && !endTime.HasValue && eventIdList.Count == 0)
                {
                    downloads = await _statsService.GetLatestDownloadsAsync(count);
                }
                else
                {
                    // With filtering, query database directly
                    // Database stores dates in UTC, so filter with UTC
                    var startDate = startTime.HasValue
                        ? startTime.Value.FromUnixSeconds()
                        : DateTime.MinValue;
                    var endDate = endTime.HasValue
                        ? endTime.Value.FromUnixSeconds()
                        : DateTime.UtcNow;

                    // Apply event filter if provided (filters to only tagged downloads)
                    if (eventIdList.Count > 0)
                    {
                        // Use subquery to atomically fetch downloads with event associations
                        // This eliminates the race condition by using a single query
                        downloads = await _context.Downloads
                            .AsNoTracking()
                            .Where(d => _context.EventDownloads
                                .Where(ed => eventIdList.Contains(ed.EventId))
                                .Select(ed => ed.DownloadId)
                                .Contains(d.Id))
                            .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                            .OrderByDescending(d => d.StartTimeUtc)
                            .Take(count)
                            .ToListAsync();
                    }
                    else
                    {
                        downloads = await _context.Downloads
                            .AsNoTracking()
                            .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                            .OrderByDescending(d => d.StartTimeUtc)
                            .Take(count)
                            .ToListAsync();
                    }

                    downloads.WithUtcMarking();
                }

                // Apply exclusion filter to ALL downloads (both cached and direct query paths)
                if (excludedClientIps.Count > 0)
                {
                    downloads = downloads
                        .Where(d => !excludedClientIps.Contains(d.ClientIp))
                        .ToList();
                }

                // Filter out prefill sessions (safety net - StatsDataService already filters, but direct queries may not)
                downloads = downloads
                    .Where(d => !string.Equals(d.ClientIp, PrefillToken, StringComparison.OrdinalIgnoreCase))
                    .Where(d => !string.Equals(d.Datasource, PrefillToken, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Return just the array - frontend will use array.length for actual count
                return Ok(downloads);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                if (retry < maxRetries - 1)
                {
                    _logger.LogWarning($"Database busy, retrying... (attempt {retry + 1}/{maxRetries})");
                    await Task.Delay(100 * (retry + 1)); // Exponential backoff
                    continue;
                }
                _logger.LogError(ex, "Database locked after retries");
                return Ok(new List<Download>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting latest downloads");
                return Ok(new List<Download>());
            }
        }
        return Ok(new List<Download>());
    }

    /// <summary>
    /// Get a download by ID with its tags and events
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetByIdAsync(int id)
    {
        var download = await _context.Downloads
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id && d.ClientIp != PrefillToken && d.ClientIp != "Prefill")
            ?? throw new NotFoundException("Download");

        var excludedClientIps = _stateRepository.GetExcludedClientIps();
        if (excludedClientIps.Contains(download.ClientIp))
        {
            throw new NotFoundException("Download");
        }

        if (string.Equals(download.ClientIp, PrefillToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(download.Datasource, PrefillToken, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotFoundException("Download");
        }

        download.WithUtcMarking();

        // Get events for this download
        var eventDownloads = await _context.EventDownloads
            .AsNoTracking()
            .Include(ed => ed.Event)
            .Where(ed => ed.DownloadId == id)
            .ToListAsync();

        var events = eventDownloads.Select(ed => new
        {
            ed.Event.Id,
            ed.Event.Name,
            ed.Event.ColorIndex,
            ed.Event.StartTimeUtc,
            ed.Event.EndTimeUtc,
            ed.AutoTagged,
            ed.TaggedAtUtc
        }).ToList();

        return Ok(new
        {
            download,
            events
        });
    }

    /// <summary>
    /// Get events for multiple download IDs in a single batch request
    /// </summary>
    [HttpPost("batch-download-events")]
    public async Task<IActionResult> GetBatchDownloadEventsAsync([FromBody] BatchDownloadEventsRequest request)
    {
        if (request.DownloadIds == null || request.DownloadIds.Count == 0)
        {
            return Ok(new Dictionary<int, object>());
        }

        // Limit to prevent abuse
        const int maxIds = 500;
        var downloadIds = request.DownloadIds.Take(maxIds).ToList();

        // Get all events for these downloads in a single query
        var eventDownloads = await _context.EventDownloads
            .AsNoTracking()
            .Include(ed => ed.Event)
            .Where(ed => downloadIds.Contains(ed.DownloadId))
            .ToListAsync();

        // Group by download ID and return as dictionary
        var result = downloadIds.ToDictionary(
            id => id,
            id => new
            {
                events = eventDownloads
                    .Where(ed => ed.DownloadId == id)
                    .Select(ed => new
                    {
                        ed.Event.Id,
                        ed.Event.Name,
                        ed.Event.ColorIndex,
                        ed.AutoTagged
                    })
                    .ToList()
            }
        );

        return Ok(result);
    }

    /// <summary>
    /// Get downloads with their tags and events for a time range
    /// </summary>
    [HttpGet("with-associations")]
    public async Task<IActionResult> GetWithAssociationsAsync(
        [FromQuery] int count = 100,
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        var excludedClientIps = _stateRepository.GetExcludedClientIps();
        var startDate = startTime.HasValue
            ? startTime.Value.FromUnixSeconds()
            : DateTime.MinValue;
        var endDate = endTime.HasValue
            ? endTime.Value.FromUnixSeconds()
            : DateTime.UtcNow;

        // Use a single query with projection to atomically fetch downloads and their event associations
        // This eliminates the race condition by avoiding separate queries
        var downloadsWithEvents = await _context.Downloads
            .AsNoTracking()
            .Where(d => excludedClientIps.Count == 0 || !excludedClientIps.Contains(d.ClientIp))
            .Where(d => d.ClientIp != PrefillToken && d.ClientIp != "Prefill")
            .Where(d => d.Datasource != PrefillToken && d.Datasource != "Prefill")
            .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
            .OrderByDescending(d => d.StartTimeUtc)
            .Take(count)
            .Select(d => new
            {
                Download = d,
                Events = _context.EventDownloads
                    .AsNoTracking()
                    .Where(ed => ed.DownloadId == d.Id)
                    .Select(ed => new
                    {
                        ed.Event.Id,
                        ed.Event.Name,
                        ed.Event.ColorIndex,
                        ed.AutoTagged
                    })
                    .ToList()
            })
            .ToListAsync();

        if (downloadsWithEvents.Count == 0)
        {
            return Ok(new List<object>());
        }

        // Mark timestamps as UTC and build response
        var result = downloadsWithEvents.Select(item =>
        {
            item.Download.WithUtcMarking();
            return new
            {
                download = item.Download,
                events = (object)item.Events
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Get paginated, grouped download data for the Retro view.
    /// Groups downloads by DepotId + ClientIp and aggregates cache statistics.
    /// Resolves game names via LEFT JOIN with SteamDepotMappings and EpicGameMappings.
    /// </summary>
    [HttpGet("retro")]
    public async Task<ActionResult<RetroDownloadResponse>> GetRetroDownloadsAsync([FromQuery] RetroDownloadQuery query)
    {
        const int maxRetries = 3;
        const int maxPageSize = 200;

        // Clamp page size
        query.PageSize = Math.Clamp(query.PageSize, 1, maxPageSize);
        if (query.Page < 1) query.Page = 1;

        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                var excludedClientIps = _stateRepository.GetExcludedClientIps();

                // Base query: filter prefill sessions
                var baseQuery = _context.Downloads
                    .AsNoTracking()
                    .Where(d => d.ClientIp == null || d.ClientIp.ToLower() != PrefillToken)
                    .Where(d => d.Datasource == null || d.Datasource.ToLower() != PrefillToken)
                    .Where(d => !d.IsActive); // Only completed downloads for retro view

                // Exclude hidden client IPs
                if (excludedClientIps.Count > 0)
                {
                    baseQuery = baseQuery.Where(d => !excludedClientIps.Contains(d.ClientIp));
                }

                // Filter: hide localhost
                if (query.HideLocalhost)
                {
                    baseQuery = baseQuery.Where(d => d.ClientIp != "127.0.0.1" && d.ClientIp != "::1");
                }

                // Filter: service
                if (!string.IsNullOrEmpty(query.Service) && query.Service != "all")
                {
                    baseQuery = baseQuery.Where(d => d.Service == query.Service);
                }

                // Filter: client IP
                if (!string.IsNullOrEmpty(query.Client) && query.Client != "all")
                {
                    baseQuery = baseQuery.Where(d => d.ClientIp == query.Client);
                }

                // Filter: hide zero-byte downloads
                if (!query.ShowZeroBytes)
                {
                    baseQuery = baseQuery.Where(d => (d.CacheHitBytes + d.CacheMissBytes) > 0);
                }

                // LEFT JOIN with SteamDepotMappings to resolve missing game names
                var withSteamMapping = from d in baseQuery
                                       join sm in _context.SteamDepotMappings.Where(m => m.IsOwner)
                                           on d.DepotId equals sm.DepotId into steamMappings
                                       from sm in steamMappings.DefaultIfEmpty()
                                       select new
                                       {
                                           Download = d,
                                           SteamAppName = sm != null ? sm.AppName : null,
                                           SteamAppId = sm != null ? (uint?)sm.AppId : null
                                       };

                // Materialize to memory for grouping with Epic resolution
                var allDownloads = await withSteamMapping.ToListAsync();

                // Build Epic game name lookup for Epic downloads
                var epicAppIds = allDownloads
                    .Where(r => !string.IsNullOrEmpty(r.Download.EpicAppId))
                    .Select(r => r.Download.EpicAppId!)
                    .Distinct()
                    .ToList();

                var epicMappings = epicAppIds.Count > 0
                    ? await _context.EpicGameMappings
                        .AsNoTracking()
                        .Where(m => epicAppIds.Contains(m.AppId))
                        .ToDictionaryAsync(m => m.AppId, m => m.Name)
                    : new Dictionary<string, string>();

                // Resolve game names and project to flat structure
                var resolved = allDownloads.Select(r =>
                {
                    var d = r.Download;
                    var gameName = d.GameName;
                    var gameAppId = d.GameAppId;

                    // Fill from Steam mapping if missing
                    if (string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(r.SteamAppName))
                    {
                        gameName = r.SteamAppName;
                        gameAppId = r.SteamAppId;
                    }

                    // Fill from Epic mapping if still missing
                    if (string.IsNullOrEmpty(gameName) && !string.IsNullOrEmpty(d.EpicAppId)
                        && epicMappings.TryGetValue(d.EpicAppId, out var epicName))
                    {
                        gameName = epicName;
                    }

                    return new
                    {
                        d.Id,
                        d.DepotId,
                        d.EpicAppId,
                        d.ClientIp,
                        d.Service,
                        d.Datasource,
                        d.StartTimeUtc,
                        d.EndTimeUtc,
                        d.CacheHitBytes,
                        d.CacheMissBytes,
                        TotalBytes = d.CacheHitBytes + d.CacheMissBytes,
                        GameName = gameName ?? d.Service,
                        GameAppId = gameAppId,
                        AverageBytesPerSecond = d.AverageBytesPerSecond
                    };
                }).ToList();

                // Filter: search by game name
                if (!string.IsNullOrEmpty(query.Search))
                {
                    var searchLower = query.Search.ToLower();
                    resolved = resolved
                        .Where(r => r.GameName.ToLower().Contains(searchLower)
                                 || r.Service.ToLower().Contains(searchLower)
                                 || (r.DepotId.HasValue && r.DepotId.Value.ToString().Contains(searchLower))
                                 || r.ClientIp.Contains(searchLower))
                        .ToList();
                }

                // Filter: hide unknown games
                if (query.HideUnknown)
                {
                    resolved = resolved
                        .Where(r => !string.IsNullOrEmpty(r.GameName)
                                 && r.GameName != r.Service
                                 && !r.GameName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // Group by DepotId + ClientIp (same logic as frontend groupByDepot)
                var grouped = resolved
                    .GroupBy(r => r.DepotId.HasValue
                        ? $"depot-{r.DepotId}-{r.ClientIp}"
                        : $"no-depot-{r.Service}-{r.ClientIp}-{r.Id}")
                    .Select(g =>
                    {
                        var first = g.First();
                        var totalBytes = g.Sum(r => r.TotalBytes);
                        var cacheHitBytes = g.Sum(r => r.CacheHitBytes);
                        var cacheMissBytes = g.Sum(r => r.CacheMissBytes);
                        var cacheHitPercent = totalBytes > 0 ? (cacheHitBytes * 100.0) / totalBytes : 0;

                        // Weighted average speed (weight by bytes downloaded)
                        var weightedSpeedSum = g.Sum(r => r.AverageBytesPerSecond * r.TotalBytes);
                        var speedBytesSum = g.Where(r => r.AverageBytesPerSecond > 0 && r.TotalBytes > 0)
                            .Sum(r => (double)r.TotalBytes);
                        var avgSpeed = speedBytesSum > 0 ? weightedSpeedSum / speedBytesSum : 0;

                        return new RetroDownloadDto
                        {
                            Id = g.Key,
                            DepotId = first.DepotId,
                            EpicAppId = first.EpicAppId,
                            ClientIp = first.ClientIp,
                            Service = first.Service,
                            Datasource = first.Datasource,
                            AppName = first.GameName,
                            SteamAppId = first.GameAppId,
                            StartTimeUtc = g.Min(r => r.StartTimeUtc),
                            EndTimeUtc = g.Max(r => r.EndTimeUtc),
                            CacheHitBytes = cacheHitBytes,
                            CacheMissBytes = cacheMissBytes,
                            CacheHitPercent = Math.Round(cacheHitPercent, 1),
                            TotalBytes = totalBytes,
                            AverageBytesPerSecond = avgSpeed,
                            RequestCount = g.Count(),
                            DownloadIds = g.Select(r => r.Id).ToList()
                        };
                    })
                    .ToList();

                // Sort
                grouped = query.Sort switch
                {
                    "oldest" => grouped.OrderBy(g => g.StartTimeUtc).ToList(),
                    "largest" => grouped.OrderByDescending(g => g.TotalBytes).ToList(),
                    "smallest" => grouped.OrderBy(g => g.TotalBytes).ToList(),
                    "efficiency" => grouped.OrderByDescending(g => g.CacheHitPercent).ToList(),
                    "efficiency-low" => grouped.OrderBy(g => g.CacheHitPercent).ToList(),
                    "sessions" => grouped.OrderByDescending(g => g.RequestCount).ToList(),
                    "alphabetical" => grouped.OrderBy(g => g.AppName, StringComparer.OrdinalIgnoreCase).ToList(),
                    "service" => grouped.OrderBy(g => g.Service).ThenByDescending(g => g.EndTimeUtc).ToList(),
                    _ => grouped.OrderByDescending(g => g.EndTimeUtc).ToList(), // "latest" default
                };

                // Paginate
                var totalItems = grouped.Count;
                var totalPages = (int)Math.Ceiling((double)totalItems / query.PageSize);
                var items = grouped
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .ToList();

                return Ok(new RetroDownloadResponse
                {
                    Items = items,
                    TotalItems = totalItems,
                    TotalPages = totalPages,
                    CurrentPage = query.Page,
                    PageSize = query.PageSize
                });
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                if (retry < maxRetries - 1)
                {
                    _logger.LogWarning("Database busy on retro query, retrying... (attempt {Attempt}/{MaxRetries})", retry + 1, maxRetries);
                    await Task.Delay(100 * (retry + 1));
                    continue;
                }
                _logger.LogError(ex, "Database locked after retries on retro query");
                return Ok(new RetroDownloadResponse());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting retro downloads");
                return Ok(new RetroDownloadResponse());
            }
        }
        return Ok(new RetroDownloadResponse());
    }
}
