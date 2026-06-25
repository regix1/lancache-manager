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
    private readonly ILogger<XboxGameMappingController> _logger;

    public XboxGameMappingController(
        IDbContextFactory<AppDbContext> dbContextFactory,
        XboxMappingService xboxMappingService,
        ILogger<XboxGameMappingController> logger)
    {
        _dbContextFactory = dbContextFactory;
        _xboxMappingService = xboxMappingService;
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
