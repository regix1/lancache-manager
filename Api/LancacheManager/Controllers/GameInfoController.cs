using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading;
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

            // Only use pattern matching if SteamKit2 is not ready or no PICS crawl has completed
            // Pattern matching can return incorrect results (e.g., depot 377239 -> app 377230 instead of 359550)
            if (!appId.HasValue && !_steamKit2Service.IsReady)
            {
                appId = await TryDepotPatternMatchingAsync(id);
                source = "PatternMatching";
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
    [HttpPost("steamkit/rebuild")]
    public IActionResult TriggerSteamKitRebuild(CancellationToken cancellationToken)
    {
        try
        {
            var started = _steamKit2Service.TryStartRebuild(cancellationToken);

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

                    // Only use pattern matching if SteamKit2 is not ready (avoid incorrect mappings)
                    if (!appId.HasValue && !_steamKit2Service.IsReady)
                    {
                        appId = await TryDepotPatternMatchingAsync(download.DepotId.Value);
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
    /// Try to resolve depot ID to app ID using pattern matching
    /// Steam depot IDs often follow patterns where depot 275851 maps to app 275850
    /// </summary>
    private async Task<uint?> TryDepotPatternMatchingAsync(uint depotId)
    {
        try
        {
            var candidateAppIds = new List<uint>();

            // Pattern 1: Replace last digit with 0 (275851 -> 275850)
            var basePattern = (depotId / 10) * 10;
            candidateAppIds.Add(basePattern);

            // Pattern 2: Try decrementing the depot ID by 1-20 (covers more cases)
            for (uint offset = 1; offset <= 20; offset++)
            {
                if (depotId > offset)
                {
                    candidateAppIds.Add(depotId - offset);
                }
            }

            // Pattern 3: Try the depot ID itself as an app ID
            candidateAppIds.Add(depotId);

            // Test each candidate app ID with Steam API and collect all valid matches
            var validMatches = new List<(uint appId, LancacheManager.Services.SteamService.GameInfo gameInfo)>();

            foreach (var candidateAppId in candidateAppIds)
            {
                try
                {
                    var gameInfo = await _steamService.GetGameInfoAsync(candidateAppId);
                    if (gameInfo != null && !string.IsNullOrEmpty(gameInfo.Name))
                    {
                        validMatches.Add((candidateAppId, gameInfo));
                        _logger.LogDebug($"Pattern matching found: depot {depotId} -> app {candidateAppId} ({gameInfo.Name})");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogTrace($"Pattern matching failed for depot {depotId} -> app {candidateAppId}: {ex.Message}");
                }

                // Add small delay to avoid overwhelming Steam API
                await Task.Delay(50);
            }

            // Prioritize matches: prefer main games over trials/demos/DLC
            if (validMatches.Any())
            {
                var bestMatch = validMatches
                    .OrderBy(match => GetGameTypePriority(match.gameInfo))
                    .ThenBy(match => match.appId) // Fallback to lower app ID
                    .First();

                _logger.LogInformation($"Pattern matching success: depot {depotId} -> app {bestMatch.appId} ({bestMatch.gameInfo.Name}) [selected from {validMatches.Count} matches]");

                // Store the best discovered mapping
                await StoreDiscoveredMappingAsync(depotId, bestMatch.appId, bestMatch.gameInfo.Name, "PatternMatching");

                return bestMatch.appId;
            }

            _logger.LogDebug($"Pattern matching failed for depot {depotId} - no valid app IDs found");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error in pattern matching for depot {depotId}");
            return null;
        }
    }

    /// <summary>
    /// Get priority for game type selection (lower = better)
    /// Based on Steam's depot mounting order principles
    /// </summary>
    private static int GetGameTypePriority(LancacheManager.Services.SteamService.GameInfo gameInfo)
    {
        var name = gameInfo.Name?.ToLower() ?? "";
        var type = gameInfo.Type?.ToLower() ?? "";

        // Highest priority: Base/Main games (Steam's base app principle)
        if (type == "game" && !IsTrialOrDemo(name) && !IsDlcContent(name))
        {
            return 1;
        }

        // Medium-high priority: Non-trial DLC content
        if ((type == "dlc" || type == "content") && !IsTrialOrDemo(name))
        {
            return 2;
        }

        // Low priority: Trial DLC and demo content (per Steam DLC research)
        if (IsTrialOrDemo(name) || type == "demo" || type == "beta")
        {
            return 3;
        }

        // Very low priority: Tools, servers, etc.
        if (type == "tool" || type == "server" || name.Contains("dedicated server"))
        {
            return 4;
        }

        // Default priority for unknown types
        return 2;
    }

    /// <summary>
    /// Check if a game name indicates it's DLC content
    /// </summary>
    private static bool IsDlcContent(string name)
    {
        var lowerName = name.ToLower();
        return lowerName.Contains("dlc") ||
               lowerName.Contains("expansion") ||
               lowerName.Contains("season pass") ||
               lowerName.Contains("add-on") ||
               lowerName.Contains("downloadable content");
    }

    /// <summary>
    /// Check if a game name indicates it's a trial, demo, or beta
    /// </summary>
    private static bool IsTrialOrDemo(string name)
    {
        var lowerName = name.ToLower();
        return lowerName.Contains("trial") ||
               lowerName.Contains("demo") ||
               lowerName.Contains("beta") ||
               lowerName.Contains("free weekend") ||
               lowerName.Contains("test") ||
               lowerName.Contains("preview") ||
               lowerName.Contains("alpha");
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
}
