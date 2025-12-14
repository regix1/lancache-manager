using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for metrics endpoint configuration
/// Handles metrics security status and configuration
/// </summary>
[ApiController]
[Route("api/metrics")]
public class MetricsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly LancacheMetricsService _metricsService;

    public MetricsController(IConfiguration configuration, LancacheMetricsService metricsService)
    {
        _configuration = configuration;
        _metricsService = metricsService;
    }

    /// <summary>
    /// Get metrics endpoint security status
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var requiresAuth = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);

        return Ok(new MetricsStatusResponse
        {
            RequiresAuthentication = requiresAuth,
            Endpoint = "/metrics",
            AuthMethod = requiresAuth ? "X-Api-Key header required" : "Public access"
        });
    }

    /// <summary>
    /// Get the current metrics update interval
    /// </summary>
    [HttpGet("interval")]
    public IActionResult GetInterval()
    {
        return Ok(new { interval = _metricsService.GetUpdateInterval() });
    }

    /// <summary>
    /// Set the metrics update interval (5-60 seconds)
    /// </summary>
    [HttpPost("interval")]
    public IActionResult SetInterval([FromBody] SetIntervalRequest request)
    {
        if (request.Interval < 5 || request.Interval > 60)
        {
            return BadRequest(new { error = "Interval must be between 5 and 60 seconds" });
        }

        _metricsService.SetUpdateInterval(request.Interval);
        return Ok(new { interval = _metricsService.GetUpdateInterval() });
    }
}

public class SetIntervalRequest
{
    public int Interval { get; set; }
}
