using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LancacheManager.Core.Services.SteamKit2;
using System.Text.Json;


namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for system-wide operations and configuration
/// Handles system config, state, setup status, and maintenance operations
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize]
public class SystemController : ControllerBase
{
    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SystemController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly CacheClearingService _cacheClearingService;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly DatasourceService _datasourceService;
    private readonly ISignalRNotificationService _notifications;
    private readonly UserPreferencesService _userPreferencesService;

    public SystemController(
        StateService stateService,
        IConfiguration configuration,
        ILogger<SystemController> logger,
        IPathResolver pathResolver,
        CacheClearingService cacheClearingService,
        SteamKit2Service steamKit2Service,
        DatasourceService datasourceService,
        ISignalRNotificationService notifications,
        UserPreferencesService userPreferencesService)
    {
        _stateService = stateService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _cacheClearingService = cacheClearingService;
        _steamKit2Service = steamKit2Service;
        _datasourceService = datasourceService;
        _notifications = notifications;
        _userPreferencesService = userPreferencesService;
    }

    /// <summary>
    /// GET /api/system/config - Get system configuration
    /// Note: Public endpoint - needed for app initialization before authentication
    /// </summary>
    [AllowAnonymous]
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
    [AllowAnonymous]
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
    [Authorize]
    [HttpGet("permissions")]
    public IActionResult GetPermissions()
    {
        var defaultDatasource = _datasourceService.GetDefaultDatasource();
        var cachePath = defaultDatasource?.CachePath ?? _pathResolver.GetCacheDirectory();
        var logPath = defaultDatasource?.LogPath ?? _pathResolver.GetLogsDirectory();

        var cacheExists = Directory.Exists(cachePath);
        var logsExists = Directory.Exists(logPath);
        var cacheWritable = cacheExists && _pathResolver.IsDirectoryWritable(cachePath);
        var logsWritable = logsExists && _pathResolver.IsDirectoryWritable(logPath);
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
    [AllowAnonymous]
    [HttpGet("setup")]
    public IActionResult GetSetupStatus()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var state = _stateService.GetState();
        var isCompleted = state.SetupCompleted;
        var hasProcessedLogs = state.HasProcessedLogs;
        // Check if actual PostgreSQL credentials exist (env var or config file).
        // Migrated users have SetupCompleted=true from the SQLite era but no credentials file yet.
        var hasEnvPassword = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"));
        var hasCredentialsFile = System.IO.File.Exists(_pathResolver.GetPostgresCredentialsPath());
        var needsPostgresCredentials = !hasEnvPassword && !hasCredentialsFile;

        return Ok(new SetupStatusResponse
        {
            IsCompleted = isCompleted,
            HasProcessedLogs = hasProcessedLogs,
            SetupCompleted = isCompleted, // For backward compatibility
            NeedsPostgresCredentials = needsPostgresCredentials,
            CurrentSetupStep = state.CurrentSetupStep,
            DataSourceChoice = state.DataSourceChoice,
            CompletedPlatforms = state.CompletedPlatforms
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("gc-management/status")]
    public IActionResult GetGcManagementStatus()
    {
        var isEnabled = _configuration.GetValue<bool>(
            "Optimizations:EnableGarbageCollectionManagement",
            false);

        return Ok(new
        {
            enabled = isEnabled
        });
    }

    /// <summary>
    /// PATCH /api/system/setup - Update setup status
    /// RESTful: PATCH is proper method for partial updates
    /// Request body: { "completed": true }
    /// </summary>
    [Authorize]
    [HttpPatch("setup")]
    public IActionResult UpdateSetupStatus([FromBody] JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new ErrorResponse { Error = "Invalid setup update request body" });
        }

        var validSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "database-setup",
            "permissions-check",
            "import-historical-data",
            "platform-setup",
            "steam-api-key",
            "steam-auth",
            "depot-init",
            "pics-progress",
            "epic-auth",
            "log-processing",
            "depot-mapping"
        };
        var validChoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "github",
            "steam",
            "epic",
            "skip"
        };

        var hasUpdate = false;

        if (!TryReadOptionalBoolean(request, "completed", out var hasCompleted, out var completed, out var completedError))
        {
            return BadRequest(new ErrorResponse { Error = completedError ?? "Invalid completed value" });
        }

        if (!TryReadOptionalString(request, "currentSetupStep", out var hasCurrentSetupStep, out var currentSetupStep, out var currentStepError))
        {
            return BadRequest(new ErrorResponse { Error = currentStepError ?? "Invalid currentSetupStep value" });
        }

        if (!TryReadOptionalString(request, "dataSourceChoice", out var hasDataSourceChoice, out var dataSourceChoice, out var dataSourceError))
        {
            return BadRequest(new ErrorResponse { Error = dataSourceError ?? "Invalid dataSourceChoice value" });
        }

        if (!TryReadOptionalString(request, "completedPlatforms", out var hasCompletedPlatforms, out var completedPlatforms, out var completedPlatformsError))
        {
            return BadRequest(new ErrorResponse { Error = completedPlatformsError ?? "Invalid completedPlatforms value" });
        }

        if (currentSetupStep != null && !validSteps.Contains(currentSetupStep))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid currentSetupStep value" });
        }

        if (dataSourceChoice != null && !validChoices.Contains(dataSourceChoice))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid dataSourceChoice value" });
        }

        _stateService.UpdateState(state =>
        {
            // Persist wizard state fields if provided
            if (hasCurrentSetupStep)
            {
                state.CurrentSetupStep = currentSetupStep;
                hasUpdate = true;
            }

            if (hasDataSourceChoice)
            {
                state.DataSourceChoice = dataSourceChoice;
                hasUpdate = true;
            }

            if (hasCompletedPlatforms)
            {
                state.CompletedPlatforms = completedPlatforms;
                hasUpdate = true;
            }

            if (hasCompleted)
            {
                // Always clear wizard state fields when completion is explicitly set.
                state.CurrentSetupStep = null;
                state.DataSourceChoice = null;
                state.CompletedPlatforms = null;
                hasUpdate = true;
            }
        });

        // Call SetSetupCompleted AFTER UpdateState so the TaskCompletionSource signal
        // fires and unblocks all gated startup services immediately (no restart needed).
        if (hasCompleted)
        {
            _stateService.SetSetupCompleted(completed!.Value);
        }

        if (!_stateService.IsPersistenceAvailable)
        {
            return StatusCode(503, new ErrorResponse
            {
                Error = "Setup state could not be persisted. Please check server logs and try again."
            });
        }

        if (hasCompleted)
        {
            _logger.LogInformation("Setup status updated: {Completed}", completed!.Value);
            return Ok(new SetupUpdateResponse { Message = "Setup status updated", SetupCompleted = completed.Value });
        }

        if (hasUpdate)
        {
            return Ok(new SetupUpdateResponse
            {
                Message = "Setup status updated",
                SetupCompleted = _stateService.GetSetupCompleted()
            });
        }

        return BadRequest(new ErrorResponse { Error = "No update provided" });
    }

    private static bool TryReadOptionalBoolean(
        JsonElement request,
        string propertyName,
        out bool hasProperty,
        out bool? parsedValue,
        out string? error)
    {
        hasProperty = false;
        parsedValue = null;
        error = null;

        if (!request.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        hasProperty = true;

        if (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
        {
            parsedValue = property.GetBoolean();
            return true;
        }

        error = "Expected a boolean value";
        return false;
    }

    private static bool TryReadOptionalString(
        JsonElement request,
        string propertyName,
        out bool hasProperty,
        out string? parsedValue,
        out string? error)
    {
        hasProperty = false;
        parsedValue = null;
        error = null;

        if (!request.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        hasProperty = true;

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = "Expected a string or null value";
            return false;
        }

        parsedValue = property.GetString();
        return true;
    }

    /// <summary>
    /// GET /api/system/rsync/available - Check if rsync is available
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
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
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("migrations/sessions")]
    public IActionResult MigrateSessions()
    {
        return Ok(MessageResponse.Ok("Session migration completed successfully"));
    }

    /// <summary>
    /// PATCH /api/system/cache-delete-mode - Set cache clearing delete mode
    /// RESTful: PATCH is proper method for configuration updates
    /// Request body: { "deleteMode": "preserve" | "full" | "rsync" }
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("cache-delete-mode")]
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
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("depots/crawl-interval")]
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
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("depots/scan-mode")]
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
    [AllowAnonymous]
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
    [Authorize(Policy = "AdminOnly")]
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
    [Authorize]
    [HttpGet("default-guest-refresh-rate")]
    public IActionResult GetDefaultGuestRefreshRate()
    {
        var rate = _stateService.GetDefaultGuestRefreshRate();
        var locked = _stateService.GetGuestRefreshRateLocked();
        return Ok(new { refreshRate = rate, locked });
    }

    /// <summary>
    /// PATCH /api/system/default-guest-refresh-rate - Set the default refresh rate for guest users
    /// RESTful: PATCH is proper method for configuration updates
    /// Request body: { "refreshRate": "LIVE" | "ULTRA" | "REALTIME" | "STANDARD" | "RELAXED" | "SLOW" }
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("default-guest-refresh-rate")]
    public async Task<IActionResult> SetDefaultGuestRefreshRateAsync([FromBody] SetRefreshRateRequest request)
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
    /// PATCH /api/system/guest-refresh-rate-lock - Lock or unlock guest refresh rate selection
    /// Request body: { "locked": true | false }
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("guest-refresh-rate-lock")]
    public async Task<IActionResult> SetGuestRefreshRateLockAsync([FromBody] GuestRefreshRateLockRequest request)
    {
        if (request == null)
        {
            return BadRequest(new ErrorResponse { Error = "Request body is required" });
        }

        _stateService.SetGuestRefreshRateLocked(request.Locked);
        _logger.LogInformation("Guest refresh rate lock set to: {Locked}", request.Locked);

        await _notifications.NotifyAllAsync(SignalREvents.GuestRefreshRateLockChanged, new
        {
            locked = request.Locked
        });

        return Ok(new { success = true, locked = request.Locked });
    }

    /// <summary>
    /// Get default guest preferences
    /// </summary>
    [Authorize]
    [HttpGet("default-guest-preferences")]
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
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("default-guest-preferences/allowed-time-formats")]
    public async Task<IActionResult> SetAllowedTimeFormatsAsync([FromBody] SetAllowedTimeFormatsRequest request)
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
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("default-guest-preferences/{key}")]
    public async Task<IActionResult> SetDefaultGuestPreferenceAsync(string key, [FromBody] SetBoolPreferenceRequest request)
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
    /// Get default prefill panel settings (session-aware for thread limits)
    /// </summary>
    [Authorize]
    [HttpGet("prefill-defaults")]
    public IActionResult GetPrefillDefaults()
    {
        var steamMaxThreadLimit = ResolveEffectiveSteamThreadLimit();
        var epicMaxThreadLimit = ResolveEffectiveEpicThreadLimit();
        var maxConcurrency = ClampConcurrencyToLimit(
            _stateService.GetDefaultPrefillMaxConcurrency(), steamMaxThreadLimit);
        var epicMaxConcurrency = ClampConcurrencyToLimit(
            _stateService.GetEpicDefaultPrefillMaxConcurrency(), epicMaxThreadLimit);

        return Ok(new
        {
            operatingSystems = _stateService.GetDefaultPrefillOperatingSystems(),
            maxConcurrency,
            serverThreadCount = 256,
            maxThreadLimit = steamMaxThreadLimit,
            epicDefaultPrefillMaxConcurrency = epicMaxConcurrency
        });
    }

    /// <summary>
    /// Update default prefill panel settings
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPatch("prefill-defaults")]
    public async Task<IActionResult> SetPrefillDefaultsAsync([FromBody] SetPrefillDefaultsRequest request)
    {
        if (request.OperatingSystems != null)
        {
            _stateService.SetDefaultPrefillOperatingSystems(request.OperatingSystems);
        }
        if (request.MaxConcurrency != null)
        {
            _stateService.SetDefaultPrefillMaxConcurrency(request.MaxConcurrency);
        }
        if (request.EpicDefaultPrefillMaxConcurrency != null)
        {
            _stateService.SetEpicDefaultPrefillMaxConcurrency(request.EpicDefaultPrefillMaxConcurrency);
        }

        var steamMaxThreadLimit = ResolveEffectiveSteamThreadLimit();
        var epicMaxThreadLimit = ResolveEffectiveEpicThreadLimit();

        await _notifications.NotifyAllAsync(SignalREvents.PrefillDefaultsChanged, new
        {
            operatingSystems = _stateService.GetDefaultPrefillOperatingSystems(),
            maxConcurrency = _stateService.GetDefaultPrefillMaxConcurrency(),
            serverThreadCount = 256,
            maxThreadLimit = steamMaxThreadLimit,
            epicDefaultPrefillMaxConcurrency = _stateService.GetEpicDefaultPrefillMaxConcurrency()
        });

        return Ok(new
        {
            operatingSystems = _stateService.GetDefaultPrefillOperatingSystems(),
            maxConcurrency = _stateService.GetDefaultPrefillMaxConcurrency(),
            serverThreadCount = 256,
            maxThreadLimit = steamMaxThreadLimit,
            epicDefaultPrefillMaxConcurrency = _stateService.GetEpicDefaultPrefillMaxConcurrency()
        });
    }

    private UserSession? GetSession() => HttpContext.GetUserSession();

    private int? ResolveEffectiveSteamThreadLimit()
    {
        var session = GetSession();
        if (session == null) return null;
        if (session.SessionType == "admin") return null;

        // Guest: check per-user override first, then system default
        var prefs = _userPreferencesService.GetPreferences(session.Id);
        return prefs?.SteamMaxThreadCount ?? _stateService.GetDefaultGuestMaxThreadCount();
    }

    private int? ResolveEffectiveEpicThreadLimit()
    {
        var session = GetSession();
        if (session == null) return null;
        if (session.SessionType == "admin") return null;

        // Guest: check per-user override first, then system default
        var prefs = _userPreferencesService.GetPreferences(session.Id);
        return prefs?.EpicMaxThreadCount ?? _stateService.GetEpicDefaultGuestMaxThreadCount();
    }

    /// <summary>
    /// Clamp the default concurrency value so it does not exceed the guest thread limit.
    /// "auto" passes through unchanged; numeric values are capped.
    /// </summary>
    private static string ClampConcurrencyToLimit(string concurrency, int? maxThreadLimit)
    {
        if (!maxThreadLimit.HasValue) return concurrency;

        var limit = maxThreadLimit.Value;

        if (int.TryParse(concurrency, out var numeric) && numeric > limit)
            return limit.ToString();

        return concurrency;
    }

}
