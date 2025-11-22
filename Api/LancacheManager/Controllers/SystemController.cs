using LancacheManager.Application.Services;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using LancacheManager.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for system-wide operations and configuration
/// Handles system config, state, setup status, and maintenance operations
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly StateRepository _stateService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SessionMigrationService _sessionMigrationService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly SteamKit2Service _steamKit2Service;

    public SystemController(
        StateRepository stateService,
        IConfiguration configuration,
        ILogger<SystemController> logger,
        IPathResolver pathResolver,
        SessionMigrationService sessionMigrationService,
        CacheClearingService cacheClearingService,
        SteamKit2Service steamKit2Service)
    {
        _stateService = stateService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _sessionMigrationService = sessionMigrationService;
        _cacheClearingService = cacheClearingService;
        _steamKit2Service = steamKit2Service;
    }

    /// <summary>
    /// GET /api/system/config - Get system configuration
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        try
        {
            var config = new
            {
                cachePath = _pathResolver.GetCacheDirectory(),
                logsPath = _pathResolver.GetLogsDirectory(),
                dataPath = _pathResolver.GetDataDirectory(),
                cacheDeleteMode = _cacheClearingService.GetDeleteMode(),
                steamAuthMode = _stateService.GetSteamAuthMode(),
                timeZone = _configuration.GetValue<string>("TimeZone", "UTC"),
                cacheWritable = _pathResolver.IsCacheDirectoryWritable(),
                logsWritable = _pathResolver.IsLogsDirectoryWritable()
            };

            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system config");
            return StatusCode(500, new { error = "Failed to get system config", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/system/state - Get application state
    /// </summary>
    [HttpGet("state")]
    public IActionResult GetState()
    {
        try
        {
            var state = new
            {
                setupCompleted = _stateService.GetSetupCompleted(),
                hasDataLoaded = _stateService.HasDataLoaded(),
                steamAuthMode = _stateService.GetSteamAuthMode(),
                cacheDeleteMode = _cacheClearingService.GetDeleteMode()
            };

            return Ok(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system state");
            return StatusCode(500, new { error = "Failed to get system state", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/system/permissions - Check directory permissions
    /// </summary>
    [HttpGet("permissions")]
    public IActionResult GetPermissions()
    {
        try
        {
            var cachePath = _pathResolver.GetCacheDirectory();
            var logPath = _pathResolver.GetLogsDirectory();

            var cacheWritable = _pathResolver.IsCacheDirectoryWritable();
            var logsWritable = _pathResolver.IsLogsDirectoryWritable();

            return Ok(new
            {
                cache = new
                {
                    path = cachePath,
                    writable = cacheWritable,
                    readOnly = !cacheWritable
                },
                logs = new
                {
                    path = logPath,
                    writable = logsWritable,
                    readOnly = !logsWritable
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking directory permissions");
            return StatusCode(500, new { error = "Failed to check directory permissions", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/system/setup - Get setup status
    /// </summary>
    [HttpGet("setup")]
    public IActionResult GetSetupStatus()
    {
        try
        {
            var isCompleted = _stateService.GetSetupCompleted();
            var hasProcessedLogs = _stateService.GetHasProcessedLogs();

            return Ok(new
            {
                isCompleted,
                hasProcessedLogs,
                setupCompleted = isCompleted // For backward compatibility
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting setup status");
            return StatusCode(500, new { error = "Failed to get setup status", details = ex.Message });
        }
    }

    /// <summary>
    /// PATCH /api/system/setup - Update setup status
    /// RESTful: PATCH is proper method for partial updates
    /// Request body: { "completed": true }
    /// </summary>
    [HttpPatch("setup")]
    [RequireAuth]
    public IActionResult UpdateSetupStatus([FromBody] UpdateSetupRequest request)
    {
        try
        {
            if (request.Completed.HasValue)
            {
                _stateService.SetSetupCompleted(request.Completed.Value);
                _logger.LogInformation("Setup status updated: {Completed}", request.Completed.Value);

                return Ok(new
                {
                    message = "Setup status updated",
                    setupCompleted = request.Completed.Value
                });
            }

            return BadRequest(new { error = "No update provided" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating setup status");
            return StatusCode(500, new { error = "Failed to update setup status", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/system/rsync/available - Check if rsync is available
    /// </summary>
    [HttpGet("rsync/available")]
    public IActionResult CheckRsyncAvailable()
    {
        try
        {
            var isAvailable = _cacheClearingService.IsRsyncAvailable();
            return Ok(new { available = isAvailable });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking rsync availability");
            return StatusCode(500, new { error = "Failed to check rsync availability", details = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/system/migrations/sessions - Run session migration
    /// Note: POST is acceptable as this is a one-time operation/action
    /// </summary>
    [HttpPost("migrations/sessions")]
    [RequireAuth]
    public async Task<IActionResult> MigrateSessions()
    {
        try
        {
            await _sessionMigrationService.MigrateOldSessionsToDatabase();
            _logger.LogInformation("Session migration completed successfully");

            return Ok(new { message = "Session migration completed successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during session migration");
            return StatusCode(500, new { error = "Session migration failed", details = ex.Message });
        }
    }

    /// <summary>
    /// PATCH /api/system/cache-delete-mode - Set cache clearing delete mode
    /// RESTful: PATCH is proper method for configuration updates
    /// Request body: { "deleteMode": "preserve" | "full" | "rsync" }
    /// </summary>
    [HttpPatch("cache-delete-mode")]
    [RequireAuth]
    public IActionResult SetCacheDeleteMode([FromBody] SetCacheDeleteModeRequest request)
    {
        try
        {
            _cacheClearingService.SetDeleteMode(request.DeleteMode);
            _logger.LogInformation("Cache delete mode updated to: {Mode}", request.DeleteMode);

            return Ok(new
            {
                message = "Cache delete mode updated",
                deleteMode = request.DeleteMode
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting cache delete mode");
            return StatusCode(500, new { error = "Failed to set cache delete mode", details = ex.Message });
        }
    }

    /// <summary>
    /// PATCH /api/system/depots/crawl-interval - Set depot crawl interval
    /// RESTful: PATCH is proper method for configuration updates
    /// Request body: { "intervalHours": 24 }
    /// </summary>
    [HttpPatch("depots/crawl-interval")]
    [RequireAuth]
    public IActionResult SetDepotCrawlInterval([FromBody] SetCrawlIntervalRequest request)
    {
        try
        {
            if (request.IntervalHours <= 0)
            {
                return BadRequest(new { error = "Interval must be greater than 0" });
            }

            _steamKit2Service.CrawlIntervalHours = request.IntervalHours;
            _logger.LogInformation("PICS crawl interval set to {Hours} hours", request.IntervalHours);

            return Ok(new
            {
                message = "Crawl interval updated",
                intervalHours = request.IntervalHours
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting crawl interval");
            return StatusCode(500, new { error = "Failed to set crawl interval", details = ex.Message });
        }
    }

    /// <summary>
    /// PATCH /api/system/depots/scan-mode - Set depot scan mode
    /// RESTful: PATCH is proper method for configuration updates
    /// Request body: { "mode": "full" } or { "mode": "incremental" }
    /// </summary>
    [HttpPatch("depots/scan-mode")]
    [RequireAuth]
    public IActionResult SetDepotScanMode([FromBody] SetScanModeRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Mode))
            {
                return BadRequest(new { error = "Scan mode is required" });
            }

            var validModes = new[] { "full", "incremental" };
            if (!validModes.Contains(request.Mode.ToLowerInvariant()))
            {
                return BadRequest(new { error = "Invalid scan mode. Must be 'full' or 'incremental'" });
            }

            _steamKit2Service.CrawlIncrementalMode = request.Mode.ToLowerInvariant() == "incremental";
            _logger.LogInformation("PICS scan mode set to: {Mode}", request.Mode);

            return Ok(new
            {
                message = "Scan mode updated",
                mode = request.Mode.ToLowerInvariant()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting scan mode");
            return StatusCode(500, new { error = "Failed to set scan mode", details = ex.Message });
        }
    }

    public class UpdateSetupRequest
    {
        public bool? Completed { get; set; }
    }

    public class SetCacheDeleteModeRequest
    {
        public string DeleteMode { get; set; } = string.Empty;
    }

    public class SetCrawlIntervalRequest
    {
        public int IntervalHours { get; set; }
    }

    public class SetScanModeRequest
    {
        public string Mode { get; set; } = string.Empty;
    }
}
