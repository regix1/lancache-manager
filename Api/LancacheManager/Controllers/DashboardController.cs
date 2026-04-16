using LancacheManager.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Batch endpoint for the dashboard — returns all dashboard data sets in a single HTTP round trip.
/// All compute lives in <see cref="IDashboardBatchService"/> so that a startup warmer can
/// pre-populate the underlying IMemoryCache before the first user request arrives.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardBatchService _dashboardBatchService;

    public DashboardController(IDashboardBatchService dashboardBatchService)
    {
        _dashboardBatchService = dashboardBatchService;
    }

    /// <summary>
    /// GET /api/dashboard/batch — returns cache, clients, services, dashboard stats, downloads,
    /// detection, sparklines, hourly activity, cache snapshot, and cache growth in a single
    /// response. Sub-queries execute in parallel inside the service.
    /// </summary>
    [HttpGet("batch")]
    public async Task<IActionResult> GetBatchAsync(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] long? eventId = null,
        CancellationToken ct = default)
    {
        Response.Headers["Cache-Control"] = "no-store, private";
        var response = await _dashboardBatchService.GetBatchAsync(startTime, endTime, eventId, ct);
        return Ok(response);
    }
}
