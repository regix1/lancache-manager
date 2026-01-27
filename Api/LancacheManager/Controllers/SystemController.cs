using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using LancacheManager.Core.Services.SteamKit2;


namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for system-wide operations and configuration
/// Handles system config, state, setup status, and maintenance operations
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly SessionMigrationService _sessionMigrationService;
    private readonly CacheClearingService _cacheClearingService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly DatasourceService _datasourceService;
    private readonly ISignalRNotificationService _notifications;
    private readonly NginxLogRotationHostedService _logRotationService;

    public SystemController(
        StateService stateService,
        IConfiguration configuration,
        ILogger<SystemController> logger,
        IPathResolver pathResolver,
        SessionMigrationService sessionMigrationService,
        CacheClearingService cacheClearingService,
        SteamKit2Service steamKit2Service,
        DatasourceService datasourceService,
        ISignalRNotificationService notifications,
        NginxLogRotationHostedService logRotationService)
    {
        _stateService = stateService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _sessionMigrationService = sessionMigrationService;
        _cacheClearingService = cacheClearingService;
        _steamKit2Service = steamKit2Service;
        _datasourceService = datasourceService;
        _notifications = notifications;
        _logRotationService = logRotationService;
    }

    /// <summary>
    /// GET /api/system/config - Get system configuration
    /// Note: Public endpoint - needed for app initialization before authentication
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
    [RequireGuestSession]
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
    [RequireGuestSession]
    public IActionResult GetPermissions()
    {
        var cachePath = _pathResolver.GetCacheDirectory();
        var logPath = _pathResolver.GetLogsDirectory();

        var cacheExists = Directory.Exists(cachePath);
        var logsExists = Directory.Exists(logPath);
        var cacheWritable = cacheExists && _pathResolver.IsCacheDirectoryWritable();
        var logsWritable = logsExists && _pathResolver.IsLogsDirectoryWritable();
        var dockerSocketAvailable = _pathResolver.IsDockerSocketAvailable();

        return Ok(new SystemPermissionsResponse
        {
            Cache = new DirectoryPermission
            {
                Path = cachePath,
                Exists = cacheExists,
                Writable = cacheWritable,
                ReadOnly = cacheExists && !cacheWritable
            },
            Logs = new DirectoryPermission
            {
                Path = logPath,
                Exists = logsExists,
                Writable = logsWritable,
                ReadOnly = logsExists && !logsWritable
            },
            DockerSocket = new DockerSocketPermission
            {
                Available = dockerSocketAvailable
            }
        });
    }

    /// <summary>
    /// GET /api/system/setup - Get setup status
    /// Note: Public endpoint - needed for AuthenticationModal before authentication
    /// </summary>
    [HttpGet("setup")]
    public IActionResult GetSetupStatus()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

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
    [RequireGuestSession]
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
    /// Note: Public endpoint - needed for RefreshRateContext before authentication
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
    [RequireAuth]
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
    [RequireGuestSession]
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
        if (request == null || string.IsNullOrWhiteSpace(request.RefreshRate))
        {
            return BadRequest(new ErrorResponse { Error = "Refresh rate is required" });
        }

        var normalizedRate = request.RefreshRate.Trim().ToUpperInvariant();
        var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
        if (!validRates.Contains(normalizedRate))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid refresh rate. Must be LIVE, ULTRA, REALTIME, STANDARD, RELAXED, or SLOW" });
        }

        _stateService.SetDefaultGuestRefreshRate(normalizedRate);
        _logger.LogInformation("Default guest refresh rate set to: {Rate}", normalizedRate);

        // Broadcast to all clients so guest users pick up the new default
        await _notifications.NotifyAllAsync(SignalREvents.DefaultGuestRefreshRateChanged, new
        {
            refreshRate = normalizedRate
        });

        return Ok(new RefreshRateResponse
        {
            Message = "Default guest refresh rate updated",
            RefreshRate = normalizedRate
        });
    }

    /// <summary>
    /// Get default guest preferences
    /// </summary>
    [HttpGet("default-guest-preferences")]
    [RequireGuestSession]
    public IActionResult GetDefaultGuestPreferences()
    {
        var state = _stateService.GetState();
        return Ok(new
        {
            useLocalTimezone = state.DefaultGuestUseLocalTimezone,
            use24HourFormat = state.DefaultGuestUse24HourFormat,
            sharpCorners = state.DefaultGuestSharpCorners,
            disableTooltips = state.DefaultGuestDisableTooltips,
            showDatasourceLabels = state.DefaultGuestShowDatasourceLabels,
            showYearInDates = state.DefaultGuestShowYearInDates,
            allowedTimeFormats = state.AllowedTimeFormats ?? new List<string> { "server-24h", "server-12h", "local-24h", "local-12h" }
        });
    }

    /// <summary>
    /// Update allowed time formats for guests
    /// </summary>
    [HttpPatch("default-guest-preferences/allowed-time-formats")]
    [RequireAuth]
    public async Task<IActionResult> SetAllowedTimeFormats([FromBody] SetAllowedTimeFormatsRequest request)
    {
        var validFormats = new[] { "server-24h", "server-12h", "local-24h", "local-12h" };

        if (request.Formats == null || request.Formats.Count == 0)
        {
            return BadRequest(new ErrorResponse { Error = "At least one time format must be allowed" });
        }

        // Validate all formats
        foreach (var format in request.Formats)
        {
            if (!validFormats.Contains(format))
            {
                return BadRequest(new ErrorResponse { Error = $"Invalid time format: {format}. Valid formats are: {string.Join(", ", validFormats)}" });
            }
        }

        _stateService.UpdateState(state =>
        {
            state.AllowedTimeFormats = request.Formats.Distinct().ToList();
        });

        _logger.LogInformation("Allowed time formats set to: {Formats}", string.Join(", ", request.Formats));

        // Broadcast to all clients
        await _notifications.NotifyAllAsync(SignalREvents.AllowedTimeFormatsChanged, new
        {
            formats = request.Formats
        });

        return Ok(new { message = "Allowed time formats updated", formats = request.Formats });
    }

    /// <summary>
    /// Update a single default guest preference
    /// </summary>
    [HttpPatch("default-guest-preferences/{key}")]
    [RequireAuth]
    public async Task<IActionResult> SetDefaultGuestPreference(string key, [FromBody] SetBoolPreferenceRequest request)
    {
        var validKeys = new[] { "useLocalTimezone", "use24HourFormat", "sharpCorners", "disableTooltips", "showDatasourceLabels", "showYearInDates" };
        if (!validKeys.Contains(key))
        {
            return BadRequest(new ErrorResponse { Error = $"Invalid preference key: {key}" });
        }

        _stateService.UpdateState(state =>
        {
            switch (key)
            {
                case "useLocalTimezone":
                    state.DefaultGuestUseLocalTimezone = request.Value;
                    break;
                case "use24HourFormat":
                    state.DefaultGuestUse24HourFormat = request.Value;
                    break;
                case "sharpCorners":
                    state.DefaultGuestSharpCorners = request.Value;
                    break;
                case "disableTooltips":
                    state.DefaultGuestDisableTooltips = request.Value;
                    break;
                case "showDatasourceLabels":
                    state.DefaultGuestShowDatasourceLabels = request.Value;
                    break;
                case "showYearInDates":
                    state.DefaultGuestShowYearInDates = request.Value;
                    break;
            }
        });

        _logger.LogInformation("Default guest preference {Key} set to: {Value}", key, request.Value);

        // Broadcast to all clients so other admins and guest users can update
        await _notifications.NotifyAllAsync(SignalREvents.DefaultGuestPreferencesChanged, new
        {
            key,
            value = request.Value
        });

        return Ok(new { message = $"Default guest preference {key} updated", key, value = request.Value });
    }

    /// <summary>
    /// GET /api/system/log-rotation/status - Get nginx log rotation status
    /// </summary>
    [HttpGet("log-rotation/status")]
    [RequireGuestSession]
    public IActionResult GetLogRotationStatus()
    {
        var status = _logRotationService.GetStatus();
        return Ok(status);
    }

    /// <summary>
    /// POST /api/system/log-rotation/trigger - Force nginx log rotation
    /// </summary>
    [HttpPost("log-rotation/trigger")]
    [RequireAuth]
    public async Task<IActionResult> TriggerLogRotation()
    {
        _logger.LogInformation("Manual log rotation triggered via API");
        var success = await _logRotationService.ForceRotationAsync();

        if (success)
        {
            return Ok(new { success = true, message = "Log rotation completed successfully" });
        }
        else
        {
            return Ok(new { success = false, message = "Log rotation failed. Check server logs for details." });
        }
    }

    /// <summary>
    /// PUT /api/system/log-rotation/schedule - Update log rotation schedule
    /// </summary>
    [HttpPut("log-rotation/schedule")]
    [RequireAuth]
    public async Task<IActionResult> UpdateLogRotationSchedule([FromBody] UpdateLogRotationScheduleRequest request)
    {
        if (request.ScheduleHours < 0 || request.ScheduleHours > 168)
        {
            return BadRequest(new { success = false, message = "Schedule hours must be between 0 (disabled) and 168 (1 week)" });
        }

        _logger.LogInformation("Log rotation schedule update requested: {Hours} hours", request.ScheduleHours);
        var success = await _logRotationService.UpdateScheduleAsync(request.ScheduleHours);

        if (success)
        {
            var status = _logRotationService.GetStatus();
            return Ok(new { success = true, message = "Log rotation schedule updated", status });
        }
        else
        {
            return BadRequest(new { success = false, message = "Failed to update schedule" });
        }
    }

    public class UpdateLogRotationScheduleRequest
    {
        public int ScheduleHours { get; set; }
    }

    public class SetAllowedTimeFormatsRequest
    {
        public List<string> Formats { get; set; } = new();
    }

    public class SetBoolPreferenceRequest
    {
        public bool Value { get; set; }
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
