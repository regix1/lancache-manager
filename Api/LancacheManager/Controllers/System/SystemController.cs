using LancacheManager.Models;
using LancacheManager.Models.ApiRequests;
using LancacheManager.Configuration;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Security;


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
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly CacheManagementService _cacheManagementService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AccountClaimWindow _claimWindow;

    // Names the clock change inside DefaultGuestPreferencesChanged, alongside the single-field keys the
    // same event has always carried. A listener reads this to know the payload holds a whole clock and
    // the one it replaced rather than one field's new value.
    private const string DefaultGuestClockKey = "clock";

    public SystemController(
        StateService stateService,
        IConfiguration configuration,
        ILogger<SystemController> logger,
        IPathResolver pathResolver,
        CacheClearingService cacheClearingService,
        SteamKit2Service steamKit2Service,
        DatasourceService datasourceService,
        ISignalRNotificationService notifications,
        UserPreferencesService userPreferencesService,
        DatasourceCapabilityService capabilityService,
        NginxLogRotationService nginxLogRotationService,
        CacheManagementService cacheManagementService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        AccountClaimWindow claimWindow)
    {
        _capabilityService = capabilityService;
        _stateService = stateService;
        _configuration = configuration;
        _logger = logger;
        _pathResolver = pathResolver;
        _cacheClearingService = cacheClearingService;
        _steamKit2Service = steamKit2Service;
        _datasourceService = datasourceService;
        _notifications = notifications;
        _userPreferencesService = userPreferencesService;
        _nginxLogRotationService = nginxLogRotationService;
        _cacheManagementService = cacheManagementService;
        _dbContextFactory = dbContextFactory;
        _claimWindow = claimWindow;
    }

    /// <summary>
    /// Gets the system configuration.
    /// </summary>
    /// <remarks>
    /// This is a public endpoint, needed for app initialization before authentication.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("config")]
    [ProducesResponseType(typeof(SystemConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemConfigResponse>> GetConfigAsync()
    {
        // Where the cache, the logs and the data directory live is told to a caller who has signed in
        // and to nobody else. The app loads this response before any session exists, so the route stays
        // anonymous and only the filesystem layout and its writability are withheld. Read once into a
        // local because the datasource projection below runs its branches concurrently.
        var isSignedIn = User.Identity?.IsAuthenticated == true;

        var datasources = _datasourceService.GetDatasources();
        var defaultDatasource = _datasourceService.GetDefaultDatasource();
        var cacheSizeResolutions = (await _cacheManagementService.GetDatasourceCacheSizeResolutionsAsync())
            .ToDictionary(resolution => resolution.DatasourceName, StringComparer.OrdinalIgnoreCase);
        // Enabled-only resolutions exclude disabled datasources; surface their persisted manual
        // override directly so a stored size is not hidden as "full disk" while a datasource is off.
        var cacheSizeOverrides = new Dictionary<string, long>(
            _stateService.GetDatasourceCacheSizeOverrides(), StringComparer.OrdinalIgnoreCase);
        var datasourceDtos = await Task.WhenAll(datasources.Select(async ds =>
        {
            var capabilities = _capabilityService.GetCapabilities(ds);
            var nginxReopen = await _nginxLogRotationService.GetNginxReopenAvailabilityAsync(ds.Layout);
            var cacheSize = cacheSizeResolutions.GetValueOrDefault(ds.Name)
                ?? (cacheSizeOverrides.TryGetValue(ds.Name, out var storedOverride) && storedOverride > 0
                    ? new DatasourceCacheSizeResolution(ds.Name, storedOverride, storedOverride, CacheSizeSource.Manual)
                    : new DatasourceCacheSizeResolution(ds.Name, null, 0, CacheSizeSource.FullDisk));
            return new DatasourceInfoDto
            {
                Name = ds.Name,
                CachePath = isSignedIn ? ds.CachePath : string.Empty,
                LogsPath = isSignedIn ? ds.LogPath : string.Empty,
                CacheWritable = isSignedIn && ds.CacheWritable,
                LogsWritable = isSignedIn && ds.LogsWritable,
                Enabled = ds.Enabled,
                CacheSizeOverrideBytes = cacheSize.OverrideBytes,
                ResolvedCacheSizeBytes = cacheSize.ResolvedBytes,
                CacheSizeSource = cacheSize.Source.ToWireValue(),
                SchemeOverride = ds.SchemeOverride.ToWireValue(),
                CacheKeyScheme = DatasourceCapabilityService.GetSchemeWireValue(capabilities),
                CapabilityDenialReason = capabilities.DenialReason,
                Layout = ds.Layout,
                SourceCount = ds.LogSourceStems.Count,
                CanMapLogicalObjects = capabilities.CanMapLogicalObjects,
                CanClearWholeCacheRoot = capabilities.CanClearWholeCacheRoot,
                NginxReopenAvailable = nginxReopen.Available,
                NginxReopenHint = nginxReopen.Hint == NginxReopenHint.None
                    ? null
                    : nginxReopen.Hint
            };
        }));

        return Ok(new SystemConfigResponse
        {
            // Use first datasource paths for backward compatibility, or fall back to PathResolver
            CachePath = isSignedIn ? (defaultDatasource?.CachePath ?? _pathResolver.GetCacheDirectory()) : string.Empty,
            LogsPath = isSignedIn ? (defaultDatasource?.LogPath ?? _pathResolver.GetLogsDirectory()) : string.Empty,
            DataPath = isSignedIn ? _pathResolver.GetDataDirectory() : string.Empty,
            CacheDeleteMode = _cacheClearingService.GetDeleteMode(),
            SteamAuthMode = _stateService.GetSteamAuthMode() ?? SteamAuthMode.Anonymous,
            TimeZone = ServerTimeZone.IanaId(_configuration),
            // Use cached permission flags maintained by DirectoryPermissionMonitor.
            CacheWritable = isSignedIn && (defaultDatasource?.CacheWritable ?? _pathResolver.IsCacheWritable()),
            LogsWritable = isSignedIn && (defaultDatasource?.LogsWritable ?? _pathResolver.IsLogsWritable()),
            // Include all datasources with their layout + capability evidence.
            DataSources = datasourceDtos.ToList()
        });
    }

    /// <summary>
    /// Checks directory permissions and docker socket availability.
    /// </summary>
    [Authorize]
    [HttpGet("permissions")]
    [ProducesResponseType(typeof(SystemPermissionsResponse), StatusCodes.Status200OK)]
    public ActionResult<SystemPermissionsResponse> GetPermissions()
    {
        var defaultDatasource = _datasourceService.GetDefaultDatasource();
        var cachePath = defaultDatasource?.CachePath ?? _pathResolver.GetCacheDirectory();
        var logPath = defaultDatasource?.LogPath ?? _pathResolver.GetLogsDirectory();

        var cacheExists = Directory.Exists(cachePath);
        var logsExists = Directory.Exists(logPath);
        var cacheWritable = cacheExists && (defaultDatasource?.CacheWritable ?? _pathResolver.IsCacheWritable());
        var logsWritable = logsExists && (defaultDatasource?.LogsWritable ?? _pathResolver.IsLogsWritable());
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
    /// Gets the setup status.
    /// </summary>
    /// <remarks>
    /// This is a public endpoint, needed for AuthenticationModal before authentication.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("setup")]
    [ProducesResponseType(typeof(SetupStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<SetupStatusResponse> GetSetupStatus()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var state = _stateService.GetState();
        var isCompleted = state.SetupCompleted;
        var hasProcessedLogs = state.HasProcessedLogs;

        var mode = Environment.GetEnvironmentVariable("POSTGRES_MODE") ?? "embedded";
        var hasEnvPassword = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_PASSWORD"));
        var hasEnvHost = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POSTGRES_HOST"));
        var credentialsFilePath = _pathResolver.GetPostgresCredentialsPath();
        var hasCredentialsFile = System.IO.File.Exists(credentialsFilePath);

        // External mode needs both a host and a password; embedded only needs the password
        // (the host is the fixed Unix socket).
        var needsPostgresCredentials = mode == "external"
            ? !(hasEnvPassword && hasEnvHost) && !hasCredentialsFile
            : !hasEnvPassword && !hasCredentialsFile;

        // Answered outside the signed-in block below on purpose: the app reads this response before
        // any session exists, and an installation upgrading from a build that had no accounts owns
        // none, so a flag only a signed-in caller could see would be invisible exactly when it
        // decides whether the wizard opens at account creation. An unreachable database leaves it
        // unknown rather than failing the response, because this is also the route a broken install
        // reads to find its way to the credentials step, and answering "no account" there would send
        // it to a step that cannot save.
        bool? accountExists;
        try
        {
            using var context = _dbContextFactory.CreateDbContext();
            accountExists = context.UserAccounts.Any();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read the accounts table, so the setup status reports an unknown account state");
            accountExists = null;
        }

        string? postgresHost = null;
        int? postgresPort = null;
        string? postgresDatabase = null;
        string? postgresUser = null;

        // Where the database lives is told to a caller who has signed in and to nobody else. The
        // completion answer above is read before any session exists, so it stays anonymous while
        // the connection target does not.
        if (User.Identity?.IsAuthenticated == true)
        {
            // Surface the configured external-mode target for the info screen, without
            // ever exposing the password. Pulls from env first; falls back to the
            // credentials file when the UI was used to enter creds.
            postgresDatabase = Environment.GetEnvironmentVariable("POSTGRES_DB");
            postgresUser = Environment.GetEnvironmentVariable("POSTGRES_USER");

            if (mode == "external")
            {
                postgresHost = Environment.GetEnvironmentVariable("POSTGRES_HOST");
                var envPort = Environment.GetEnvironmentVariable("POSTGRES_PORT");
                if (int.TryParse(envPort, out var parsedPort))
                    postgresPort = parsedPort;

                if ((string.IsNullOrEmpty(postgresHost) || postgresPort is null || string.IsNullOrEmpty(postgresDatabase))
                    && hasCredentialsFile)
                {
                    try
                    {
                        var json = System.IO.File.ReadAllText(credentialsFilePath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (string.IsNullOrEmpty(postgresHost) && root.TryGetProperty("host", out var hostElement))
                            postgresHost = hostElement.GetString();
                        if (postgresPort is null && root.TryGetProperty("port", out var portElement))
                        {
                            postgresPort = portElement.ValueKind == System.Text.Json.JsonValueKind.Number
                                ? portElement.GetInt32()
                                : (int.TryParse(portElement.GetString(), out var p) ? p : null);
                        }
                        if (string.IsNullOrEmpty(postgresDatabase) && root.TryGetProperty("database", out var dbElement))
                            postgresDatabase = dbElement.GetString();
                        if (string.IsNullOrEmpty(postgresUser) && root.TryGetProperty("username", out var userElement))
                            postgresUser = userElement.GetString();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Postgres credentials file {Path} is unreadable or malformed, so the setup screen falls back to the environment variables and shows less detail",
                            credentialsFilePath);
                    }
                }
            }
            else if (!needsPostgresCredentials)
            {
                // Embedded mode with credentials configured: surface socket path for the info screen.
                postgresHost = "/var/run/postgresql";
                postgresPort = null;
                if (string.IsNullOrEmpty(postgresDatabase))
                    postgresDatabase = "lancache";
                if (string.IsNullOrEmpty(postgresUser))
                    postgresUser = "lancache";

                if (hasCredentialsFile)
                {
                    try
                    {
                        var json = System.IO.File.ReadAllText(credentialsFilePath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (string.IsNullOrEmpty(postgresDatabase) && root.TryGetProperty("database", out var dbElement))
                            postgresDatabase = dbElement.GetString();
                        if (string.IsNullOrEmpty(postgresUser) && root.TryGetProperty("username", out var userElement))
                            postgresUser = userElement.GetString();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Postgres credentials file {Path} is unreadable or malformed, so the setup screen falls back to the embedded defaults and shows less detail",
                            credentialsFilePath);
                    }
                }
            }
        }

        return Ok(new SetupStatusResponse
        {
            IsCompleted = isCompleted,
            HasProcessedLogs = hasProcessedLogs,
            SetupCompleted = isCompleted, // For backward compatibility
            NeedsPostgresCredentials = needsPostgresCredentials,
            AccountExists = accountExists,
            MainAdminRecoveryAvailable = _claimWindow.IsRecoveryOpen && accountExists == true,
            CurrentSetupStep = state.CurrentSetupStep?.ToWireString(),
            DataSourceChoice = state.DataSourceChoice?.ToWireString(),
            CompletedPlatforms = state.CompletedPlatforms,
            Mode = mode,
            PostgresHost = postgresHost,
            PostgresPort = postgresPort,
            PostgresDatabase = postgresDatabase,
            PostgresUser = postgresUser
        });
    }

    /// <summary>
    /// Updates the setup status.
    /// </summary>
    /// <remarks>
    /// PATCH is the proper method here for partial updates. Request body: { "completed": true }.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("setup")]
    [ProducesResponseType(typeof(SetupUpdateResponse), StatusCodes.Status200OK)]
    public ActionResult<SetupUpdateResponse> UpdateSetupStatus([FromBody] UpdateSetupStatusRequest request)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse.Error("Invalid setup update request body"));
        }

        var hasCompleted = request.Completed.HasValue;
        var hasCurrentSetupStep = request.CurrentSetupStep.HasValue;
        var hasDataSourceChoice = request.DataSourceChoice.HasValue;
        var hasCompletedPlatforms = request.CompletedPlatforms != null;

        // Reject any SetupStep value that deserialized to Unknown - the converter maps
        // unrecognised wire strings to SetupStep.Unknown rather than throwing, so we have
        // to guard here to match the pre-existing "invalid step" error shape.
        if (hasCurrentSetupStep && request.CurrentSetupStep == SetupStep.Unknown)
        {
            return BadRequest(ApiResponse.Error("Invalid currentSetupStep value"));
        }

        var hasUpdate = false;

        _stateService.UpdateState(state =>
        {
            // Persist wizard state fields if provided
            if (hasCurrentSetupStep)
            {
                state.CurrentSetupStep = request.CurrentSetupStep;
                hasUpdate = true;
            }

            if (hasDataSourceChoice)
            {
                state.DataSourceChoice = request.DataSourceChoice;
                hasUpdate = true;
            }

            if (hasCompletedPlatforms)
            {
                state.CompletedPlatforms = request.CompletedPlatforms;
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

        // The wizard's platform choice is erased the moment setup completes (see the
        // hasCompleted branch above), so the scheduled scan mode has to be recorded while the
        // choice is still on the wire. Without this an install where the user deliberately picked
        // Steam PICS would silently keep running the GitHub default that a fresh install starts on.
        // Epic, Xbox and Skip are left alone because they say nothing about how Steam depot data
        // should be refreshed. Set through the service rather than the state directly so the
        // running crawler picks the new mode up without a restart.
        if (hasDataSourceChoice)
        {
            if (request.DataSourceChoice == DataSourceChoice.Steam)
            {
                _steamKit2Service.CrawlIncrementalMode = true;
            }
            else if (request.DataSourceChoice == DataSourceChoice.Github)
            {
                _steamKit2Service.CrawlIncrementalMode = "github";
            }
        }

        // Call SetSetupCompleted AFTER UpdateState so the TaskCompletionSource signal
        // fires and unblocks all gated startup services immediately (no restart needed).
        if (hasCompleted)
        {
            _stateService.SetSetupCompleted(request.Completed!.Value);
        }

        if (hasCompleted)
        {
            _logger.LogInformation("Setup status updated: {Completed}", request.Completed!.Value);
            return Ok(new SetupUpdateResponse { Message = "Setup status updated", SetupCompleted = request.Completed.Value });
        }

        if (hasUpdate)
        {
            return Ok(new SetupUpdateResponse
            {
                Message = "Setup status updated",
                SetupCompleted = _stateService.GetSetupCompleted()
            });
        }

        // No fields produced an update - the request is well-formed but every nullable
        // field is null/absent. Treat this as a valid clear-all-wizard-state no-op,
        // matching the frontend's clearServerWizardState() semantic. This branch fires
        // on wizard completion and on mount when setup is already complete (e.g. after
        // a data restore). Returning 400 here was rejecting a valid intent and triggering
        // the SetupStatusContext retry loop visible in the browser console.
        _stateService.UpdateState(state =>
        {
            state.CurrentSetupStep = null;
            state.DataSourceChoice = null;
            state.CompletedPlatforms = null;
        });

        return Ok(new SetupUpdateResponse
        {
            Message = "Wizard state cleared",
            SetupCompleted = _stateService.GetSetupCompleted()
        });
    }

    /// <summary>
    /// Checks whether rsync is available.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("rsync/available")]
    [ProducesResponseType(typeof(RsyncAvailableResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RsyncAvailableResponse>> IsRsyncAvailableAsync()
    {
        var isAvailable = await _cacheClearingService.IsRsyncAvailableAsync();
        return Ok(new RsyncAvailableResponse { Available = isAvailable });
    }

    /// <summary>
    /// Sets the cache clearing delete mode.
    /// </summary>
    /// <remarks>
    /// PATCH is the proper method here for configuration updates. Request body:
    /// { "deleteMode": "preserve" | "full" | "rsync" }.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("cache-delete-mode")]
    [ProducesResponseType(typeof(CacheDeleteModeResponse), StatusCodes.Status200OK)]
    public ActionResult<CacheDeleteModeResponse> SetCacheDeleteMode([FromBody] SetCacheDeleteModeRequest request)
    {
        _cacheClearingService.SetDeleteMode(request.DeleteMode);
        _logger.LogInformation("Cache delete mode updated to: {Mode}", request.DeleteMode.ToWireString());

        return Ok(new CacheDeleteModeResponse
        {
            Message = "Cache delete mode updated",
            DeleteMode = request.DeleteMode
        });
    }

    /// <summary>
    /// Gets the current refresh rate setting.
    /// </summary>
    /// <remarks>
    /// This is a public endpoint, needed for RefreshRateContext before authentication.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("refresh-rate")]
    [ProducesResponseType(typeof(RefreshRateResponse), StatusCodes.Status200OK)]
    public ActionResult<RefreshRateResponse> GetRefreshRate()
    {
        var rate = _stateService.GetRefreshRate();
        return Ok(new RefreshRateResponse { RefreshRate = rate });
    }

    /// <summary>
    /// Sets the refresh rate.
    /// </summary>
    /// <remarks>
    /// PATCH is the proper method here for configuration updates. Request body:
    /// { "refreshRate": "LIVE" | "ULTRA" | "REALTIME" | "STANDARD" | "RELAXED" | "SLOW" }.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("refresh-rate")]
    [ProducesResponseType(typeof(RefreshRateResponse), StatusCodes.Status200OK)]
    public ActionResult<RefreshRateResponse> SetRefreshRate([FromBody] SetRefreshRateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshRate))
        {
            return BadRequest(ApiResponse.Error("Refresh rate is required"));
        }

        var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
        if (!validRates.Contains(request.RefreshRate.ToUpperInvariant()))
        {
            return BadRequest(ApiResponse.Error("Invalid refresh rate. Must be LIVE, ULTRA, REALTIME, STANDARD, RELAXED, or SLOW"));
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
    /// Gets the default refresh rate for guest users.
    /// </summary>
    /// <remarks>
    /// Anonymous access is intentional: the login screen displays the configured guest refresh rate
    /// before the user authenticates. Leaving this endpoint [Authorize] produced repeated 401
    /// challenges and log spam for every unauthenticated pageload.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("default-guest-refresh-rate")]
    [ProducesResponseType(typeof(DefaultGuestRefreshRateResponse), StatusCodes.Status200OK)]
    public ActionResult<DefaultGuestRefreshRateResponse> GetDefaultGuestRefreshRate()
    {
        var rate = _stateService.GetDefaultGuestRefreshRate();
        var locked = _stateService.GetGuestRefreshRateLocked();
        return Ok(new DefaultGuestRefreshRateResponse { RefreshRate = rate, Locked = locked });
    }

    /// <summary>
    /// Sets the default refresh rate for guest users.
    /// </summary>
    /// <remarks>
    /// PATCH is the proper method here for configuration updates. Request body:
    /// { "refreshRate": "LIVE" | "ULTRA" | "REALTIME" | "STANDARD" | "RELAXED" | "SLOW" }.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("default-guest-refresh-rate")]
    [ProducesResponseType(typeof(RefreshRateResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RefreshRateResponse>> SetDefaultGuestRefreshRateAsync([FromBody] SetRefreshRateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshRate))
        {
            return BadRequest(ApiResponse.Error("Refresh rate is required"));
        }

        var normalizedRate = request.RefreshRate.Trim().ToUpperInvariant();
        var validRates = new[] { "LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW" };
        if (!validRates.Contains(normalizedRate))
        {
            return BadRequest(ApiResponse.Error("Invalid refresh rate. Must be LIVE, ULTRA, REALTIME, STANDARD, RELAXED, or SLOW"));
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
    /// Locks or unlocks guest refresh rate selection.
    /// </summary>
    /// <remarks>
    /// Request body: { "locked": true | false }.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("guest-refresh-rate-lock")]
    [ProducesResponseType(typeof(GuestRefreshRateLockResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GuestRefreshRateLockResponse>> SetGuestRefreshRateLockAsync([FromBody] GuestRefreshRateLockRequest request)
    {
        if (request == null)
        {
            return BadRequest(ApiResponse.Error("Request body is required"));
        }

        _stateService.SetGuestRefreshRateLocked(request.Locked);
        _logger.LogInformation("Guest refresh rate lock set to: {Locked}", request.Locked);

        await _notifications.NotifyAllAsync(SignalREvents.GuestRefreshRateLockChanged, new
        {
            locked = request.Locked
        });

        return Ok(new GuestRefreshRateLockResponse { Success = true, Locked = request.Locked });
    }

    /// <summary>
    /// Gets the default preferences a new guest session starts with, before any per-session override.
    /// </summary>
    [Authorize]
    [HttpGet("default-guest-preferences")]
    [ProducesResponseType(typeof(DefaultGuestPreferencesResponse), StatusCodes.Status200OK)]
    public ActionResult<DefaultGuestPreferencesResponse> GetDefaultGuestPreferences()
    {
        var state = _stateService.GetState();
        return Ok(new DefaultGuestPreferencesResponse
        {
            UseLocalTimezone = state.DefaultGuestUseLocalTimezone,
            UseUtcTimezone = state.DefaultGuestUseUtcTimezone,
            Use24HourFormat = state.DefaultGuestUse24HourFormat,
            SharpCorners = state.DefaultGuestSharpCorners,
            DisableTooltips = state.DefaultGuestDisableTooltips,
            ShowDatasourceLabels = state.DefaultGuestShowDatasourceLabels,
            AllowedTimeFormats = state.AllowedTimeFormats
        });
    }

    /// <summary>
    /// Sets which time formats guests are allowed to pick between, restricting the choices offered by
    /// the per-session clock preference.
    /// </summary>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("default-guest-preferences/allowed-time-formats")]
    [ProducesResponseType(typeof(AllowedTimeFormatsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AllowedTimeFormatsResponse>> SetAllowedTimeFormatsAsync([FromBody] SetAllowedTimeFormatsRequest request)
    {
        if (request.Formats == null || request.Formats.Count == 0)
        {
            return BadRequest(ApiResponse.Error("At least one time format must be allowed"));
        }

        // Validate all formats
        foreach (var format in request.Formats)
        {
            if (!TimeFormats.All.Contains(format))
            {
                return BadRequest(ApiResponse.Error($"Invalid time format: {format}. Valid formats are: {string.Join(", ", TimeFormats.All)}"));
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

        return Ok(new AllowedTimeFormatsResponse { Message = "Allowed time formats updated", Formats = request.Formats });
    }

    /// <summary>
    /// Writes the three default-guest clock fields in one update.
    /// </summary>
    /// <remarks>
    /// Sent as separate requests they cross each other: a guest session created between two of them
    /// copies a clock nobody chose, a second admin changing the clock at the same time leaves the
    /// three fields describing no option at all, and a listener that sees only the field that moved
    /// cannot tell whether the guest it is holding still follows the default. The clock the admin
    /// replaced travels with the new one so it can.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("default-guest-preferences/clock")]
    [ProducesResponseType(typeof(DefaultGuestClockResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetDefaultGuestClockAsync([FromBody] UserPreferencesService.ClockPreferences clock)
    {
        if (clock == null)
        {
            return BadRequest(ApiResponse.Error("A default guest clock needs a value"));
        }

        // Settles the three against each other before anything is stored, so the tuple a new guest copies
        // and the tuple the listeners compare against are the one an admin could have picked.
        UserPreferencesService.NormalizeClockPreferences(clock);

        var previousClock = new UserPreferencesService.ClockPreferences();
        _stateService.UpdateState(state =>
        {
            previousClock.UseUtcTimezone = state.DefaultGuestUseUtcTimezone;
            previousClock.UseLocalTimezone = state.DefaultGuestUseLocalTimezone;
            previousClock.Use24HourFormat = state.DefaultGuestUse24HourFormat;

            state.DefaultGuestUseUtcTimezone = clock.UseUtcTimezone;
            state.DefaultGuestUseLocalTimezone = clock.UseLocalTimezone;
            state.DefaultGuestUse24HourFormat = clock.Use24HourFormat;
        });

        _logger.LogInformation(
            "Default guest clock set to utc={UseUtc}, local={UseLocal}, 24h={Use24Hour}",
            clock.UseUtcTimezone, clock.UseLocalTimezone, clock.Use24HourFormat);

        // Broadcast to all clients so other admins and guest users can update
        await _notifications.NotifyAllAsync(SignalREvents.DefaultGuestPreferencesChanged, new
        {
            key = DefaultGuestClockKey,
            clock,
            previousClock
        });

        return Ok(new DefaultGuestClockResponse { Message = "Default guest clock updated", Clock = clock });
    }

    /// <summary>
    /// Sets a single default guest preference by key.
    /// </summary>
    /// <remarks>
    /// The three clock fields are handled separately by <see cref="SetDefaultGuestClockAsync"/>
    /// since they must be written together.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("default-guest-preferences/{key}")]
    [ProducesResponseType(typeof(DefaultGuestPreferenceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetDefaultGuestPreferenceAsync(string key, [FromBody] SetBoolPreferenceRequest request)
    {
        // The three clock fields are deliberately absent: between them they hold one choice, and this
        // route can only ever carry one of them, so accepting them here is what let a half-applied clock
        // become visible and durable. They go through the clock route above.
        var validKeys = new[] { "sharpCorners", "disableTooltips", "showDatasourceLabels" };
        if (!validKeys.Contains(key))
        {
            return BadRequest(ApiResponse.Error($"Invalid preference key: {key}"));
        }

        _stateService.UpdateState(state =>
        {
            switch (key)
            {
                case "sharpCorners":
                    state.DefaultGuestSharpCorners = request.Value;
                    break;
                case "disableTooltips":
                    state.DefaultGuestDisableTooltips = request.Value;
                    break;
                case "showDatasourceLabels":
                    state.DefaultGuestShowDatasourceLabels = request.Value;
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

        return Ok(new DefaultGuestPreferenceResponse { Message = $"Default guest preference {key} updated", Key = key, Value = request.Value });
    }

    /// <summary>
    /// Gets the default prefill panel settings.
    /// </summary>
    /// <remarks>
    /// Session-aware: a guest session's thread limit clamps the returned max concurrency, while an
    /// admin session gets no clamp.
    /// </remarks>
    [Authorize]
    [HttpGet("prefill-defaults")]
    [ProducesResponseType(typeof(PrefillDefaultsResponse), StatusCodes.Status200OK)]
    public ActionResult<PrefillDefaultsResponse> GetPrefillDefaults()
    {
        var steamMaxThreadLimit = SteamThreadLimit();
        var maxConcurrency = ClampConcurrency(
            _stateService.GetDefaultPrefillMaxConcurrency(), steamMaxThreadLimit);

        return Ok(new PrefillDefaultsResponse
        {
            OperatingSystems = _stateService.GetDefaultPrefillOperatingSystems(),
            MaxConcurrency = maxConcurrency,
            MaxThreadLimit = steamMaxThreadLimit
        });
    }

    /// <summary>
    /// Updates the default prefill panel settings.
    /// </summary>
    /// <remarks>
    /// Broadcasts the change so open prefill panels pick up the new defaults without a reload.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch("prefill-defaults")]
    [ProducesResponseType(typeof(PrefillDefaultsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrefillDefaultsResponse>> SetPrefillDefaultsAsync([FromBody] SetPrefillDefaultsRequest request)
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

        var steamMaxThreadLimit = SteamThreadLimit();

        await _notifications.NotifyAllAsync(SignalREvents.PrefillDefaultsChanged, new
        {
            operatingSystems = _stateService.GetDefaultPrefillOperatingSystems(),
            maxConcurrency = _stateService.GetDefaultPrefillMaxConcurrency(),
            maxThreadLimit = steamMaxThreadLimit
        });

        return Ok(new PrefillDefaultsResponse
        {
            OperatingSystems = _stateService.GetDefaultPrefillOperatingSystems(),
            MaxConcurrency = _stateService.GetDefaultPrefillMaxConcurrency(),
            MaxThreadLimit = steamMaxThreadLimit
        });
    }

    private UserSession? GetSession() => HttpContext.GetUserSession();

    private int? SteamThreadLimit()
    {
        var session = GetSession();
        if (session == null) return null;
        if (session.SessionType.IsAccountHolder()) return null;

        // Guest: check per-user override first, then system default
        var prefs = _userPreferencesService.GetPreferences(session.Id);
        return prefs?.SteamMaxThreadCount ?? _stateService.GetDefaultGuestMaxThreadCount();
    }

    /// <summary>
    /// Clamp the default concurrency value so it does not exceed the guest thread limit.
    /// "auto" passes through unchanged; numeric values are capped.
    /// </summary>
    private static string ClampConcurrency(string concurrency, int? maxThreadLimit)
    {
        if (!maxThreadLimit.HasValue) return concurrency;

        var limit = maxThreadLimit.Value;

        if (int.TryParse(concurrency, out var numeric) && numeric > limit)
            return limit.ToString();

        return concurrency;
    }

}
