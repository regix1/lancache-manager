using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Core.Interfaces.Repositories;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for download history
/// Handles retrieval of latest and active downloads
/// </summary>
[ApiController]
[Route("api/downloads")]
public class DownloadsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly StatsRepository _statsService;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        AppDbContext context,
        StatsRepository statsService,
        ILogger<DownloadsController> logger)
    {
        _context = context;
        _statsService = statsService;
        _logger = logger;
    }

    [HttpGet("latest")]
    [RequireGuestSession]
    [ResponseCache(Duration = 5)] // Cache for 5 seconds
    public async Task<IActionResult> GetLatest([FromQuery] int count = int.MaxValue, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null, [FromQuery] int? eventId = null)
    {
        const int maxRetries = 3;

        // Convert single eventId to list for filtering
        var eventIdList = eventId.HasValue
            ? new List<int> { eventId.Value }
            : new List<int>();

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
                        ? DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime
                        : DateTime.MinValue;
                    var endDate = endTime.HasValue
                        ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime
                        : DateTime.UtcNow;

                    // Apply event filter if provided (filters to only tagged downloads)
                    if (eventIdList.Count > 0)
                    {
                        // Use explicit join for event filtering - support multiple event IDs
                        var taggedDownloadIds = await _context.EventDownloads
                            .AsNoTracking()
                            .Where(ed => eventIdList.Contains(ed.EventId))
                            .Select(ed => ed.DownloadId)
                            .Distinct()
                            .ToListAsync();

                        downloads = await _context.Downloads
                            .AsNoTracking()
                            .Where(d => taggedDownloadIds.Contains(d.Id))
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

                    // Fix timezone: Ensure UTC DateTime values are marked as UTC for proper JSON serialization
                    foreach (var download in downloads)
                    {
                        download.StartTimeUtc = download.StartTimeUtc.AsUtc();
                            if (download.EndTimeUtc != default(DateTime))
                        {
                            download.EndTimeUtc = download.EndTimeUtc.AsUtc();
                        }
                    }
                }

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
    [RequireGuestSession]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var download = await _context.Downloads
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id);

            if (download == null)
            {
                return NotFound(ApiResponse.NotFound("Download"));
            }

            // Fix timezone
            download.StartTimeUtc = download.StartTimeUtc.AsUtc();
            if (download.EndTimeUtc != default)
            {
                download.EndTimeUtc = download.EndTimeUtc.AsUtc();
            }

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting download {Id}", id);
            return StatusCode(500, ApiResponse.InternalError("getting download"));
        }
    }

    /// <summary>
    /// Get events for multiple download IDs in a single batch request
    /// </summary>
    [HttpPost("batch-download-events")]
    [RequireGuestSession]
    public async Task<IActionResult> GetBatchDownloadEvents([FromBody] BatchDownloadEventsRequest request)
    {
        if (request.DownloadIds == null || request.DownloadIds.Count == 0)
        {
            return Ok(new Dictionary<int, object>());
        }

        // Limit to prevent abuse
        const int maxIds = 500;
        var downloadIds = request.DownloadIds.Take(maxIds).ToList();

        try
        {
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch download events for {Count} downloads", downloadIds.Count);
            return Ok(new Dictionary<int, object>());
        }
    }

    /// <summary>
    /// Get downloads with their tags and events for a time range
    /// </summary>
    [HttpGet("with-associations")]
    [RequireGuestSession]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetWithAssociations(
        [FromQuery] int count = 100,
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        try
        {
            var startDate = startTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(startTime.Value).UtcDateTime
                : DateTime.MinValue;
            var endDate = endTime.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(endTime.Value).UtcDateTime
                : DateTime.UtcNow;

            // Get downloads
            var downloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                .OrderByDescending(d => d.StartTimeUtc)
                .Take(count)
                .ToListAsync();

            if (downloads.Count == 0)
            {
                return Ok(new List<object>());
            }

            var downloadIds = downloads.Select(d => d.Id).ToList();

            // Get all events for these downloads
            var eventDownloads = await _context.EventDownloads
                .AsNoTracking()
                .Include(ed => ed.Event)
                .Where(ed => downloadIds.Contains(ed.DownloadId))
                .ToListAsync();

            // Group by download ID
            var eventsLookup = eventDownloads
                .GroupBy(ed => ed.DownloadId)
                .ToDictionary(g => g.Key, g => g.Select(ed => new
                {
                    ed.Event.Id,
                    ed.Event.Name,
                    ed.Event.ColorIndex,
                    ed.AutoTagged
                }).ToList());

            // Build response
            var result = downloads.Select(d =>
            {
                d.StartTimeUtc = d.StartTimeUtc.AsUtc();
                if (d.EndTimeUtc != default)
                {
                    d.EndTimeUtc = d.EndTimeUtc.AsUtc();
                }

                eventsLookup.TryGetValue(d.Id, out var downloadEvents);

                return new
                {
                    download = d,
                    events = downloadEvents ?? (object)Array.Empty<object>()
                };
            }).ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting downloads with associations");
            return Ok(new List<object>());
        }
    }
}

/// <summary>
/// Request model for batch download events endpoint
/// </summary>
public class BatchDownloadEventsRequest
{
    public List<int> DownloadIds { get; set; } = new();
}
