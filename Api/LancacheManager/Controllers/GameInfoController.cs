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
    /// List available depot mappings (first 10) from SteamKit2
    /// </summary>
    [HttpGet("list-depots")]
    public IActionResult ListAvailableDepots()
    {
        try
        {
            var mappings = _steamKit2Service.GetSampleDepotMappings(10).Select(m => new {
                depotId = m.Key,
                appId = m.Value
            }).ToList();

            return Ok(new {
                totalMappings = _steamKit2Service.GetDepotMappingCount(),
                sampleMappings = mappings,
                steamKit2Ready = _steamKit2Service.IsReady,
                rebuildInProgress = _steamKit2Service.IsRebuildRunning,
                source = "SteamKit2"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing depot mappings");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Test depot mapping for a specific depot ID
    /// </summary>
    [HttpGet("test-depot/{id}")]
    public async Task<IActionResult> TestDepotMapping(uint id)
    {
        try
        {
            _logger.LogInformation($"Testing depot mapping for depot {id}");

            // Heuristic: depot IDs rarely match their owner AppID,
            // and DLC appids like 2278280 will never be valid depots.
            if (_steamKit2Service.GetAppIdsForDepot(id).Count == 0 &&
                (await _picsDataService.GetAppIdsForDepotFromJsonAsync(id)).Count == 0)
            {
                // If it looks like an AppID with a valid store page, tell the user
                var maybeAppInfo = await _steamService.GetGameInfoAsync(id);
                if (maybeAppInfo != null)
                {
                    return Ok(new {
                        depotId = (uint?)null,
                        appId = id,
                        gameName = maybeAppInfo.Name,
                        message = $"The ID {id} looks like an AppID (\"{maybeAppInfo.Name}\"), not a depot ID.",
                        success = false,
                        source = "AppID-Detection"
                    });
                }
            }

            // Try to get app ID from depot using SteamKit2 first
            var appId = _steamKit2Service.GetAppIdFromDepot(id);
            string source = "SteamKit2";

            // Fallback to JSON file if no database mapping found
            if (!appId.HasValue)
            {
                var jsonAppIds = await _picsDataService.GetAppIdsForDepotFromJsonAsync(id);
                if (jsonAppIds.Any())
                {
                    appId = jsonAppIds.First();
                    source = "JSON";
                }
            }


            if (!appId.HasValue)
            {
                return Ok(new {
                    depotId = id,
                    appId = (uint?)null,
                    gameName = "No mapping found",
                    success = false,
                    source = "None",
                    steamKit2Ready = _steamKit2Service.IsReady,
                    rebuildInProgress = _steamKit2Service.IsRebuildRunning
                });
            }

            // Get game info
            var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);

            return Ok(new {
                depotId = id,
                appId = appId.Value,
                gameName = gameInfo?.Name ?? "Unknown",
                gameType = gameInfo?.Type,
                success = true,
                source = source,
                steamKit2Ready = _steamKit2Service.IsReady,
                rebuildInProgress = _steamKit2Service.IsRebuildRunning
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error testing depot mapping for depot {id}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get current PICS crawl progress
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
    /// Update all downloads with depot IDs to include game information
    /// </summary>
    [HttpPost("update-depot-mappings")]
    public async Task<IActionResult> UpdateDepotMappings()
    {
        return await UpdateDepotMappingsInternal(false);
    }

    /// <summary>
    /// Force update all downloads with depot IDs, overriding existing mappings
    /// </summary>
    [HttpPost("update-depot-mappings/force")]
    public async Task<IActionResult> ForceUpdateDepotMappings()
    {
        return await UpdateDepotMappingsInternal(true);
    }

    private async Task<IActionResult> UpdateDepotMappingsInternal(bool forceUpdate)
    {
        try
        {
            _logger.LogInformation($"Starting depot mapping update for downloads (force={forceUpdate})");

            // Get downloads that have depot IDs and either no game info or force update all
            var downloadsNeedingGameInfo = await _context.Downloads
                .Where(d => d.DepotId.HasValue && (forceUpdate || d.GameAppId == null))
                .ToListAsync();

            _logger.LogInformation($"Found {downloadsNeedingGameInfo.Count} downloads needing game info");

            int updated = 0;
            int notFound = 0;

            foreach (var download in downloadsNeedingGameInfo)
            {
                try
                {
                    uint? appId = null;

                    // Use SteamKit2Service PICS mappings first
                    var appIds = _steamKit2Service.GetAppIdsForDepot(download.DepotId.Value);
                    if (appIds.Any())
                    {
                        appId = appIds.First(); // Take the first app ID if multiple exist
                    }
                    else
                    {
                        // Fallback to JSON file
                        var jsonAppIds = await _picsDataService.GetAppIdsForDepotFromJsonAsync(download.DepotId.Value);
                        if (jsonAppIds.Any())
                        {
                            appId = jsonAppIds.First();
                        }
                    }


                    if (appId.HasValue)
                    {
                        download.GameAppId = appId.Value;

                        // Get game info from Steam API
                        var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                        if (gameInfo != null)
                        {
                            download.GameName = gameInfo.Name;
                            download.GameImageUrl = gameInfo.HeaderImage;
                            updated++;

                            _logger.LogDebug($"Updated download {download.Id}: depot {download.DepotId} -> {gameInfo.Name} (App {appId})");
                        }
                        else
                        {
                            download.GameName = $"Steam App {appId}";
                            updated++;
                        }
                    }
                    else
                    {
                        notFound++;
                        _logger.LogDebug($"No mapping found for depot {download.DepotId}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get game info for depot {download.DepotId}");
                    notFound++;
                }
            }

            if (updated > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation($"Updated {updated} downloads with game information");
            }

            return Ok(new
            {
                message = "Depot mapping update completed",
                totalProcessed = downloadsNeedingGameInfo.Count,
                updated = updated,
                notFound = notFound,
                steamKit2Ready = _steamKit2Service.IsReady,
                steamKit2DepotCount = _steamKit2Service.GetDepotMappingCount()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating depot mappings");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Import PICS depot mappings from JSON file to database
    /// </summary>
    [HttpPost("import-json-data")]
    public async Task<IActionResult> ImportJsonDataToDatabase()
    {
        try
        {
            _logger.LogInformation("Starting import of PICS JSON data to database");

            await _picsDataService.ImportJsonDataToDatabaseAsync();

            return Ok(new
            {
                message = "PICS JSON data imported to database successfully",
                timestamp = DateTime.UtcNow,
                picsJsonPath = _picsDataService.GetPicsJsonFilePath()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing PICS JSON data to database");
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
    /// Get detailed game information for a specific download
    /// </summary>
    [HttpGet("download/{downloadId}")]
    public async Task<IActionResult> GetDownloadGameInfo(int downloadId)
    {
        try
        {
            var download = await _context.Downloads
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == downloadId);

            if (download == null)
            {
                return NotFound(new { error = "Download not found" });
            }

            // For non-Steam services, return basic info
            if (download.Service.ToLower() != "steam")
            {
                return Ok(new GameDownloadDetails
                {
                    DownloadId = download.Id,
                    Service = download.Service,
                    GameName = $"{download.Service} Content",
                    TotalBytes = download.TotalBytes,
                    CacheHitBytes = download.CacheHitBytes,
                    CacheMissBytes = download.CacheMissBytes,
                    CacheHitPercent = download.CacheHitPercent,
                    StartTime = download.StartTime,
                    EndTime = download.EndTime,
                    ClientIp = download.ClientIp,
                    IsActive = download.IsActive
                });
            }

            // Try to get game info from download record first
            if (download.GameAppId.HasValue && !string.IsNullOrEmpty(download.GameName))
            {
                var cachedInfo = await _steamService.GetGameInfoAsync(download.GameAppId.Value);
                
                return Ok(new GameDownloadDetails
                {
                    DownloadId = download.Id,
                    Service = download.Service,
                    AppId = download.GameAppId,
                    GameName = cachedInfo?.Name ?? download.GameName,
                    GameType = cachedInfo?.Type,
                    HeaderImage = cachedInfo?.HeaderImage ?? download.GameImageUrl,
                    Description = cachedInfo?.Description,
                    TotalBytes = download.TotalBytes,
                    CacheHitBytes = download.CacheHitBytes,
                    CacheMissBytes = download.CacheMissBytes,
                    CacheHitPercent = download.CacheHitPercent,
                    StartTime = download.StartTime,
                    EndTime = download.EndTime,
                    ClientIp = download.ClientIp,
                    IsActive = download.IsActive
                });
            }

            // Try to extract app ID from URL if we have one
            if (!string.IsNullOrEmpty(download.LastUrl))
            {
                var appId = _steamService.ExtractAppIdFromUrl(download.LastUrl);
                if (appId.HasValue)
                {
                    var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                    
                    // Update the download record with game info
                    download.GameAppId = appId;
                    download.GameName = gameInfo?.Name;
                    download.GameImageUrl = gameInfo?.HeaderImage;
                    _context.Downloads.Update(download);
                    await _context.SaveChangesAsync();

                    return Ok(new GameDownloadDetails
                    {
                        DownloadId = download.Id,
                        Service = download.Service,
                        AppId = appId,
                        GameName = gameInfo?.Name ?? "Unknown Steam Game",
                        GameType = gameInfo?.Type,
                        HeaderImage = gameInfo?.HeaderImage,
                        Description = gameInfo?.Description,
                        TotalBytes = download.TotalBytes,
                        CacheHitBytes = download.CacheHitBytes,
                        CacheMissBytes = download.CacheMissBytes,
                        CacheHitPercent = download.CacheHitPercent,
                        StartTime = download.StartTime,
                        EndTime = download.EndTime,
                        ClientIp = download.ClientIp,
                        IsActive = download.IsActive
                    });
                }
            }

            // Return basic info if we can't determine the game
            return Ok(new GameDownloadDetails
            {
                DownloadId = download.Id,
                Service = download.Service,
                GameName = "Unknown Steam Game",
                TotalBytes = download.TotalBytes,
                CacheHitBytes = download.CacheHitBytes,
                CacheMissBytes = download.CacheMissBytes,
                CacheHitPercent = download.CacheHitPercent,
                StartTime = download.StartTime,
                EndTime = download.EndTime,
                ClientIp = download.ClientIp,
                IsActive = download.IsActive
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting game info for download {downloadId}");
            return StatusCode(500, new { error = "Failed to get game information" });
        }
    }





    /// <summary>
    /// Store a newly discovered depot mapping in the database
    /// </summary>
    private async Task StoreDiscoveredMappingAsync(uint depotId, uint appId, string appName, string source)
    {
        try
        {
            // Check if mapping already exists
            var existingMapping = await _context.SteamDepotMappings
                .FirstOrDefaultAsync(m => m.DepotId == depotId && m.AppId == appId);

            if (existingMapping == null)
            {
                var newMapping = new SteamDepotMapping
                {
                    DepotId = depotId,
                    AppId = appId,
                    AppName = appName,
                    Source = source,
                    Confidence = 75, // Medium confidence for pattern matching
                    DiscoveredAt = DateTime.UtcNow
                };

                _context.SteamDepotMappings.Add(newMapping);
                await _context.SaveChangesAsync();

                _logger.LogInformation($"Stored new depot mapping: {depotId} -> {appId} ({appName}) via {source}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to store discovered mapping: depot {depotId} -> app {appId}");
        }
    }

    /// <summary>
    /// Simple test endpoint
    /// </summary>
    [HttpGet("test")]
    public IActionResult Test()
    {
        return Ok(new { message = "GameInfoController is working", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Proxy Steam game header images to avoid CORS issues
    /// </summary>
    [HttpGet("gameimages/{appId}/header")]
    public async Task<IActionResult> GetGameHeaderImage(uint appId)
    {
        try
        {
            var imageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");

            var response = await httpClient.GetAsync(imageUrl);

            if (response.IsSuccessStatusCode)
            {
                var imageBytes = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

                // Add cache headers
                Response.Headers["Cache-Control"] = "public, max-age=3600"; // Cache for 1 hour

                return File(imageBytes, contentType);
            }
            else
            {
                _logger.LogWarning($"Failed to fetch Steam header image for app {appId}, status: {response.StatusCode}");
                return NotFound(new { error = $"Steam header image not found for app {appId}" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error proxying Steam header image for app {appId}");
            return StatusCode(500, new { error = "Failed to fetch game header image" });
        }
    }

    /// <summary>
    /// Download pre-created depot mappings from GitHub repo
    /// </summary>
    [HttpPost("download-precreated-data")]
    public async Task<IActionResult> DownloadPrecreatedDepotData()
    {
        try
        {
            _logger.LogInformation("Starting download of pre-created depot data from GitHub");

            const string githubUrl = "https://raw.githubusercontent.com/regix1/lancache-manager/main/Data/pics_depot_mappings.json";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 minute timeout for large file

            _logger.LogInformation($"Downloading from: {githubUrl}");

            var response = await httpClient.GetAsync(githubUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to download pre-created data: HTTP {response.StatusCode}");
                return BadRequest(new {
                    error = "Failed to download pre-created depot data from GitHub",
                    statusCode = response.StatusCode,
                    url = githubUrl
                });
            }

            var jsonContent = await response.Content.ReadAsStringAsync();

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
            await System.IO.File.WriteAllTextAsync(localPath, jsonContent);

            _logger.LogInformation($"Saved pre-created depot data to: {localPath}");

            // Import to database
            await _picsDataService.ImportJsonDataToDatabaseAsync();

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading pre-created depot data");
            return StatusCode(500, new { error = "Failed to download and import pre-created depot data", details = ex.Message });
        }
    }
}
