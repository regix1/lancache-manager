using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Security;
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
    private readonly IStateRepository _stateRepository;

    public MetricsController(
        IConfiguration configuration,
        LancacheMetricsService metricsService,
        IStateRepository stateRepository)
    {
        _configuration = configuration;
        _metricsService = metricsService;
        _stateRepository = stateRepository;
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
    [RequireAuth]
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
    /// Set metrics authentication requirement via UI toggle
    /// </summary>
    [HttpPost("security")]
    [RequireAuth]
    public IActionResult SetSecurity([FromBody] SetSecurityRequest request)
    {
        _stateRepository.SetRequireAuthForMetrics(request.Enabled);

        // Return updated state
        return GetSecurity();
    }
}

public class SetIntervalRequest
{
    public int Interval { get; set; }
}

public class SetSecurityRequest
{
    public bool Enabled { get; set; }
}

public class MetricsSecurityResponse
{
    public bool RequiresAuthentication { get; set; }
    public string Source { get; set; } = "config";
    public bool CanToggle { get; set; } = true;
    public bool EnvVarValue { get; set; }
}
