using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Models;
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
    private readonly ITagsRepository _tagsRepository;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        AppDbContext context,
        StatsRepository statsService,
        ITagsRepository tagsRepository,
        ILogger<DownloadsController> logger)
    {
        _context = context;
        _statsService = statsService;
        _tagsRepository = tagsRepository;
        _logger = logger;
    }

    [HttpGet("latest")]
    [ResponseCache(Duration = 5)] // Cache for 5 seconds
    public async Task<IActionResult> GetLatest([FromQuery] int count = int.MaxValue, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null)
    {
        const int maxRetries = 3;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                List<Download> downloads;

                // If no time filtering, use cached service method
                if (!startTime.HasValue && !endTime.HasValue)
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

                    downloads = await _context.Downloads
                        .AsNoTracking()
                        .Where(d => d.StartTimeUtc >= startDate && d.StartTimeUtc <= endDate)
                        .OrderByDescending(d => d.StartTimeUtc)
                        .Take(count)
                        .ToListAsync();

                    // Fix timezone: Ensure UTC DateTime values are marked as UTC for proper JSON serialization
                    foreach (var download in downloads)
                    {
                        download.StartTimeUtc = DateTime.SpecifyKind(download.StartTimeUtc, DateTimeKind.Utc);
                        if (download.EndTimeUtc != default(DateTime))
                        {
                            download.EndTimeUtc = DateTime.SpecifyKind(download.EndTimeUtc, DateTimeKind.Utc);
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

    [HttpGet("active")]
    [ResponseCache(Duration = 2)] // Cache for 2 seconds
    public async Task<IActionResult> GetActive()
    {
        try
        {
            // Use cached service method (2-second cache)
            var downloads = await _statsService.GetActiveDownloadsAsync();
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active downloads");
            return Ok(new List<Download>());
        }
    }

    /// <summary>
    /// Get a download by ID with its tags and events
    /// </summary>
    [HttpGet("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var download = await _context.Downloads
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id);

            if (download == null)
            {
                return NotFound(new { error = "Download not found" });
            }

            // Fix timezone
            download.StartTimeUtc = DateTime.SpecifyKind(download.StartTimeUtc, DateTimeKind.Utc);
            if (download.EndTimeUtc != default)
            {
                download.EndTimeUtc = DateTime.SpecifyKind(download.EndTimeUtc, DateTimeKind.Utc);
            }

            // Get tags for this download
            var tags = await _tagsRepository.GetTagsForDownloadAsync(id);

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
                tags,
                events
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting download {Id}", id);
            return StatusCode(500, new { error = "Failed to get download" });
        }
    }

    /// <summary>
    /// Get downloads with their tags and events for a time range
    /// </summary>
    [HttpGet("with-associations")]
    [RequireAuth]
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

            // Get all tags for these downloads
            var downloadTags = await _context.DownloadTags
                .AsNoTracking()
                .Include(dt => dt.Tag)
                .Where(dt => downloadIds.Contains(dt.DownloadId))
                .ToListAsync();

            // Get all events for these downloads
            var eventDownloads = await _context.EventDownloads
                .AsNoTracking()
                .Include(ed => ed.Event)
                .Where(ed => downloadIds.Contains(ed.DownloadId))
                .ToListAsync();

            // Group by download ID
            var tagsLookup = downloadTags
                .GroupBy(dt => dt.DownloadId)
                .ToDictionary(g => g.Key, g => g.Select(dt => new
                {
                    dt.Tag.Id,
                    dt.Tag.Name,
                    dt.Tag.ColorIndex,
                    dt.Tag.Description
                }).ToList());

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
                d.StartTimeUtc = DateTime.SpecifyKind(d.StartTimeUtc, DateTimeKind.Utc);
                if (d.EndTimeUtc != default)
                {
                    d.EndTimeUtc = DateTime.SpecifyKind(d.EndTimeUtc, DateTimeKind.Utc);
                }

                tagsLookup.TryGetValue(d.Id, out var downloadTags);
                eventsLookup.TryGetValue(d.Id, out var downloadEvents);

                return new
                {
                    download = d,
                    tags = downloadTags ?? (object)Array.Empty<object>(),
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