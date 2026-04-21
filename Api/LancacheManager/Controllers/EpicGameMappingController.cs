using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
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
    private static readonly SemaphoreSlim _resolveStartLock = new(1, 1);

    private readonly EpicMappingService _epicMappingService;
    private readonly ILogger<EpicGameMappingController> _logger;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IOperationConflictChecker _conflictChecker;

    public EpicGameMappingController(
        EpicMappingService epicMappingService,
        ILogger<EpicGameMappingController> logger,
        IUnifiedOperationTracker operationTracker,
        IOperationConflictChecker conflictChecker)
    {
        _epicMappingService = epicMappingService;
        _logger = logger;
        _operationTracker = operationTracker;
        _conflictChecker = conflictChecker;
    }

    /// <summary>
    /// Gets all Epic game mappings, optionally paginated and sorted.
    /// </summary>
    [HttpGet]
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
    public async Task<ActionResult<EpicMappingStats>> GetStatsAsync(CancellationToken ct = default)
    {
        return Ok(await _epicMappingService.GetStatsAsync(ct));
    }

    /// <summary>
    /// Gets the current mapping auth status (authenticated, displayName, etc.)
    /// </summary>
    [HttpGet("auth-status")]
    public ActionResult<EpicMappingAuthStatus> GetAuthStatus()
    {
        return Ok(_epicMappingService.GetAuthStatus());
    }

    /// <summary>
    /// Starts the mapping login flow by returning the Epic authorization URL.
    /// No Docker container is created - the URL points directly to Epic's login page.
    /// </summary>
    [HttpPost("auth/login")]
    public ActionResult StartMappingLogin()
    {
        try
        {
            var authorizationUrl = _epicMappingService.GetAuthorizationUrl();
            return Ok(new { authorizationUrl });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Epic authorization URL");
            return StatusCode(500, ApiResponse.Error(ex.Message));
        }
    }

    /// <summary>
    /// Logs out mapping session and clears saved credentials.
    /// </summary>
    [HttpDelete("auth")]
    public async Task<ActionResult> LogoutMappingAsync()
    {
        await _epicMappingService.LogoutAsync();
        return Ok(ApiResponse.Message("Epic mapping logged out"));
    }

    /// <summary>
    /// Accepts the authorization code from the Epic login page,
    /// exchanges it for tokens, fetches games, and saves credentials.
    /// </summary>
    [HttpPost("auth/complete")]
    public async Task<ActionResult> CompleteMappingAuthAsync([FromBody] EpicAuthCompleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.AuthorizationCode))
        {
            return BadRequest(ApiResponse.Error("Authorization code is required"));
        }

        try
        {
            await _epicMappingService.OnAuthCodeReceivedAsync(request.AuthorizationCode.Trim());
            var status = _epicMappingService.GetAuthStatus();
            return Ok(new
            {
                message = "Game collection complete",
                displayName = status.DisplayName,
                gamesDiscovered = status.GamesDiscovered
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Epic auth code exchange failed");
            return Conflict(ApiResponse.Error(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete Epic mapping auth");
            return StatusCode(500, ApiResponse.Error("Failed to collect games: " + ex.Message));
        }
    }

    /// <summary>
    /// Starts a full Epic catalog refresh (scan + apply mappings).
    /// Mirrors Steam's POST /api/depots/rebuild — runs scan then resolves downloads.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<ActionResult> StartRefreshAsync(CancellationToken ct = default)
    {
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.EpicMapping,
            ConflictScope.Bulk(),
            ct);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        var started = _epicMappingService.TryStartRefresh(ct);
        if (!started)
        {
            var raceConflict = await _conflictChecker.CheckAsync(
                OperationType.EpicMapping,
                ConflictScope.Bulk(),
                ct);
            if (raceConflict != null)
            {
                return Conflict(raceConflict);
            }

            if (!_epicMappingService.GetAuthStatus().IsAuthenticated)
            {
                return BadRequest(ApiResponse.Error("Epic mapping login is required before refreshing the catalog"));
            }

            return StatusCode(500, ApiResponse.Error("Failed to start Epic catalog refresh"));
        }

        var operationId = _epicMappingService.GetScheduleStatus().OperationId
            ?? _operationTracker.GetActiveOperations(OperationType.EpicMapping).FirstOrDefault()?.Id;

        return Accepted(new
        {
            started = true,
            operationId,
            refreshInProgress = true,
            message = "Epic catalog refresh started"
        });
    }

    /// <summary>
    /// Resolves unmatched Epic downloads against stored CDN patterns only (no scan).
    /// </summary>
    [HttpPost("resolve")]
    public async Task<ActionResult> ResolveDownloadsAsync(CancellationToken ct = default)
    {
        var start = await BeginResolveOperationAsync(ct);
        if (start.Conflict != null)
        {
            return Conflict(start.Conflict);
        }

        var operationId = start.OperationId!.Value;
        var operationCts = start.CancellationTokenSource!;

        try
        {
            _operationTracker.UpdateProgress(operationId, 0, "Resolving Epic downloads...");
            var resolved = await _epicMappingService.ResolveEpicDownloadsAsync(operationCts.Token);
            _operationTracker.UpdateProgress(operationId, 100, $"Resolved {resolved} Epic download(s)");
            _operationTracker.CompleteOperation(operationId, true);
            return Ok(new
            {
                operationId,
                resolved,
                message = $"Resolved {resolved} Epic download(s) to game names"
            });
        }
        catch (OperationCanceledException)
        {
            _operationTracker.CompleteOperation(operationId, false, "Epic download resolution was cancelled");
            return StatusCode(499, ApiResponse.Error("Epic download resolution was cancelled"));
        }
        catch (Exception ex)
        {
            _operationTracker.CompleteOperation(operationId, false, ex.Message);
            _logger.LogError(ex, "Failed to resolve Epic downloads");
            return StatusCode(500, ApiResponse.Error("Failed to resolve Epic downloads: " + ex.Message));
        }
    }

    /// <summary>
    /// Gets the current schedule status (interval, next run, last run, processing state).
    /// </summary>
    [HttpGet("schedule")]
    public ActionResult<EpicScheduleStatus> GetScheduleStatus()
    {
        return Ok(_epicMappingService.GetScheduleStatus());
    }

    /// <summary>
    /// Cancels the current Epic catalog refresh if one is running.
    /// Mirrors Steam's DELETE /api/depots/rebuild pattern.
    /// </summary>
    [HttpDelete("schedule/refresh")]
    public async Task<ActionResult> CancelRefreshAsync()
    {
        var cancelled = await _epicMappingService.CancelRefreshAsync();
        if (cancelled)
        {
            return Ok(new { cancelled = true, message = "Epic catalog refresh cancelled" });
        }
        return NotFound(new { cancelled = false, message = "No active refresh to cancel" });
    }

    /// <summary>
    /// Updates the refresh interval in hours. Set to 0 to disable.
    /// </summary>
    [HttpPut("schedule/interval")]
    public ActionResult UpdateScheduleInterval([FromBody] double intervalHours)
    {
        if (intervalHours < 0)
        {
            return BadRequest(ApiResponse.Error("Interval must be non-negative"));
        }

        _epicMappingService.RefreshIntervalHours = intervalHours;
        _logger.LogInformation("Epic refresh interval updated to {Hours} hours", intervalHours);
        return Ok(new { intervalHours, message = $"Refresh interval set to {intervalHours} hours" });
    }

    /// <summary>
    /// Search games by name (case-insensitive partial match).
    /// </summary>
    [HttpGet("search")]
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

    private async Task<(Guid? OperationId, CancellationTokenSource? CancellationTokenSource, OperationConflictResponse? Conflict)> BeginResolveOperationAsync(CancellationToken cancellationToken)
    {
        await _resolveStartLock.WaitAsync(cancellationToken);
        try
        {
            var conflict = await _conflictChecker.CheckAsync(
                OperationType.EpicMapping,
                ConflictScope.Bulk(),
                cancellationToken);
            if (conflict != null)
            {
                return (null, null, conflict);
            }

            var cts = new CancellationTokenSource();
            var operationId = _operationTracker.RegisterOperation(OperationType.EpicMapping, "Epic Download Resolution", cts);
            return (operationId, cts, null);
        }
        finally
        {
            _resolveStartLock.Release();
        }
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
    public string? ImageUrl { get; set; }
}
