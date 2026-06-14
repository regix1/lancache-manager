using LancacheManager.Core.Services.BattleNet;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// REST API endpoints for Blizzard / Battle.net game mappings.
///
/// Re-maps existing Blizzard downloads to game names by resolving the TACT CDN-path /
/// product segment from <c>Downloads.LastUrl</c> against the single-sourced
/// <c>tact_products.json</c> catalog. Mirrors the Epic mapping controller's resolve/stats
/// shape, minus auth/login (the TACT catalog is static and public - no account needed).
/// </summary>
[ApiController]
[Route("api/blizzard/game-mappings")]
[Authorize(Policy = "AdminOnly")]
public class BattleNetMappingController : ControllerBase
{
    // Serialize resolve runs so two Apply-Now clicks cannot run the re-map concurrently.
    private static readonly SemaphoreSlim _resolveLock = new(1, 1);

    private readonly BattleNetMappingService _battleNetMappingService;
    private readonly ILogger<BattleNetMappingController> _logger;

    public BattleNetMappingController(
        BattleNetMappingService battleNetMappingService,
        ILogger<BattleNetMappingController> logger)
    {
        _battleNetMappingService = battleNetMappingService;
        _logger = logger;
    }

    /// <summary>
    /// Apply Now: re-maps unnamed Blizzard downloads to game names from their LastUrl.
    /// </summary>
    [HttpPost("resolve")]
    public async Task<ActionResult> ResolveDownloadsAsync(CancellationToken ct = default)
    {
        if (!await _resolveLock.WaitAsync(0, ct))
        {
            return Conflict(ApiResponse.Error("A Blizzard download resolution is already running"));
        }

        try
        {
            var resolved = await _battleNetMappingService.ResolveDownloadsAsync(ct);
            return Ok(new
            {
                resolved,
                message = $"Resolved {resolved} Blizzard download(s) to game names"
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, ApiResponse.Error("Blizzard download resolution was cancelled"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve Blizzard downloads");
            return StatusCode(500, ApiResponse.Error("Failed to resolve Blizzard downloads: " + ex.Message));
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    /// <summary>
    /// Gets Blizzard mapping statistics (catalog product count, unnamed download count, last applied).
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<BattleNetMappingStats>> GetStatsAsync(CancellationToken ct = default)
    {
        return Ok(await _battleNetMappingService.GetStatsAsync(ct));
    }
}
