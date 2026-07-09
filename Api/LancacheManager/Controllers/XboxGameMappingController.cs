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
/// AdminOnly read of the global mapping/catalog. Resolution is automatic (the Rust ingest + the
/// RustLogProcessor post-pass + the xboxMapping schedule), so this controller only exposes the
/// catalog read plus the manager-side login.
/// </summary>
[ApiController]
[Route("api/xbox/game-mappings")]
[Authorize(Policy = "AdminOnly")]
public class XboxGameMappingController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly XboxCatalogMappingService _xboxCatalogMappingService;
    private readonly ILogger<XboxGameMappingController> _logger;

    public XboxGameMappingController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        XboxCatalogMappingService xboxCatalogMappingService,
        ILogger<XboxGameMappingController> logger)
    {
        _dbContextFactory = dbContextFactory;
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
            throw; // -> GlobalExceptionMiddleware -> 500 safe { error, details?, statusCode, traceId }
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
