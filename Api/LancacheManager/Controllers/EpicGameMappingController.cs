using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Epic Games game mappings.
/// Manages the cumulative catalog of Epic games discovered through user logins.
/// </summary>
[ApiController]
[Route("api/epic/game-mappings")]
[Authorize(Policy = "AdminOnly")]
public class EpicGameMappingController : ControllerBase
{
    private readonly EpicMappingService _epicMappingService;
    private readonly ILogger<EpicGameMappingController> _logger;

    public EpicGameMappingController(
        EpicMappingService epicMappingService,
        ILogger<EpicGameMappingController> logger)
    {
        _epicMappingService = epicMappingService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all Epic game mappings, optionally paginated and sorted.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<EpicGameMappingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<EpicGameMappingDto>>> GetAllMappingsAsync(
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        var mappings = await _epicMappingService.GetAllMappingsAsync(ct);

        var dtos = mappings.Select(m => new EpicGameMappingDto
        {
            AppId = m.AppId,
            Name = m.Name,
            DiscoveredAtUtc = m.DiscoveredAtUtc,
            LastSeenAtUtc = m.LastSeenAtUtc,
            Source = m.Source,
            ImageUrl = m.ImageUrl
        }).ToList();

        if (skip.HasValue)
            dtos = dtos.Skip(skip.Value).ToList();
        if (take.HasValue)
            dtos = dtos.Take(take.Value).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Gets mapping statistics (total games, last updated, etc.)
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(EpicMappingStats), StatusCodes.Status200OK)]
    public async Task<ActionResult<EpicMappingStats>> GetStatsAsync(CancellationToken ct = default)
    {
        return Ok(await _epicMappingService.GetStatsAsync(ct));
    }

    /// <summary>
    /// Gets the current mapping auth status (authenticated, displayName, etc.)
    /// </summary>
    [HttpGet("auth-status")]
    [ProducesResponseType(typeof(EpicMappingAuthStatus), StatusCodes.Status200OK)]
    public ActionResult<EpicMappingAuthStatus> GetAuthStatus()
    {
        return Ok(_epicMappingService.GetAuthStatus());
    }

    /// <summary>
    /// Starts the mapping login flow by returning the Epic authorization URL.
    /// </summary>
    /// <remarks>
    /// No Docker container is created; the URL points directly to Epic's login page.
    /// </remarks>
    [HttpPost("auth/login")]
    [ProducesResponseType(typeof(EpicLoginUrlResponse), StatusCodes.Status200OK)]
    public ActionResult<EpicLoginUrlResponse> StartLogin()
    {
        try
        {
            var authorizationUrl = _epicMappingService.GetAuthorizationUrl();
            return Ok(new EpicLoginUrlResponse { AuthorizationUrl = authorizationUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Epic authorization URL");
            throw; // -> GlobalExceptionMiddleware -> 500 safe { error, details?, statusCode, traceId }
        }
    }

    /// <summary>
    /// Logs out mapping session and clears saved credentials.
    /// </summary>
    [HttpDelete("auth")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> LogoutAsync()
    {
        await _epicMappingService.LogoutAsync();
        return Ok(ApiResponse.Message("Epic mapping logged out"));
    }

    /// <summary>
    /// Completes the Epic login flow using an authorization code.
    /// </summary>
    /// <remarks>
    /// Exchanges the authorization code for tokens, fetches games, and saves credentials.
    /// </remarks>
    [HttpPost("auth/complete")]
    [ProducesResponseType(typeof(EpicAuthCompleteResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EpicAuthCompleteResponse>> CompleteAuthAsync([FromBody] EpicAuthCompleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.AuthorizationCode))
        {
            return BadRequest(ApiResponse.Error("Authorization code is required"));
        }

        try
        {
            await _epicMappingService.OnAuthCodeReceivedAsync(request.AuthorizationCode.Trim());
            var status = _epicMappingService.GetAuthStatus();
            return Ok(new EpicAuthCompleteResponse
            {
                Message = "Game collection complete",
                DisplayName = status.DisplayName,
                GamesDiscovered = status.GamesDiscovered
            });
        }
        catch (Exception ex) when (ex is not ConflictException)
        {
            _logger.LogError(ex, "Failed to complete Epic mapping auth");
            throw; // -> GlobalExceptionMiddleware -> 500 safe { error, details?, statusCode, traceId }
        }
    }

    /// <summary>
    /// Gets the current schedule status (interval, next run, last run, processing state).
    /// </summary>
    [HttpGet("schedule")]
    [ProducesResponseType(typeof(EpicScheduleStatus), StatusCodes.Status200OK)]
    public ActionResult<EpicScheduleStatus> GetScheduleStatus()
    {
        return Ok(_epicMappingService.GetScheduleStatus());
    }

    /// <summary>
    /// Cancels the current Epic catalog refresh if one is running.
    /// </summary>
    /// <remarks>
    /// Mirrors the pattern used by DELETE /api/depots/rebuild for Steam.
    /// </remarks>
    [HttpDelete("schedule/refresh")]
    [ProducesResponseType(typeof(EpicRefreshCancelResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EpicRefreshCancelResponse>> CancelRefreshAsync()
    {
        var cancelled = await _epicMappingService.CancelRefreshAsync();
        if (cancelled)
        {
            return Ok(new EpicRefreshCancelResponse { Cancelled = true, Message = "Epic catalog refresh cancelled" });
        }
        return NotFound(new NotFoundResponse { Error = "No active refresh to cancel" });
    }

    /// <summary>
    /// Updates the Epic catalog refresh interval, in hours.
    /// </summary>
    /// <remarks>
    /// Set to 0 to disable the refresh schedule.
    /// </remarks>
    [HttpPut("schedule/interval")]
    [ProducesResponseType(typeof(EpicScheduleIntervalResponse), StatusCodes.Status200OK)]
    public ActionResult<EpicScheduleIntervalResponse> UpdateScheduleInterval([FromBody] double intervalHours)
    {
        if (intervalHours < 0)
        {
            return BadRequest(ApiResponse.Error("Interval must be non-negative"));
        }

        _epicMappingService.RefreshIntervalHours = intervalHours;
        _logger.LogInformation("Epic refresh interval updated to {Hours} hours", intervalHours);
        return Ok(new EpicScheduleIntervalResponse { IntervalHours = intervalHours, Message = $"Refresh interval set to {intervalHours} hours" });
    }

    /// <summary>
    /// Search games by name (case-insensitive partial match).
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<EpicGameMappingDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<EpicGameMappingDto>>> SearchGamesAsync(
        [FromQuery] string q,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest(ApiResponse.Error("Search query must be at least 2 characters"));

        var results = await _epicMappingService.SearchAsync(q, ct);

        var dtos = results.Select(m => new EpicGameMappingDto
        {
            AppId = m.AppId,
            Name = m.Name,
            DiscoveredAtUtc = m.DiscoveredAtUtc,
            LastSeenAtUtc = m.LastSeenAtUtc,
            Source = m.Source,
            ImageUrl = m.ImageUrl
        }).ToList();

        return Ok(dtos);
    }
}

/// <summary>
/// Request body for completing Epic auth with authorization code.
/// </summary>
public class EpicAuthCompleteRequest
{
    public string AuthorizationCode { get; set; } = string.Empty;
}

/// <summary>
/// DTO for Epic game mapping responses.
/// </summary>
public class EpicGameMappingDto
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime DiscoveredAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Header image URL for this game. Null until the image fetch service has resolved one.
    /// </summary>
    public string? ImageUrl { get; set; }
}
