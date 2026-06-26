using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Services.Xbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Xbox / Microsoft Store game mappings.
/// Manages the cumulative, SHARED catalog of Xbox games discovered through user logins (an admin
/// sees the union of all users' titles, by design - mirrors Epic's shared catalog model).
///
/// AdminOnly read of the global mapping/catalog. Unlike Epic, the login flow lives on the Xbox
/// PREFILL daemon (XboxDaemonController), so this controller only exposes the catalog + a manual
/// resolve trigger; the primary resolution path is the Rust ingest + the RustLogProcessor post-pass.
/// </summary>
[ApiController]
[Route("api/xbox/game-mappings")]
[Authorize(Policy = "AdminOnly")]
public class XboxGameMappingController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly XboxMappingService _xboxMappingService;
    private readonly XboxCatalogMappingService _xboxCatalogMappingService;
    private readonly ILogger<XboxGameMappingController> _logger;

    public XboxGameMappingController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        XboxMappingService xboxMappingService,
        XboxCatalogMappingService xboxCatalogMappingService,
        ILogger<XboxGameMappingController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _xboxMappingService = xboxMappingService;
        _xboxCatalogMappingService = xboxCatalogMappingService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all Xbox game mappings, optionally paginated.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<XboxGameMappingDto>>> GetAllMappingsAsync(
        [FromQuery] int? skip = null,
        [FromQuery] int? take = null,
        CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var query = db.XboxGameMappings
            .AsNoTracking()
            .OrderBy(m => m.Title)
            .AsQueryable();

        if (skip.HasValue)
            query = query.Skip(skip.Value);
        if (take.HasValue)
            query = query.Take(take.Value);

        var dtos = await query
            .Select(m => new XboxGameMappingDto
            {
                ProductId = m.ProductId,
                Title = m.Title,
                DiscoveredAtUtc = m.DiscoveredAtUtc,
                LastSeenAtUtc = m.LastSeenAtUtc,
                ImageUrl = m.ImageUrl
            })
            .ToListAsync(ct);

        return Ok(dtos);
    }

    /// <summary>
    /// Gets mapping statistics (total games discovered, last updated).
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<XboxMappingStatsDto>> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var totalGames = await db.XboxGameMappings.CountAsync(ct);
        var lastUpdated = totalGames > 0
            ? await db.XboxGameMappings.MaxAsync(m => m.LastSeenAtUtc, ct)
            : (DateTime?)null;

        return Ok(new XboxMappingStatsDto
        {
            TotalGames = totalGames,
            LastUpdatedUtc = lastUpdated
        });
    }

    /// <summary>
    /// Gets the current manager-side mapping auth status (authenticated, gamertag, last collection, count).
    /// Mirrors Epic's <c>GET auth-status</c>. Synchronous, no I/O.
    /// </summary>
    [HttpGet("auth-status")]
    public ActionResult<XboxMappingAuthStatus> GetAuthStatus()
    {
        return Ok(_xboxCatalogMappingService.GetAuthStatus());
    }

    /// <summary>
    /// Starts the daemon-free Xbox MSA device-code login: returns the <c>userCode</c>/<c>verificationUri</c>
    /// for the user to approve in their browser and kicks a background poll loop. No Docker container and no
    /// prefill daemon are created. Completion is surfaced via the <c>XboxMappingProgress</c> SignalR event
    /// (there is no code-paste complete step - the backend polls the token endpoint).
    /// </summary>
    [HttpPost("auth/login")]
    public async Task<ActionResult> StartLoginAsync(CancellationToken ct = default)
    {
        try
        {
            // A stale in-flight login is superseded inside StartLoginAsync (last-writer-wins), so
            // re-clicking Login after abandoning a prior attempt always starts fresh - it never 409s.
            var challenge = await _xboxCatalogMappingService.StartLoginAsync(ct);
            return Ok(new
            {
                userCode = challenge.UserCode,
                verificationUri = challenge.VerificationUri,
                expiresIn = challenge.ExpiresIn,
                interval = challenge.Interval,
                operationId = challenge.OperationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Xbox mapping login");
            return StatusCode(500, ApiResponse.Error("Failed to start Xbox login: " + ex.Message));
        }
    }

    /// <summary>
    /// Cancels a pending (not-yet-approved) device-code login poll, e.g. when the user closes the login
    /// modal. Does NOT clear credentials or sign out an already-authenticated account - unlike
    /// <see cref="LogoutAsync"/>, this only stops a poll that has not completed.
    /// </summary>
    [HttpPost("auth/cancel")]
    public ActionResult CancelLogin()
    {
        _xboxCatalogMappingService.CancelLogin();
        return Ok(ApiResponse.Message("Xbox login cancelled"));
    }

    /// <summary>
    /// Logs out the manager-side mapping session and clears saved credentials. No Docker container to stop.
    /// </summary>
    [HttpDelete("auth")]
    public async Task<ActionResult> LogoutAsync()
    {
        await _xboxCatalogMappingService.LogoutAsync();
        return Ok(ApiResponse.Message("Xbox mapping logged out"));
    }

    /// <summary>
    /// Search games by title (case-insensitive partial match).
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<XboxGameMappingDto>>> SearchGamesAsync(
        [FromQuery] string q,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return BadRequest(ApiResponse.Error("Search query must be at least 2 characters"));

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var pattern = $"%{q}%";
        var dtos = await db.XboxGameMappings
            .AsNoTracking()
            .Where(m => EF.Functions.Like(m.Title, pattern))
            .OrderBy(m => m.Title)
            .Select(m => new XboxGameMappingDto
            {
                ProductId = m.ProductId,
                Title = m.Title,
                DiscoveredAtUtc = m.DiscoveredAtUtc,
                LastSeenAtUtc = m.LastSeenAtUtc,
                ImageUrl = m.ImageUrl
            })
            .ToListAsync(ct);

        return Ok(dtos);
    }

    /// <summary>
    /// Manually refreshes the Xbox catalog: collects product-&gt;title + CDN fragments from the daemon's
    /// already-authenticated session(s) via <c>get-cdn-info</c>, then resolves unmatched downloads.
    /// Mirrors Epic's <c>POST /refresh</c> manual trigger and is decoupled from prefill - it re-reads the
    /// existing session (no prefill download needed). The same collection also runs on a schedule
    /// (Schedules page, key <c>xboxMapping</c>) and opportunistically when a session authenticates.
    /// </summary>
    [HttpPost("refresh-catalog")]
    public async Task<ActionResult> RefreshCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _xboxCatalogMappingService.RefreshNowAsync(ct);
            return Ok(new
            {
                newPatterns = result.NewPatterns,
                resolved = result.Resolved,
                message = $"Collected {result.NewPatterns} new CDN pattern(s); re-tagged {result.Resolved} download(s)"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Xbox catalog");
            return StatusCode(500, ApiResponse.Error("Failed to refresh Xbox catalog: " + ex.Message));
        }
    }

    /// <summary>
    /// Resolves unmatched wsus downloads against stored Xbox CDN patterns only (no scan).
    /// Backfills INACTIVE rows that the ingest path missed; the primary path runs automatically
    /// after each log process.
    /// </summary>
    [HttpPost("resolve")]
    public async Task<ActionResult> ResolveDownloadsAsync(CancellationToken ct = default)
    {
        try
        {
            var resolved = await _xboxMappingService.ResolveDownloadsAsync(ct);
            return Ok(new
            {
                resolved,
                message = $"Resolved {resolved} Xbox download(s) to game names"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve Xbox downloads");
            return StatusCode(500, ApiResponse.Error("Failed to resolve Xbox downloads: " + ex.Message));
        }
    }
}

/// <summary>
/// DTO for Xbox game mapping responses.
/// </summary>
public class XboxGameMappingDto
{
    public string ProductId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime DiscoveredAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public string? ImageUrl { get; set; }
}

/// <summary>
/// DTO for Xbox mapping statistics.
/// </summary>
public class XboxMappingStatsDto
{
    public int TotalGames { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
}
