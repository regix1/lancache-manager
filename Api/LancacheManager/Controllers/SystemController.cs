using LancacheManager.Application.DTOs;
using LancacheManager.Application.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

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
    private readonly DatasourceService _datasourceService;
    private readonly IHubContext<DownloadHub> _hubContext;

    public SystemController(
        StateRepository stateService,
        IConfiguration configuration,
        ILogger<SystemController> logger,
        IPathResolver pathResolver,
        SessionMigrationService sessionMigrationService,
        CacheClearingService cacheClearingService,
        SteamKit2Service steamKit2Service,
        DatasourceService datasourceService,
        IHubContext<DownloadHub> hubContext)
    {
        _stateService = stateService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _sessionMigrationService = sessionMigrationService;
        _cacheClearingService = cacheClearingService;
        _steamKit2Service = steamKit2Service;
        _datasourceService = datasourceService;
        _hubContext = hubContext;
    }

    /// <summary>
    /// GET /api/system/config - Get system configuration
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var datasources = _datasourceService.GetDatasources();
        var defaultDatasource = _datasourceService.GetDefaultDatasource();

        return Ok(new SystemConfigResponse
        {
            // Use first datasource paths for backward compatibility, or fall back to PathResolver
            CachePath = defaultDatasource?.CachePath ?? _pathResolver.GetCacheDirectory(),
            LogsPath = defaultDatasource?.LogPath ?? _pathResolver.GetLogsDirectory(),
            DataPath = _pathResolver.GetDataDirectory(),
            CacheDeleteMode = _cacheClearingService.GetDeleteMode(),
            SteamAuthMode = _stateService.GetSteamAuthMode() ?? string.Empty,
            // Check TZ environment variable first (Docker standard), then TimeZone config, default to UTC
            TimeZone = _configuration.GetValue<string>("TZ")
                      ?? _configuration.GetValue<string>("TimeZone")
                      ?? "UTC",
            CacheWritable = defaultDatasource != null
                ? _pathResolver.IsDirectoryWritable(defaultDatasource.CachePath)
                : _pathResolver.IsCacheDirectoryWritable(),
            LogsWritable = defaultDatasource != null
                ? _pathResolver.IsDirectoryWritable(defaultDatasource.LogPath)
                : _pathResolver.IsLogsDirectoryWritable(),
            // Include all datasources
            DataSources = datasources.Select(ds => new DatasourceInfoDto
            {
                Name = ds.Name,
                CachePath = ds.CachePath,
                LogsPath = ds.LogPath,
                CacheWritable = _pathResolver.IsDirectoryWritable(ds.CachePath),
                LogsWritable = _pathResolver.IsDirectoryWritable(ds.LogPath),
                Enabled = ds.Enabled
            }).ToList()
        });
    }

    /// <summary>
    /// GET /api/system/state - Get application state
    /// </summary>
    [HttpGet("state")]
    public IActionResult GetState()
    {
        return Ok(new SystemStateResponse
        {
            SetupCompleted = _stateService.GetSetupCompleted(),
            HasDataLoaded = _stateService.HasDataLoaded(),
            SteamAuthMode = _stateService.GetSteamAuthMode() ?? string.Empty,
            CacheDeleteMode = _cacheClearingService.GetDeleteMode()
        });
    }

    /// <summary>
    /// GET /api/system/permissions - Check directory permissions and docker socket availability
    /// </summary>
    [HttpGet("permissions")]
    public IActionResult GetPermissions()
    {
        var cachePath = _pathResolver.GetCacheDirectory();
        var logPath = _pathResolver.GetLogsDirectory();

        var cacheWritable = _pathResolver.IsCacheDirectoryWritable();
        var logsWritable = _pathResolver.IsLogsDirectoryWritable();
        var dockerSocketAvailable = _pathResolver.IsDockerSocketAvailable();

        return Ok(new SystemPermissionsResponse
        {
            Cache = new DirectoryPermission
            {
                Path = cachePath,
                Writable = cacheWritable,
                ReadOnly = !cacheWritable
            },
            Logs = new DirectoryPermission
            {
                Path = logPath,
                Writable = logsWritable,
                ReadOnly = !logsWritable
            },
            DockerSocket = new DockerSocketPermission
            {
                Available = dockerSocketAvailable
            }
        });
    }

    /// <summary>
    /// GET /api/system/setup - Get setup status
    /// </summary>
    [HttpGet("setup")]
    public IActionResult GetSetupStatus()
    {
        var isCompleted = _stateService.GetSetupCompleted();
        var hasProcessedLogs = _stateService.GetHasProcessedLogs();

        return Ok(new SetupStatusResponse
        {
            IsCompleted = isCompleted,
            HasProcessedLogs = hasProcessedLogs,
            SetupCompleted = isCompleted // For backward compatibility
        });
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
        if (request.Completed.HasValue)
        {
            _stateService.SetSetupCompleted(request.Completed.Value);
            _logger.LogInformation("Setup status updated: {Completed}", request.Completed.Value);

            return Ok(new SetupUpdateResponse
            {
                Message = "Setup status updated",
                SetupCompleted = request.Completed.Value
            });
        }

        return BadRequest(new ErrorResponse { Error = "No update provided" });
    }

    /// <summary>
    /// GET /api/system/rsync/available - Check if rsync is available
    /// </summary>
    [HttpGet("rsync/available")]
    public IActionResult CheckRsyncAvailable()
    {
        var isAvailable = _cacheClearingService.IsRsyncAvailable();
        return Ok(new RsyncAvailableResponse { Available = isAvailable });
    }

    /// <summary>
    /// POST /api/system/migrations/sessions - Run session migration
    /// Note: POST is acceptable as this is a one-time operation/action
    /// </summary>
    [HttpPost("migrations/sessions")]
    [RequireAuth]
    public async Task<IActionResult> MigrateSessions()
    {
        await _sessionMigrationService.MigrateOldSessionsToDatabase();
        _logger.LogInformation("Session migration completed successfully");

        return Ok(MessageResponse.Ok("Session migration completed successfully"));
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
        _cacheClearingService.SetDeleteMode(request.DeleteMode);
        _logger.LogInformation("Cache delete mode updated to: {Mode}", request.DeleteMode);

        return Ok(new CacheDeleteModeResponse
        {
            Message = "Cache delete mode updated",
            DeleteMode = request.DeleteMode
        });
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
        if (request.IntervalHours <= 0)
        {
            return BadRequest(new ErrorResponse { Error = "Interval must be greater than 0" });
        }

        _steamKit2Service.CrawlIntervalHours = request.IntervalHours;
        _logger.LogInformation("PICS crawl interval set to {Hours} hours", request.IntervalHours);

        return Ok(new CrawlIntervalResponse
        {
            Message = "Crawl interval updated",
            IntervalHours = request.IntervalHours
        });
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
        if (string.IsNullOrWhiteSpace(request.Mode))
        {
            return BadRequest(new ErrorResponse { Error = "Scan mode is required" });
        }

        var validModes = new[] { "full", "incremental" };
        if (!validModes.Contains(request.Mode.ToLowerInvariant()))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid scan mode. Must be 'full' or 'incremental'" });
        }

        _steamKit2Service.CrawlIncrementalMode = request.Mode.ToLowerInvariant() == "incremental";
        _logger.LogInformation("PICS scan mode set to: {Mode}", request.Mode);

        return Ok(new ScanModeResponse
        {
            Message = "Scan mode updated",
            Mode = request.Mode.ToLowerInvariant()
        });
    }

    /// <summary>
    /// GET /api/system/refresh-rate - Get the current refresh rate setting
    /// </summary>
    [HttpGet("refresh-rate")]
    public IActionResult GetRefreshRate()
    {
        var rate = _stateService.GetRefreshRate();
        return Ok(new RefreshRateResponse { RefreshRate = rate });
    }

    /// <summary>
    /// PATCH /api/system/refresh-rate - Set the refresh rate
    /// RESTful: PATCH is proper method for configuration updates
    /// Request body: { "refreshRate": "LIVE" | "ULTRA" | "REALTIME" | "STANDARD" | "RELAXED" | "SLOW" }
    /// </summary>
    [HttpPatch("refresh-rate")]
    public IActionResult SetRefreshRate([FromBody] SetRefreshRateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshRate))
        {
            return BadRequest(new ErrorResponse { Error = "Refresh rate is required" });
        }

        var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
        if (!validRates.Contains(request.RefreshRate.ToUpperInvariant()))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid refresh rate. Must be LIVE, ULTRA, REALTIME, STANDARD, RELAXED, or SLOW" });
        }

        _stateService.SetRefreshRate(request.RefreshRate);
        _logger.LogInformation("Refresh rate set to: {Rate}", request.RefreshRate.ToUpperInvariant());

        return Ok(new RefreshRateResponse
        {
            Message = "Refresh rate updated",
            RefreshRate = request.RefreshRate.ToUpperInvariant()
        });
    }

    /// <summary>
    /// GET /api/system/default-guest-refresh-rate - Get the default refresh rate for guest users
    /// </summary>
    [HttpGet("default-guest-refresh-rate")]
    public IActionResult GetDefaultGuestRefreshRate()
    {
        var rate = _stateService.GetDefaultGuestRefreshRate();
        return Ok(new RefreshRateResponse { RefreshRate = rate });
    }

    /// <summary>
    /// PATCH /api/system/default-guest-refresh-rate - Set the default refresh rate for guest users
    /// RESTful: PATCH is proper method for configuration updates
    /// Request body: { "refreshRate": "LIVE" | "ULTRA" | "REALTIME" | "STANDARD" | "RELAXED" | "SLOW" }
    /// </summary>
    [HttpPatch("default-guest-refresh-rate")]
    [RequireAuth]
    public async Task<IActionResult> SetDefaultGuestRefreshRate([FromBody] SetRefreshRateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshRate))
        {
            return BadRequest(new ErrorResponse { Error = "Refresh rate is required" });
        }

        var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
        if (!validRates.Contains(request.RefreshRate.ToUpperInvariant()))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid refresh rate. Must be LIVE, ULTRA, REALTIME, STANDARD, RELAXED, or SLOW" });
        }

        _stateService.SetDefaultGuestRefreshRate(request.RefreshRate);
        _logger.LogInformation("Default guest refresh rate set to: {Rate}", request.RefreshRate.ToUpperInvariant());

        // Broadcast to all clients so guest users pick up the new default
        await _hubContext.Clients.All.SendAsync("DefaultGuestRefreshRateChanged", new
        {
            refreshRate = request.RefreshRate.ToUpperInvariant()
        });

        return Ok(new RefreshRateResponse
        {
            Message = "Default guest refresh rate updated",
            RefreshRate = request.RefreshRate.ToUpperInvariant()
        });
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

    public class SetRefreshRateRequest
    {
        public string RefreshRate { get; set; } = string.Empty;
    }

    public class RefreshRateResponse
    {
        public string? Message { get; set; }
        public string RefreshRate { get; set; } = string.Empty;
    }
}
