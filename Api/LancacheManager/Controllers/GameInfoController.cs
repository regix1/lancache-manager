using LancacheManager.Data;
using LancacheManager.Security;
using LancacheManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameInfoController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly PicsDataService _picsDataService;
    private readonly ILogger<GameInfoController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public GameInfoController(
        AppDbContext context,
        SteamKit2Service steamKit2Service,
        PicsDataService picsDataService,
        ILogger<GameInfoController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _steamKit2Service = steamKit2Service;
        _picsDataService = picsDataService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }


    /// <summary>
    /// Trigger a background Steam PICS crawl to rebuild depot mappings.
    /// Checks viability first if incremental is requested - returns requiresFullScan flag if gap is too large.
    /// </summary>
    /// <param name="incremental">If true, performs incremental update (only changed apps), otherwise full rebuild</param>
    [HttpPost("steamkit/rebuild")]
    [RequireAuth]
    public async Task<IActionResult> TriggerSteamKitRebuild(CancellationToken cancellationToken, [FromQuery] bool incremental = false)
    {
        try
        {
            // If incremental scan requested, check viability first
            if (incremental)
            {
                _logger.LogInformation("Incremental scan requested - checking viability first");
                var viability = await _steamKit2Service.CheckIncrementalViabilityAsync(cancellationToken);
                _logger.LogInformation("Viability check returned: {Viability}", System.Text.Json.JsonSerializer.Serialize(viability));

                if (viability.WillTriggerFullScan)
                {
                    // Return info about required full scan WITHOUT starting the scan
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
                    // Clear the automatic scan skipped flag since incremental is now viable
                    _steamKit2Service.ClearAutomaticScanSkippedFlag();
                }
            }

            // Proceed with scan (either full scan requested, or incremental is viable)
            var started = _steamKit2Service.TryStartRebuild(cancellationToken, incremental);

            if (started)
            {
                // Enable periodic crawls now that user has initiated data generation
                _steamKit2Service.EnablePeriodicCrawls();
            }

            return Ok(new
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
            _logger.LogError(ex, "Error triggering SteamKit PICS rebuild");
            return StatusCode(500, new { error = ex.Message });
        }
    }


    /// <summary>
    /// Get current PICS crawl progress and status
    /// </summary>
    [HttpGet("steamkit/progress")]
    public IActionResult GetSteamKitProgress()
    {
        try
        {
            var progress = _steamKit2Service.GetProgress();
            return Ok(progress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting SteamKit PICS progress");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel the current PICS crawl/scan
    /// </summary>
    [HttpPost("steamkit/cancel")]
    public async Task<IActionResult> CancelSteamKitRebuild()
    {
        try
        {
            var cancelled = await _steamKit2Service.CancelRebuildAsync();

            if (cancelled)
            {
                return Ok(new { message = "PICS scan cancelled successfully" });
            }
            else
            {
                return Ok(new { message = "No active scan to cancel" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling SteamKit PICS scan");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Check if incremental scan is viable or if change gap is too large
    /// </summary>
    [HttpGet("steamkit/check-incremental")]
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
    /// Set the crawl interval for periodic depot mapping updates
    /// </summary>
    /// <param name="intervalHours">Interval in hours (supports fractional values like 0.00833 for 30 seconds)</param>
    [HttpPost("steamkit/interval")]
    [RequireAuth]
    public IActionResult SetCrawlInterval([FromBody] double intervalHours)
    {
        try
        {
            _logger.LogInformation("Received crawl interval request: {IntervalHours} hours", intervalHours);

            // Allow 0 (disabled), or between 1 hour and 168 hours (1 week)
            if (intervalHours < 0 || (intervalHours > 0 && intervalHours < 1) || intervalHours > 168)
            {
                return BadRequest(new { error = $"Interval must be 0 (disabled) or between 1 and 168 hours. Received: {intervalHours}" });
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
    /// Set the crawl mode for automatic scheduled depot mapping updates
    /// </summary>
    /// <param name="incremental">True for incremental scans, false for full scans</param>
    [HttpPost("steamkit/scan-mode")]
    [RequireAuth]
    public IActionResult SetCrawlMode([FromBody] bool incremental)
    {
        try
        {
            _steamKit2Service.CrawlIncrementalMode = incremental;
            _logger.LogInformation("Crawl mode updated to {Mode}", incremental ? "Incremental" : "Full");

            return Ok(new
            {
                incrementalMode = _steamKit2Service.CrawlIncrementalMode,
                message = $"Automatic scan mode set to {(incremental ? "incremental" : "full")} scans"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting crawl mode");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get status of PICS JSON data and database
    /// </summary>
    [HttpGet("pics-status")]
    public async Task<IActionResult> GetPicsStatus()
    {
        try
        {
            var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
            var needsUpdate = await _picsDataService.NeedsUpdateAsync();

            // Count database depot mappings
            var dbMappingCount = await _context.SteamDepotMappings.CountAsync();

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
                    depotCount = _steamKit2Service.GetDepotMappingCount()
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting PICS status");
            return StatusCode(500, new { error = ex.Message });
        }
    }





    /// <summary>
    /// Download pre-created depot mappings from GitHub repo
    /// </summary>
    [HttpPost("download-precreated-data")]
    [RequireAuth]
    public async Task<IActionResult> DownloadPrecreatedDepotData(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting download of pre-created depot data from GitHub");

            const string githubUrl = "https://raw.githubusercontent.com/regix1/lancache-pics/main/output/pics_depot_mappings.json";

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 minute timeout for large file

            _logger.LogInformation($"Downloading from: {githubUrl}");

            var response = await httpClient.GetAsync(githubUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to download pre-created data: HTTP {response.StatusCode}");
                return BadRequest(new {
                    error = "Failed to download pre-created depot data from GitHub",
                    statusCode = response.StatusCode,
                    url = githubUrl
                });
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                return BadRequest(new { error = "Downloaded file is empty" });
            }

            // Validate JSON structure
            try
            {
                var testData = JsonSerializer.Deserialize<PicsJsonData>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (testData?.DepotMappings == null || !testData.DepotMappings.Any())
                {
                    return BadRequest(new { error = "Downloaded file does not contain valid depot mappings" });
                }

                _logger.LogInformation($"Downloaded {testData.Metadata?.TotalMappings ?? 0} depot mappings");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Downloaded file is not valid JSON");
                return BadRequest(new { error = "Downloaded file is not valid JSON format" });
            }

            // Save to local file
            var localPath = _picsDataService.GetPicsJsonFilePath();
            await System.IO.File.WriteAllTextAsync(localPath, jsonContent, cancellationToken);

            _logger.LogInformation($"Saved pre-created depot data to: {localPath}");

            // Clear existing depot mappings before importing (GitHub download is a full replacement)
            _logger.LogInformation("Clearing existing depot mappings for full replacement");
            await _picsDataService.ClearDepotMappingsAsync(cancellationToken);

            // Import to database
            await _picsDataService.ImportJsonDataToDatabaseAsync(cancellationToken);

            // Apply depot mappings to existing downloads
            _logger.LogInformation("Applying depot mappings to existing downloads");
            await _steamKit2Service.ManuallyApplyDepotMappings();

            // Get current change number from Steam and update JSON metadata
            // This allows incremental scans to work from the current position going forward
            try
            {
                _logger.LogInformation("Getting current Steam change number to update GitHub data metadata");
                var currentChangeNumber = await _steamKit2Service.GetCurrentChangeNumberAsync(cancellationToken);
                await _picsDataService.UpdateLastChangeNumberAsync(currentChangeNumber);
                _logger.LogInformation("Updated JSON metadata with current change number {ChangeNumber} - incremental scans will now work", currentChangeNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update change number in JSON metadata - incremental scans may not work until next full scan");
            }

            // Clear the automatic scan skipped flag since user took manual action
            _steamKit2Service.ClearAutomaticScanSkippedFlag();

            // Enable periodic crawls now that we have initial data
            _steamKit2Service.EnablePeriodicCrawls();

            // Trigger a simple incremental scan to catch recent updates (no viability check, just do it)
            // This runs synchronously to avoid connection conflicts
            var scanStarted = _steamKit2Service.TryStartRebuild(CancellationToken.None, incrementalOnly: true);

            if (scanStarted)
            {
                _logger.LogInformation("Started incremental scan after GitHub download to catch recent updates");
                // Update last crawl time after scan starts successfully
                _steamKit2Service.UpdateLastCrawlTime();
            }
            else
            {
                _logger.LogWarning("Could not start incremental scan after GitHub download (another scan may be running)");
                // Still update last crawl time to prevent immediate retry
                _steamKit2Service.UpdateLastCrawlTime();
            }

            return Ok(new
            {
                message = scanStarted
                    ? "Pre-created depot data downloaded and imported successfully. Starting incremental scan to catch recent updates..."
                    : "Pre-created depot data downloaded and imported successfully",
                source = "GitHub",
                url = githubUrl,
                localPath = localPath,
                timestamp = DateTime.UtcNow
            });
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
            _logger.LogInformation("Download of pre-created depot data was cancelled by user");
            return StatusCode(499, new { error = "Operation cancelled by user" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading pre-created depot data");
            return StatusCode(500, new { error = "Failed to download and import pre-created depot data", details = ex.Message });
        }
    }

    /// <summary>
    /// Import existing PICS JSON file to database
    /// </summary>
    [HttpPost("import-pics-data")]
    [RequireAuth]
    public async Task<IActionResult> ImportPicsData(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting import of existing PICS data to database");

            // Import to database
            await _picsDataService.ImportJsonDataToDatabaseAsync(cancellationToken);

            // Enable periodic crawls now that we have data
            _steamKit2Service.EnablePeriodicCrawls();

            return Ok(new
            {
                message = "PICS data imported successfully",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import PICS data");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Manually apply depot mappings to existing downloads
    /// </summary>
    [HttpPost("apply-depot-mappings")]
    [RequireAuth]
    public async Task<IActionResult> ApplyDepotMappings(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[API] apply-depot-mappings endpoint called - starting manual depot mapping");

            // Call the depot mapping method directly
            await _steamKit2Service.ManuallyApplyDepotMappings();

            _logger.LogInformation("[API] Manual depot mapping completed successfully");

            return Ok(new
            {
                message = "Depot mappings applied successfully",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[API] Failed to apply depot mappings");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
