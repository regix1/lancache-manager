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
/// AccountHolder read of the global mapping/catalog. Resolution is automatic (the Rust ingest + the
/// RustLogProcessor post-pass + the xboxMapping schedule), so this controller only exposes the
/// catalog read plus the manager-side login.
/// </summary>
[ApiController]
[Route("api/xbox/game-mappings")]
[Authorize(Policy = "AccountHolder")]
public class XboxGameMappingController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly XboxCatalogMappingService _xboxCatalogMappingService;

    public XboxGameMappingController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        XboxCatalogMappingService xboxCatalogMappingService)
    {
        _dbContextFactory = dbContextFactory;
        _xboxCatalogMappingService = xboxCatalogMappingService;
    }

    /// <summary>
    /// Gets all Xbox game mappings, optionally paginated.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<XboxGameMappingDto>), StatusCodes.Status200OK)]
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
    [ProducesResponseType(typeof(XboxMappingStatsDto), StatusCodes.Status200OK)]
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
    /// Gets the current manager-side mapping auth status.
    /// </summary>
    /// <remarks>
    /// Includes authenticated state, gamertag, last collection, and count. Mirrors Epic's
    /// <c>GET auth-status</c>. Synchronous, no I/O.
    /// </remarks>
    [HttpGet("auth-status")]
    [ProducesResponseType(typeof(XboxMappingAuthStatus), StatusCodes.Status200OK)]
    public ActionResult<XboxMappingAuthStatus> GetAuthStatus()
    {
        return Ok(_xboxCatalogMappingService.GetAuthStatus());
    }

    /// <summary>
    /// Starts the daemon-free Xbox MSA device-code login.
    /// </summary>
    /// <remarks>
    /// Returns the <c>userCode</c>/<c>verificationUri</c> for the user to approve in their browser
    /// and kicks a background poll loop. No Docker container and no prefill daemon are created.
    /// Authentication state is surfaced via <c>XboxMappingAuthStateChanged</c>; approved catalog
    /// work then uses the tracked Xbox mapping lifecycle (there is no code-paste complete step, the
    /// backend polls the token endpoint).
    /// </remarks>
    [HttpPost("auth/login")]
    [ProducesResponseType(typeof(XboxDeviceCodeChallenge), StatusCodes.Status200OK)]
    public async Task<ActionResult<XboxDeviceCodeChallenge>> StartLoginAsync(CancellationToken ct = default)
    {
        // Cancellation can come from the initiating request, a superseding login, logout, or
        // modal close. Let the global exception middleware classify it as a quiet 499 instead of
        // logging the expected TaskCanceledException as a failed login here.
        var challenge = await _xboxCatalogMappingService.StartLoginAsync(ct);
        return Ok(challenge);
    }

    /// <summary>
    /// Cancels a pending device-code login poll.
    /// </summary>
    /// <remarks>
    /// Used e.g. when the user closes the login modal. Does NOT clear credentials or sign out an
    /// already-authenticated account. Unlike <see cref="LogoutAsync"/>, this only stops a poll that
    /// has not completed.
    /// </remarks>
    [HttpPost("auth/cancel")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public ActionResult<MessageOnlyResponse> CancelLogin()
    {
        _xboxCatalogMappingService.CancelLogin();
        return Ok(new MessageOnlyResponse { Message = "Xbox login cancelled" });
    }

    /// <summary>
    /// Logs out the manager-side mapping session and clears saved credentials.
    /// </summary>
    /// <remarks>
    /// No Docker container to stop.
    /// </remarks>
    [HttpDelete("auth")]
    [ProducesResponseType(typeof(MessageOnlyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageOnlyResponse>> LogoutAsync()
    {
        await _xboxCatalogMappingService.LogoutAsync();
        return Ok(new MessageOnlyResponse { Message = "Xbox mapping logged out" });
    }

    /// <summary>
    /// Search games by title (case-insensitive partial match).
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<XboxGameMappingDto>), StatusCodes.Status200OK)]
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
