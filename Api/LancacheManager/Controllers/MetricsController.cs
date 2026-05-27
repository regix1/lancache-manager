using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for metrics endpoint configuration
/// Handles metrics security status and configuration
/// </summary>
[ApiController]
[Route("api/metrics")]
[Authorize(Policy = "AdminOnly")]
public class MetricsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly LancacheMetricsService _metricsService;
    private readonly IStateService _stateRepository;
    private readonly IHubContext<DownloadHub> _hubContext;

    public MetricsController(
        IConfiguration configuration,
        LancacheMetricsService metricsService,
        IStateService stateRepository,
        IHubContext<DownloadHub> hubContext)
    {
        _configuration = configuration;
        _metricsService = metricsService;
        _stateRepository = stateRepository;
        _hubContext = hubContext;
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
            return BadRequest(ApiResponse.Invalid("Interval must be between 5 and 60 seconds"));
        }

        _metricsService.SetUpdateInterval(request.Interval);
        return Ok(new { interval = _metricsService.GetUpdateInterval() });
    }

    /// <summary>
    /// Get metrics authentication security settings
    /// Returns current state, source (ui toggle or config), and env var default
    /// </summary>
    [HttpGet("security")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetSecurity()
    {
        // Get env var / appsettings.json value (the default)
        var configValue = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);

        // Get UI toggle value (null if not set via UI)
        var stateValue = _stateRepository.GetRequireAuthForMetrics();

        // Effective value: UI override takes precedence, otherwise use config
        var effectiveValue = stateValue ?? configValue;
        var source = stateValue.HasValue ? "ui" : "config";

        return Ok(new MetricsSecurityResponse
        {
            RequiresAuthentication = effectiveValue,
            Source = source,
            CanToggle = true,
            EnvVarValue = configValue
        });
    }

    /// <summary>
    /// Set metrics authentication requirement via UI toggle.
    /// Pass null to clear the UI override and fall back to the env-var / appsettings default.
    /// </summary>
    [HttpPost("security")]
    public async Task<IActionResult> SetSecurityAsync([FromBody] SetSecurityRequest request)
    {
        try
        {
            _stateRepository.SetRequireAuthForMetrics(request.Enabled);
        }
        catch (Exception)
        {
            return StatusCode(503, new { error = "state_persistence_disabled", message = "Failed to persist the metrics security setting. Your change was not saved." });
        }

        // Broadcast updated state to all connected clients
        var configValue = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);
        var stateValue = _stateRepository.GetRequireAuthForMetrics();
        var effectiveValue = stateValue ?? configValue;
        var source = stateValue.HasValue ? "ui" : "config";

        await _hubContext.Clients.All.SendAsync(SignalREvents.MetricsSecurityUpdated, new
        {
            requiresAuthentication = effectiveValue,
            source,
            canToggle = true,
            envVarValue = configValue
        });

        // Return updated state
        return GetSecurity();
    }
}
