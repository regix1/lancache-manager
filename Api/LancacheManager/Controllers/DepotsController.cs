using System.Text.Json;
using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for depot mapping management
/// Handles Steam PICS data, depot rebuilds, imports, and mappings
/// Note: /steamkit/* routes have been renamed to /depots/* for proper resource-based naming
/// Note: Configuration endpoints moved to SystemController at /api/system/depots/*
/// </summary>
[ApiController]
[Route("api/depots")]
public class DepotsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly PicsDataService _picsDataService;
    private readonly ILogger<DepotsController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public DepotsController(
        AppDbContext context,
        SteamKit2Service steamKit2Service,
        PicsDataService picsDataService,
        ILogger<DepotsController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _steamKit2Service = steamKit2Service;
        _picsDataService = picsDataService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// GET /api/depots/status - Get status of depot mappings (PICS JSON and database)
    /// RESTful: Proper resource status endpoint
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetDepotStatus()
    {
        try
        {
            var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
            var needsUpdate = await _picsDataService.NeedsUpdateAsync();
            var dbMappingCount = _steamKit2Service.GetDepotMappingCount();

            return Ok(new
            {
                jsonFile = new
                {
                    exists = picsData != null,
                    path = _picsDataService.GetPicsJsonFilePath(),
                    lastUpdated = picsData?.Metadata?.LastUpdated,
                    totalMappings = picsData?.Metadata?.TotalMappings ?? 0,
                    nextUpdateDue = picsData?.Metadata?.NextUpdateDue,
                    needsUpdate
                },
                database = new
                {
                    totalMappings = dbMappingCount
                },
                steamKit2 = new
                {
                    isReady = _steamKit2Service.IsReady,
                    isRebuildRunning = _steamKit2Service.IsRebuildRunning,
                    depotCount = dbMappingCount
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting depot status");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/depots/rebuild?incremental=true|false - Start depot mapping rebuild
    /// RESTful: POST is acceptable for starting async operations
    /// PRE-FLIGHT VIABILITY CHECK:
    /// - If incremental=true: Checks with Steam if incremental scan is viable BEFORE starting
    /// - If Steam requires full scan: Returns requiresFullScan flag to show modal to user
    /// - If incremental=false: Skips viability check and proceeds directly to full scan
    /// </summary>
    [HttpPost("rebuild")]
    [RequireAuth]
    public async Task<IActionResult> StartDepotRebuild(CancellationToken cancellationToken, [FromQuery] bool incremental = false)
    {
        try
        {
            // PRE-FLIGHT CHECK: Only check viability if user requested incremental scan
            if (incremental)
            {
                _logger.LogInformation("Incremental scan requested - checking viability first");
                var viability = await _steamKit2Service.CheckIncrementalViabilityAsync(cancellationToken);
                _logger.LogInformation("Viability check returned: {Viability}", System.Text.Json.JsonSerializer.Serialize(viability));

                if (viability.WillTriggerFullScan)
                {
                    _logger.LogInformation("Incremental scan not viable - change gap too large ({ChangeGap}). Returning requiresFullScan flag.", viability.ChangeGap);

                    return Ok(new
                    {
                        started = false,
                        requiresFullScan = true,
                        changeGap = viability.ChangeGap,
                        estimatedApps = viability.EstimatedAppsToScan,
                        message = viability.Error ?? "Change gap is too large for incremental scan. A full scan is required.",
                        viabilityError = viability.Error
                    });
                }
                else
                {
                    _logger.LogInformation("Incremental scan is viable - proceeding with scan");
                    _steamKit2Service.ClearAutomaticScanSkippedFlag();
                }
            }

            // Proceed with scan
            var started = _steamKit2Service.TryStartRebuild(cancellationToken, incremental);

            if (started)
            {
                _steamKit2Service.EnablePeriodicCrawls();
            }

            return Accepted(new
            {
                started,
                requiresFullScan = false,
                rebuildInProgress = _steamKit2Service.IsRebuildRunning,
                ready = _steamKit2Service.IsReady,
                depotCount = _steamKit2Service.GetDepotMappingCount()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering depot rebuild");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/depots/rebuild/progress - Get current depot rebuild progress
    /// RESTful: Progress is a sub-resource of the rebuild operation
    /// </summary>
    [HttpGet("rebuild/progress")]
    public IActionResult GetRebuildProgress()
    {
        try
        {
            var progress = _steamKit2Service.GetProgress();
            return Ok(progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting depot rebuild progress");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/depots/rebuild - Cancel the current depot rebuild
    /// RESTful: DELETE is proper method for cancelling/removing operations
    /// </summary>
    [HttpDelete("rebuild")]
    [RequireAuth]
    public async Task<IActionResult> CancelRebuild()
    {
        try
        {
            var cancelled = await _steamKit2Service.CancelRebuildAsync();

            if (cancelled)
            {
                return Ok(new { message = "Depot rebuild cancelled successfully" });
            }
            else
            {
                return NotFound(new { message = "No active rebuild to cancel" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling depot rebuild");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/depots/rebuild/check-incremental - Check if incremental scan is viable
    /// RESTful: This is a query/check operation on the rebuild resource
    /// </summary>
    [HttpGet("rebuild/check-incremental")]
    [RequireAuth]
    public async Task<IActionResult> CheckIncrementalViability(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _steamKit2Service.CheckIncrementalViabilityAsync(cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking incremental viability");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/depots/import?source=github|local - Import depot mappings
    /// RESTful: POST is proper method for importing/creating resources
    /// Query param 'source' determines import source: 'github' or 'local'
    /// </summary>
    [HttpPost("import")]
    [RequireAuth]
    public async Task<IActionResult> ImportDepotMappings([FromQuery] string source, CancellationToken cancellationToken)
    {
        try
        {
            if (source == "github")
            {
                _logger.LogInformation("Starting download of pre-created depot data from GitHub");

                var success = await _steamKit2Service.DownloadAndImportGitHubDataAsync(cancellationToken);

                if (success)
                {
                    _steamKit2Service.ClearAutomaticScanSkippedFlag();
                    _steamKit2Service.EnablePeriodicCrawls();

                    _logger.LogInformation("Pre-created depot data downloaded and imported successfully from GitHub");

                    return Ok(new
                    {
                        message = "Pre-created depot data downloaded and imported successfully",
                        source = "GitHub",
                        timestamp = DateTime.UtcNow
                    });
                }
                else
                {
                    return StatusCode(500, new { error = "Failed to download and import pre-created depot data from GitHub" });
                }
            }
            else if (source == "local")
            {
                _logger.LogInformation("Starting import of existing PICS data to database");

                await _picsDataService.ImportJsonDataToDatabaseAsync(cancellationToken);
                _steamKit2Service.EnablePeriodicCrawls();

                return Ok(new
                {
                    message = "PICS data imported successfully",
                    source = "Local",
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                return BadRequest(new { error = "Invalid source. Must be 'github' or 'local'" });
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error while downloading pre-created depot data");
            return StatusCode(500, new { error = "Network error: Unable to download from GitHub. Check your internet connection." });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout while downloading pre-created depot data");
            return StatusCode(500, new { error = "Download timeout: The GitHub file is taking too long to download." });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Import operation was cancelled by user");
            return StatusCode(499, new { error = "Operation cancelled by user" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing depot mappings");
            return StatusCode(500, new { error = "Failed to import depot mappings", details = ex.Message });
        }
    }

    /// <summary>
    /// PATCH /api/depots - Apply depot mappings to existing downloads
    /// RESTful: PATCH is proper method for applying updates to a resource collection
    /// </summary>
    [HttpPatch]
    [RequireAuth]
    public async Task<IActionResult> ApplyDepotMappings(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting manual depot mapping application");

            await _steamKit2Service.ManuallyApplyDepotMappings();

            _logger.LogInformation("Manual depot mapping completed successfully");

            return Ok(new
            {
                message = "Depot mappings applied successfully",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply depot mappings");
            return StatusCode(500, new { error = ex.Message });
        }
    }


    /// <summary>
    /// PUT /api/depots/rebuild/config/interval - Set the automatic crawl interval
    /// RESTful: PUT is proper method for updating configuration
    /// </summary>
    /// <param name="intervalHours">Interval in hours (supports fractional values like 0.00833 for 30 seconds). Use 0 to disable.</param>
    [HttpPut("rebuild/config/interval")]
    [RequireAuth]
    public IActionResult SetCrawlInterval([FromBody] double intervalHours)
    {
        try
        {
            _logger.LogInformation("Received crawl interval request: {IntervalHours} hours", intervalHours);

            if (intervalHours < 0)
            {
                return BadRequest(new { error = $"Interval must be 0 or a positive number. Received: {intervalHours}" });
            }

            _steamKit2Service.CrawlIntervalHours = intervalHours;

            var actualInterval = _steamKit2Service.CrawlIntervalHours;
            _logger.LogInformation("Crawl interval set. Requested: {Requested}, Actual: {Actual}", intervalHours, actualInterval);

            if (intervalHours == 0)
            {
                _logger.LogInformation("Automatic crawl schedule disabled");
            }
            else if (intervalHours < 1)
            {
                var seconds = intervalHours * 3600;
                _logger.LogInformation("Crawl interval updated to {Seconds} seconds", (int)seconds);
            }
            else
            {
                _logger.LogInformation("Crawl interval updated to {IntervalHours} hours", intervalHours);
            }

            return Ok(new
            {
                intervalHours = _steamKit2Service.CrawlIntervalHours,
                message = intervalHours == 0
                    ? "Automatic schedule disabled"
                    : intervalHours < 1
                        ? $"Crawl interval updated to {(int)(intervalHours * 3600)} seconds (testing mode)"
                        : $"Crawl interval updated to {intervalHours} hour(s)"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting crawl interval");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// PUT /api/depots/rebuild/config/mode - Set the automatic crawl mode
    /// RESTful: PUT is proper method for updating configuration
    /// </summary>
    /// <param name="mode">Mode value: true (incremental), false (full), or "github" (PICS updates only)</param>
    [HttpPut("rebuild/config/mode")]
    [RequireAuth]
    public IActionResult SetCrawlMode([FromBody] JsonElement mode)
    {
        try
        {
            string scanMode;

            // Handle different input types: bool or string "github"
            if (mode.ValueKind == JsonValueKind.True)
            {
                _steamKit2Service.CrawlIncrementalMode = true;
                scanMode = "Incremental";
            }
            else if (mode.ValueKind == JsonValueKind.False)
            {
                _steamKit2Service.CrawlIncrementalMode = false;
                scanMode = "Full";
            }
            else if (mode.ValueKind == JsonValueKind.String && mode.GetString() == "github")
            {
                _steamKit2Service.CrawlIncrementalMode = "github";
                scanMode = "GitHub (PICS Updates)";
            }
            else
            {
                return BadRequest(new { error = "Invalid scan mode. Must be true, false, or \"github\"" });
            }

            _logger.LogInformation("Crawl mode updated to {Mode}", scanMode);

            return Ok(new
            {
                incrementalMode = _steamKit2Service.CrawlIncrementalMode,
                message = $"Automatic scan mode set to {scanMode}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting crawl mode");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
