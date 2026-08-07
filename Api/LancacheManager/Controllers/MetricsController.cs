using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        IConfiguration configuration,
        LancacheMetricsService metricsService,
        IStateService stateRepository,
        ISignalRNotificationService notifications,
        ILogger<MetricsController> logger)
    {
        _configuration = configuration;
        _metricsService = metricsService;
        _stateRepository = stateRepository;
        _notifications = notifications;
        _logger = logger;
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
    /// Get how many games the per-game metrics report. Answered from the persisted value, which is
    /// also what the collection loop reads, so a restart reports the saved limit immediately
    /// instead of the default until the first cycle completes. [32]
    /// </summary>
    [HttpGet("game-limit")]
    public IActionResult GetGameLimit()
    {
        return Ok(new { gameLimit = ReadGameLimit() });
    }

    /// <summary>
    /// Set how many games the per-game metrics report (1-500). Persisted, so it survives a restart.
    /// The new value applies from the next collection cycle, up to one update interval later.
    /// </summary>
    [HttpPost("game-limit")]
    public IActionResult SetGameLimit([FromBody] SetGameLimitRequest request)
    {
        if (request.GameLimit < MinGameLimit || request.GameLimit > MaxGameLimit)
        {
            return BadRequest(ApiResponse.Invalid($"Game limit must be between {MinGameLimit} and {MaxGameLimit}"));
        }

        var previousLimit = ReadGameLimit();

        try
        {
            _stateRepository.SetTopGameCount(request.GameLimit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist metrics game limit");
            return StatusCode(503, ApiResponse.Error("state_persistence_disabled", "Failed to persist the metrics game limit. Your change was not saved."));
        }

        if (previousLimit != request.GameLimit)
        {
            _logger.LogInformation("Metrics game limit changed from {Old} to {New}", previousLimit, request.GameLimit);
        }

        return Ok(new { gameLimit = ReadGameLimit() });
    }

    /// <summary>
    /// The ceiling keeps the series count well under the OpenTelemetry SDK's 2000-point-per-metric
    /// limit, past which individual games would silently fold into a single overflow series. The
    /// stored value is clamped on the way out as well as on the way in, because state.json is a
    /// plain file an operator can edit by hand.
    /// </summary>
    private const int MinGameLimit = 1;
    private const int MaxGameLimit = 500;

    private int ReadGameLimit() => Math.Clamp(_stateRepository.GetTopGameCount(), MinGameLimit, MaxGameLimit);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist metrics security setting");
            return StatusCode(503, ApiResponse.Error("state_persistence_disabled", "Failed to persist the metrics security setting. Your change was not saved."));
        }

        // Broadcast updated state to all connected clients
        var configValue = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);
        var stateValue = _stateRepository.GetRequireAuthForMetrics();
        var effectiveValue = stateValue ?? configValue;
        var source = stateValue.HasValue ? "ui" : "config";

        await _notifications.NotifyAllAsync(SignalREvents.MetricsSecurityUpdated, new
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
