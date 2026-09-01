using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// The download rows themselves. Separate from the grouped and paged endpoints because those
/// return aggregates, and the Downloads page export writes one line per download, so a group
/// row has nothing it can write.
/// </summary>
[ApiController]
[Route("api/downloads")]
[Authorize]
public class DownloadRowsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IStateService _stateRepository;

    public DownloadRowsController(AppDbContext context, IStateService stateRepository)
    {
        _context = context;
        _stateRepository = stateRepository;
    }

    /// <summary>
    /// One page of the downloads the current range and event selection contain, newest first.
    /// </summary>
    /// <remarks>
    /// The two arms differ: the live view drops rows tagged to app 0, while a picked range or
    /// event drops evicted rows instead. Evicted rows still come back on the live view because the
    /// Downloads page carries its own hide-evicted setting and decides over these rows itself.
    ///
    /// Paged forward from the last row the caller already holds rather than by page number: a
    /// caller walking the whole table with an offset makes the database re-scan every earlier page
    /// to reach page N, and this table is the largest one here. Pass the id of the last row of the
    /// previous page as <paramref name="afterId"/> to get the next page; a page shorter than the
    /// page size is the last one.
    /// </remarks>
    [HttpGet("all")]
    [ProducesResponseType(typeof(List<Download>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<Download>>> GetAllAsync(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] long? eventId = null,
        [FromQuery] long? afterId = null)
    {
        const int pageSize = 500;

        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();

        var query = _context.Downloads
            .AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEmptySessionFilter()
            .ApplyTimeRange(startTime, endTime);

        if (eventId.HasValue)
        {
            var taggedEventId = eventId.Value;
            query = query.Where(d => _context.EventDownloads
                .Where(ed => ed.EventId == taggedEventId)
                .Select(ed => ed.DownloadId)
                .Contains(d.Id));
        }

        var isLive = !startTime.HasValue && !endTime.HasValue && !eventId.HasValue;
        query = isLive
            ? query.Where(d => !d.GameAppId.HasValue || d.GameAppId.Value != 0)
            : query.ApplyEvictedFilter(evictedMode);

        if (afterId.HasValue)
        {
            var cursorStartTime = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.Id == afterId.Value)
                .Select(d => (DateTime?)d.StartTimeUtc)
                .FirstOrDefaultAsync(HttpContext.RequestAborted);

            if (!cursorStartTime.HasValue)
            {
                // The row the caller is continuing from is gone, which DownloadCleanupService does
                // on its own schedule while a walk is in flight. There is no position left to
                // continue from, and starting over at the newest row would hand back rows the
                // caller already has. An empty page reads as the end of the walk, so the caller
                // would save a file missing every older row without being told; fail instead.
                return NotFound();
            }

            var cursorStart = cursorStartTime.Value;
            var cursorId = afterId.Value;
            query = query.Where(d => d.StartTimeUtc < cursorStart
                                     || (d.StartTimeUtc == cursorStart && d.Id < cursorId));
        }

        // Id breaks start-time ties so the order is total: without it two rows sharing a start time
        // can land on both sides of a page boundary or on neither.
        //
        // A caller that navigates away or aborts its request would otherwise leave the read and the
        // name resolution below running.
        var downloads = await query
            .OrderByDescending(d => d.StartTimeUtc)
            .ThenByDescending(d => d.Id)
            .Take(pageSize)
            .ToListAsync(HttpContext.RequestAborted);

        var showClean = evictedMode == EvictedDataMode.ShowClean.ToWireString();
        foreach (var download in downloads)
        {
            // Show-clean presents the rows without their eviction marks rather than hiding them,
            // so the flag is cleared after the read instead of filtered in the query above.
            if (showClean)
            {
                download.IsEvicted = false;
            }

            // Not a stored column, so it is absent unless filled here. The export writes it as its
            // own column, which would otherwise come out blank for every row.
            if (download.EndTimeUtc != default && download.EndTimeUtc > download.StartTimeUtc)
            {
                download.DurationSeconds = (download.EndTimeUtc - download.StartTimeUtc).TotalSeconds;
            }
        }

        await GameNameResolver.ResolveAsync(_context, downloads, HttpContext.RequestAborted);

        // Timestamps come back from the provider with an unspecified kind, which serializes without
        // the zone and would be read as local time in the browser.
        return Ok(downloads.WithUtcMarking());
    }
}
