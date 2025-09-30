using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Text.Json;
using LancacheManager.Services;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GameInfoController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly SteamService _steamService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly PicsDataService _picsDataService;
    private readonly ILogger<GameInfoController> _logger;

    public GameInfoController(
        AppDbContext context,
        SteamService steamService,
        SteamKit2Service steamKit2Service,
        PicsDataService picsDataService,
        ILogger<GameInfoController> logger)
    {
        _context = context;
        _steamService = steamService;
        _steamKit2Service = steamKit2Service;
        _picsDataService = picsDataService;
        _logger = logger;
    }


    /// <summary>
    /// Trigger a background Steam PICS crawl to rebuild depot mappings.
    /// </summary>
    /// <param name="incremental">If true, performs incremental update (only changed apps), otherwise full rebuild</param>
    [HttpPost("steamkit/rebuild")]
    public IActionResult TriggerSteamKitRebuild(CancellationToken cancellationToken, [FromQuery] bool incremental = false)
    {
        try
        {
            var started = _steamKit2Service.TryStartRebuild(cancellationToken, incremental);

            if (started)
            {
                // Enable periodic crawls now that user has initiated data generation
                _steamKit2Service.EnablePeriodicCrawls();
            }

            return Ok(new
            {
                started,
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
    /// Set the crawl interval for periodic depot mapping updates
    /// </summary>
    /// <param name="intervalHours">Interval in hours</param>
    [HttpPost("steamkit/interval")]
    public IActionResult SetCrawlInterval([FromBody] double intervalHours)
    {
        try
        {
            if (intervalHours < 0.1 || intervalHours > 168) // Min 6 minutes, max 1 week
            {
                return BadRequest(new { error = "Interval must be between 0.1 and 168 hours" });
            }

            _steamKit2Service.CrawlIntervalHours = intervalHours;
            _logger.LogInformation("Crawl interval updated to {IntervalHours} hours", intervalHours);

            return Ok(new
            {
                intervalHours = _steamKit2Service.CrawlIntervalHours,
                message = $"Crawl interval updated to {intervalHours} hour(s)"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting crawl interval");
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
                    needsUpdate = needsUpdate
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
    public async Task<IActionResult> DownloadPrecreatedDepotData(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting download of pre-created depot data from GitHub");

            const string githubUrl = "https://raw.githubusercontent.com/regix1/lancache-manager/main/Data/pics_depot_mappings.json";

            using var httpClient = new HttpClient();
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

            // Import to database
            await _picsDataService.ImportJsonDataToDatabaseAsync(cancellationToken);

            // Enable periodic crawls now that we have initial data
            _steamKit2Service.EnablePeriodicCrawls();

            return Ok(new
            {
                message = "Pre-created depot data downloaded and imported successfully",
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
}
