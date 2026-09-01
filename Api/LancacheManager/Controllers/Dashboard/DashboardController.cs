using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Middleware;
using LancacheManager.Models;
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
    private readonly IClientHostnameService _hostnameService;

    public DashboardController(
        IDashboardBatchService dashboardBatchService,
        IEventsService eventsService,
        IClientHostnameService hostnameService)
    {
        _dashboardBatchService = dashboardBatchService;
        _eventsService = eventsService;
        _hostnameService = hostnameService;
    }

    /// <summary>
    /// Returns all dashboard data sets in a single response.
    /// </summary>
    /// <remarks>
    /// Returns cache, clients, services, dashboard stats, download totals both plain and filtered,
    /// the download filter options, the recent download slice, detection, sparklines, hourly
    /// activity, and cache snapshot in one call. Sub-queries execute in parallel inside the
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
    /// <param name="service">
    /// Narrows the filtered download totals and the recent slice to one service, named by the
    /// folded key the dropdown shows. Omitted or "all" leaves them over every service, and the
    /// plain download totals ignore it either way.
    /// </param>
    /// <param name="client">
    /// Narrows the same two sections to the given client addresses, comma-separated because a
    /// dropdown entry can name a client group covering several. Omitted or "all" leaves them over
    /// every client.
    /// </param>
    [HttpGet("batch")]
    [ProducesResponseType(typeof(DashboardBatchResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardBatchResponse>> GetBatchAsync(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] long? eventId = null,
        [FromQuery] string? timeZoneId = null,
        [FromQuery] string? service = null,
        [FromQuery] string? client = null,
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

        // A guest is given a view of the cache, not an inventory of the machines on the network,
        // so client names reach one only when an admin has said they may. Decided here, where the
        // session is, and passed down rather than filtered out of the answer: a name and a nickname
        // arrive in the same field, so the body a guest gets has to be one that never carried names.
        var includeClientHostnames =
            HttpContext.GetUserSession()?.SessionType.IsAccountHolder() == true
            || _hostnameService.IsVisibleToGuests();

        var response = await _dashboardBatchService.GetBatchAsync(
            startTime, endTime, eventId, timeZoneId, includeClientHostnames, ct, service, client);
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
