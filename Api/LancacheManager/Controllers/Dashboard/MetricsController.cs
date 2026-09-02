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
[Authorize(Policy = "AccountHolder")]
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
    [ProducesResponseType(typeof(MetricsIntervalResponse), StatusCodes.Status200OK)]
    public ActionResult<MetricsIntervalResponse> GetInterval()
    {
        return Ok(new MetricsIntervalResponse { Interval = _metricsService.GetUpdateInterval() });
    }

    /// <summary>
    /// Set the metrics update interval (5-60 seconds)
    /// </summary>
    [HttpPost("interval")]
    [ProducesResponseType(typeof(MetricsIntervalResponse), StatusCodes.Status200OK)]
    public ActionResult<MetricsIntervalResponse> SetInterval([FromBody] SetIntervalRequest request)
    {
        if (request.Interval < 5 || request.Interval > 60)
        {
            return BadRequest(ApiResponse.Invalid("Interval must be between 5 and 60 seconds"));
        }

        _metricsService.SetUpdateInterval(request.Interval);
        return Ok(new MetricsIntervalResponse { Interval = _metricsService.GetUpdateInterval() });
    }

    /// <summary>
    /// Gets how many games the per-game metrics report.
    /// </summary>
    /// <remarks>
    /// Answered from the persisted value, which is also what the collection loop reads, so a
    /// restart reports the saved limit immediately instead of the default until the first cycle
    /// completes.
    /// </remarks>
    [HttpGet("game-limit")]
    [ProducesResponseType(typeof(MetricsGameLimitResponse), StatusCodes.Status200OK)]
    public ActionResult<MetricsGameLimitResponse> GetGameLimit()
    {
        return Ok(new MetricsGameLimitResponse { GameLimit = ReadGameLimit() });
    }

    /// <summary>
    /// Sets how many games the per-game metrics report (1-500).
    /// </summary>
    /// <remarks>
    /// Persisted, so it survives a restart. The new value applies from the next collection
    /// cycle, up to one update interval later.
    /// </remarks>
    [HttpPost("game-limit")]
    [ProducesResponseType(typeof(MetricsGameLimitResponse), StatusCodes.Status200OK)]
    public ActionResult<MetricsGameLimitResponse> SetGameLimit([FromBody] SetGameLimitRequest request)
    {
        if (request.GameLimit < MinGameLimit || request.GameLimit > MaxGameLimit)
        {
            return BadRequest(ApiResponse.Invalid($"Game limit must be between {MinGameLimit} and {MaxGameLimit}"));
        }

        var previousLimit = ReadGameLimit();

        _stateRepository.SetTopGameCount(request.GameLimit);

        if (previousLimit != request.GameLimit)
        {
            _logger.LogInformation("Metrics game limit changed from {Old} to {New}", previousLimit, request.GameLimit);
        }

        return Ok(new MetricsGameLimitResponse { GameLimit = ReadGameLimit() });
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
    /// Gets metrics authentication security settings.
    /// </summary>
    /// <remarks>
    /// Returns current state, source (UI toggle or config), and env var default.
    /// </remarks>
    [HttpGet("security")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(MetricsSecurityResponse), StatusCodes.Status200OK)]
    public ActionResult<MetricsSecurityResponse> GetSecurity()
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
    /// Sets the metrics authentication requirement via a UI toggle.
    /// </summary>
    /// <remarks>
    /// Pass null to clear the UI override and fall back to the env-var / appsettings default.
    /// </remarks>
    [HttpPost("security")]
    [ProducesResponseType(typeof(MetricsSecurityResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MetricsSecurityResponse>> SetSecurityAsync([FromBody] SetSecurityRequest request)
    {
        _stateRepository.SetRequireAuthForMetrics(request.Enabled);

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
