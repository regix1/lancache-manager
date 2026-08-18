using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Batch endpoint for the dashboard - returns all dashboard data sets in a single HTTP round trip.
/// All compute lives in <see cref="IDashboardBatchService"/> so that a startup warmer can
/// pre-populate the underlying IMemoryCache before the first user request arrives.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardBatchService _dashboardBatchService;
    private readonly IEventsService _eventsService;

    public DashboardController(IDashboardBatchService dashboardBatchService, IEventsService eventsService)
    {
        _dashboardBatchService = dashboardBatchService;
        _eventsService = eventsService;
    }

    /// <summary>
    /// Returns all dashboard data sets in a single response.
    /// </summary>
    /// <remarks>
    /// Returns cache, clients, services, dashboard stats, downloads, detection, sparklines,
    /// hourly activity, and cache snapshot in one call. Sub-queries execute in parallel inside the
    /// service, and a sub-query that fails leaves its own field null instead of failing the whole
    /// request (see <see cref="DashboardBatchResponse"/>).
    /// </remarks>
    /// <param name="eventId">
    /// When set, scopes every sub-query to the downloads tagged to this event instead of the
    /// requested time range. An unknown id throws NotFound before any sub-query runs.
    /// </param>
    /// <param name="timeZoneId">
    /// The IANA zone the hourly activity buckets are grouped on. Omitted, or naming a zone this
    /// server does not know, they stay on the server's clock.
    /// </param>
    [HttpGet("batch")]
    [ProducesResponseType(typeof(DashboardBatchResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardBatchResponse>> GetBatchAsync(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] long? eventId = null,
        [FromQuery] string? timeZoneId = null,
        CancellationToken ct = default)
    {
        // A cascade delete removes the event's EventDownloads rows, so an unknown id would
        // otherwise flow through as an empty (but 200 OK) result instead of a clear signal
        // that the id is gone.
        if (eventId.HasValue)
        {
            await _eventsService.GetByIdOrThrowAsync(eventId.Value, "Event", ct);
        }

        Response.Headers["Cache-Control"] = "no-store, private";
        var response = await _dashboardBatchService.GetBatchAsync(startTime, endTime, eventId, timeZoneId, ct);
        return Ok(response);
    }

    /// <summary>
    /// Overlays selected events on a shared elapsed-time axis so two LAN parties can be compared
    /// hour-for-hour even when they ran in different years.
    /// </summary>
    [HttpGet("event-compare")]
    [ProducesResponseType(typeof(EventCompareResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EventCompareResponse>> GetEventCompareAsync(
        [FromQuery] List<long>? eventIds,
        CancellationToken ct)
    {
        Response.Headers["Cache-Control"] = "no-store, private";
        var response = await _dashboardBatchService.GetEventCompareAsync(
            eventIds ?? [],
            ct);
        return Ok(response);
    }
}
