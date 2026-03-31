using FluentValidation;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Configuration;
using LancacheManager.Extensions;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Filters;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Security;
using LancacheManager.Validators;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Metrics;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var migrateOnly =
    string.Equals(Environment.GetEnvironmentVariable("LANCACHE_MIGRATE_ONLY"), "1", StringComparison.Ordinal) ||
    Array.Exists(args, arg => string.Equals(arg, "--migrate-only", StringComparison.OrdinalIgnoreCase));

// Read version from VERSION file if not set in environment (for dev mode)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANCACHE_MANAGER_VERSION")))
{
    try
    {
        var versionFilePath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "VERSION");
        if (File.Exists(versionFilePath))
        {
            var version = File.ReadAllText(versionFilePath).Trim();
            Environment.SetEnvironmentVariable("LANCACHE_MANAGER_VERSION", version);
        }
        else
        {
            Environment.SetEnvironmentVariable("LANCACHE_MANAGER_VERSION", "dev");
        }
    }
    catch
    {
        Environment.SetEnvironmentVariable("LANCACHE_MANAGER_VERSION", "dev");
    }
}

// Add services to the container
builder.Services.AddControllers(options =>
{
    // Add validation filter for FluentValidation error responses
    options.Filters.Add<ValidationFilter>();
});
builder.Services.AddEndpointsApiExplorer();

// Configure FluentValidation for request validation (manual via ValidationFilter)
builder.Services.AddValidatorsFromAssemblyContaining<CreateClientGroupRequestValidator>();

builder.Services.AddSwaggerGen();
builder.Services.AddSignalR(options =>
{
    // Increase timeouts to prevent disconnections during long-running operations (PICS scans, log processing, corruption analysis)
    // PICS scanning can process 740+ batches which takes significant time even with frequent progress updates
    options.KeepAliveInterval = TimeSpan.FromSeconds(10); // Send keepalive every 10 seconds (default: 15)
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(10); // Client timeout after 10 minutes (default: 30s) - generous for slow Steam API responses
    options.HandshakeTimeout = TimeSpan.FromSeconds(30); // Handshake timeout (default: 15)
}).AddJsonProtocol(options =>
{
    // Use camelCase for SignalR JSON serialization to match frontend expectations
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.PayloadSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Configure CORS
// Security:AllowedOrigins can be set to restrict origins (comma-separated list)
// Empty or "*" = allow all origins (for development or same-origin reverse proxy setups)
// Example: "https://lancache.local,https://admin.lancache.local"
var allowedOrigins = builder.Configuration["Security:AllowedOrigins"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        if (string.IsNullOrWhiteSpace(allowedOrigins) || allowedOrigins == "*")
        {
            // Permissive mode - for development or when behind same-origin reverse proxy
            policy.SetIsOriginAllowed(_ => true)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
        else
        {
            // Restricted mode - for internet-exposed instances
            var origins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            policy.WithOrigins(origins)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
    });
});

// Configure forwarded headers for reverse proxy support (nginx, Cloudflare, Traefik, etc.)
// This ensures we get the real client IP instead of the proxy IP
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Clear default known networks/proxies to accept forwarded headers from any source
    // In production behind a trusted proxy, you may want to restrict this
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure API options
builder.Services.Configure<ApiOptions>(
    builder.Configuration.GetSection("ApiOptions"));

// IMPORTANT: Register path resolver FIRST before anything that depends on it
if (OperatingSystemDetector.IsWindows)
{
    builder.Services.AddSingleton<IPathResolver, WindowsPathResolver>();
    builder.Services.AddSingleton<IMemoryManager, WindowsMemoryManager>();
    Console.WriteLine("Platform: Windows - Using WindowsMemoryManager");
}
else if (OperatingSystemDetector.IsLinux)
{
    builder.Services.AddSingleton<IPathResolver, LinuxPathResolver>();
    builder.Services.AddSingleton<IMemoryManager, LinuxMemoryManager>();
    Console.WriteLine("Platform: Linux - Using LinuxMemoryManager with glibc malloc optimizations");
    // Malloc configuration details are logged by LinuxMemoryManager
}
else
{
    throw new PlatformNotSupportedException($"Unsupported operating system: {OperatingSystemDetector.Description}");
}

// Configure Data Protection for encrypting sensitive data
// Keys are stored in the data directory and are machine-specific
// Determine the path based on OS - must match PathResolver logic but cannot use DI yet
string dataRoot;
if (OperatingSystemDetector.IsWindows)
{
    // Windows: Find project root and use data directory
    var projectRoot = FindProjectRootForDataProtection();
    dataRoot = Path.Combine(projectRoot, "data");
}
else // Linux/Docker
{
    // Linux: /data
    dataRoot = "/data";
}

var legacyKeyPath = Path.Combine(dataRoot, "DataProtection-Keys");
var securityDir = Path.Combine(dataRoot, "security");
var dataProtectionKeyPath = Path.Combine(securityDir, "DataProtection-Keys");

// Migrate legacy key path if present
MigrateDataProtectionKeys(legacyKeyPath, securityDir, dataProtectionKeyPath);

// Ensure the directory exists
Directory.CreateDirectory(dataProtectionKeyPath);

if (OperatingSystem.IsLinux())
{
    try
    {
        File.SetUnixFileMode(dataProtectionKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    catch (Exception ex)
    {
        // Log but don't fail startup
        Console.WriteLine($"Warning: Could not set permissions on DataProtection keys directory: {ex.Message}");
    }
}

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));

// On Windows, use DPAPI to encrypt keys at rest
// On Linux/Docker, keys are protected by filesystem permissions (chmod 700)
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

// Log where keys will be stored (will be logged again later with proper ILogger)
Console.WriteLine($"Data Protection keys will be stored in: {dataProtectionKeyPath}");

// Register encryption service for state.json sensitive fields
builder.Services.AddSingleton<SecureStateEncryptionService>();

// Register process manager for tracking and cleaning up spawned processes
builder.Services.AddSingletonHostedService<ProcessManager>();

// Register Rust process helper for common Rust process operations
builder.Services.AddSingleton<RustProcessHelper>();

// Register repositories with their interfaces
builder.Services.AddSingleton<ISteamAuthStorageService, SteamAuthStorageService>();
builder.Services.AddSingleton<IStateService, StateService>();
builder.Services.AddScoped<IDatabaseService, DatabaseService>();
builder.Services.AddScoped<IStatsDataService, StatsDataService>();
builder.Services.AddScoped<IEventsService, EventsService>();
builder.Services.AddScoped<IClientGroupsService, ClientGroupsService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<PathMigrationService>();

// Register image caching service
builder.Services.AddSingleton<IImageCacheService, ImageCacheService>();

// Register SignalR notification service
builder.Services.AddSingleton<ISignalRNotificationService, SignalRNotificationService>();

// Register concrete classes (for code that directly references them)
builder.Services.AddSingleton(sp => (SteamAuthStorageService)sp.GetRequiredService<ISteamAuthStorageService>());
builder.Services.AddSingleton(sp => (StateService)sp.GetRequiredService<IStateService>());
builder.Services.AddScoped(sp => (DatabaseService)sp.GetRequiredService<IDatabaseService>());
builder.Services.AddScoped(sp => (StatsDataService)sp.GetRequiredService<IStatsDataService>());
builder.Services.AddScoped(sp => (EventsService)sp.GetRequiredService<IEventsService>());
builder.Services.AddSingleton(sp => (SettingsService)sp.GetRequiredService<ISettingsService>());

// Database configuration — build connection string dynamically from env vars or config file
var baseConnStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

var pgUser = Environment.GetEnvironmentVariable("POSTGRES_USER");
var pgPassword = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");

// Check persistent config file if no env var credentials (production Docker setup page flow)
// Runs pre-DI; cannot use IPathResolver. Uses dataRoot computed above.
const string PostgresCredentialsFileName = "postgres-credentials.json";
if (string.IsNullOrEmpty(pgPassword))
{
    var configPath = Path.Combine(dataRoot, "config", PostgresCredentialsFileName);
    if (File.Exists(configPath))
    {
        try
        {
            var json = File.ReadAllText(configPath);
            var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (config != null)
            {
                pgPassword = config.GetValueOrDefault("password");
                if (string.IsNullOrEmpty(pgUser))
                    pgUser = config.GetValueOrDefault("username");
            }
        }
        catch
        {
            // Config file corrupt or unreadable — will show setup page
        }
    }
}

// Build connection string with credentials
// Start from base connection string (appsettings) — only override if env vars or config file provided values
var connBuilder = new Npgsql.NpgsqlConnectionStringBuilder(baseConnStr);
if (!string.IsNullOrEmpty(pgUser))
    connBuilder.Username = pgUser;
if (!string.IsNullOrEmpty(pgPassword))
    connBuilder.Password = pgPassword;

var dbConnectionString = connBuilder.ConnectionString;

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.UseNpgsql(dbConnectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorCodesToAdd: null  // null means all transient errors including deadlock 40P01
        );
    });
});

// Register DbContextFactory for singleton services that need to create multiple contexts
// Use a custom factory to avoid lifetime conflicts with AddDbContext
builder.Services.AddSingleton<IDbContextFactory<AppDbContext>>(_ =>
    new CustomDbContextFactory(dbConnectionString));

// Register HttpClientFactory for better HTTP client management
builder.Services.AddHttpClient();

// Register HttpClient for SteamService
builder.Services.AddHttpClient<SteamService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
});

// Register HttpClient for Steam image proxying with shorter timeout
builder.Services.AddHttpClient("SteamImages", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15); // Shorter timeout for image fetches
    client.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
});

// Register Authentication Services
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddScoped<AuthenticationHelper>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<LancacheManager.Core.Services.UserPreferencesService>();

// ASP.NET Core Authentication (session-based via cookie)
builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, null);

// Authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireClaim("SessionType", "admin"));

    options.AddPolicy("GuestAllowed", policy =>
        policy.RequireAuthenticatedUser());

    options.AddPolicy("SteamPrefillAccess", policy =>
        policy.RequireClaim("SteamPrefillActive", "true"));

    options.AddPolicy("EpicPrefillAccess", policy =>
        policy.RequireClaim("EpicPrefillActive", "true"));

    options.AddPolicy("AnyPrefillAccess", policy =>
        policy.RequireAssertion(context =>
            context.User.HasClaim("SteamPrefillActive", "true") ||
            context.User.HasClaim("EpicPrefillActive", "true")));
});

// Register SignalR connection tracking service for targeted messaging
builder.Services.AddSingleton<LancacheManager.Core.Services.ConnectionTrackingService>();

// Register SteamKit2Service for real-time Steam depot mapping
builder.Services.AddSingletonHostedService<SteamKit2Service>();

// Register SteamService as singleton and hosted service (replaces old SteamDepotMappingService)
builder.Services.AddSingletonHostedService<SteamService>();

// Register SteamWebApiService for V2/V1 fallback
builder.Services.AddSingleton<SteamWebApiService>();

// Register DatasourceService for multi-datasource support
builder.Services.AddSingleton<DatasourceService>();

// Register services (repositories already registered above)
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddSingleton<IUnifiedOperationTracker, UnifiedOperationTracker>();
builder.Services.AddSingleton<PicsDataService>();

// Register cache snapshot service for historical cache size tracking
builder.Services.AddSingletonHostedService<CacheSnapshotService>();

// Register metrics service for Prometheus/Grafana as both singleton and hosted service
builder.Services.AddSingletonHostedService<LancacheMetricsService>();

// Register Rust log processor service (replaces old C# LogProcessingService and LogWatcherService)
builder.Services.AddSingleton<RustLogProcessorService>();

// Register Rust database reset service
builder.Services.AddSingleton<RustDatabaseResetService>();

// Register Rust log removal service
builder.Services.AddSingleton<RustLogRemovalService>();

// Register nginx log rotation service (signals nginx to reopen logs after manipulation)
builder.Services.AddSingleton<NginxLogRotationService>();

// Register nginx log rotation hosted service (runs at startup and on schedule)
builder.Services.AddSingletonHostedService<NginxLogRotationHostedService>();

// Register CacheClearingService
builder.Services.AddSingletonHostedService<CacheClearingService>();

// Register GameCacheDetectionService
builder.Services.AddSingleton<GameCacheDetectionService>();

// Register CorruptionDetectionService
builder.Services.AddSingleton<CorruptionDetectionService>();

// Register PrefillSessionService for ban management and session persistence
builder.Services.AddSingleton<PrefillSessionService>();

// Register PrefillCacheService for tracking cached depots across sessions
builder.Services.AddSingleton<PrefillCacheService>();

// Register SteamDaemonService for secure daemon-based prefill management
builder.Services.AddSingletonHostedService<SteamDaemonService>();

// Register EpicPrefillDaemonService for Epic Games daemon-based prefill management
builder.Services.AddSingletonHostedService<EpicPrefillDaemonService>();

// Register EpicApiDirectClient for direct HTTP calls to Epic APIs (no Docker needed)
builder.Services.AddHttpClient<EpicApiDirectClient>();

// Register EpicAuthStorageService for Epic credential persistence
builder.Services.AddSingleton<EpicAuthStorageService>();

// Register unified EpicMappingService for game discovery, mapping, and scheduling
builder.Services.AddSingletonHostedService<EpicMappingService>();

// Register OperationStateService
builder.Services.AddSingletonHostedService<OperationStateService>();

// Register background services
builder.Services.AddHostedService<LiveLogMonitorService>();
builder.Services.AddHostedService<DownloadCleanupService>();
builder.Services.AddSingletonHostedService<CacheReconciliationService>();
builder.Services.AddSingletonHostedService<GameImageFetchService>();
builder.Services.AddHostedService<DirectoryPermissionMonitorService>();

// Register RustSpeedTrackerService for real-time per-game download speed monitoring (uses Rust for faster parsing)
builder.Services.AddSingletonHostedService<RustSpeedTrackerService>();

// Register GameDetectionStartupService to auto-trigger game cache detection at startup
builder.Services.AddSingletonHostedService<GameDetectionStartupService>();

// Configure OpenTelemetry Metrics for Prometheus + Grafana
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .AddPrometheusExporter()
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("LancacheManager"); // For custom business metrics
    });

// Configure Rate Limiting for auth endpoints
// Protects against brute force attacks on login/device registration
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    // Rate limit for authentication endpoints (login, device registration)
    options.AddFixedWindowLimiter("auth", config =>
    {
        config.PermitLimit = 5;           // 5 attempts
        config.Window = TimeSpan.FromMinutes(1); // per minute
        config.QueueLimit = 0;            // No queuing, reject immediately
    });
    
    // Rate limit for Steam auth (more lenient due to 2FA flows)
    options.AddFixedWindowLimiter("steam-auth", config =>
    {
        config.PermitLimit = 10;          // 10 attempts
        config.Window = TimeSpan.FromMinutes(5); // per 5 minutes
        config.QueueLimit = 0;
    });
});

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Apply additional filtering to reduce log noise
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Extensions.Http.DefaultHttpClientFactory", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Infrastructure.Platform.WindowsPathResolver", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Security.ApiKeyService", LogLevel.Information);
builder.Logging.AddFilter("LancacheManager.Infrastructure.Services.RustLogProcessorService", LogLevel.Information);
builder.Logging.AddFilter("LancacheManager.Services.CacheManagementService", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Services.PicsDataService", LogLevel.Information);

builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();

// IMPORTANT: Apply database migrations FIRST before any service tries to access the database
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var pathResolver = scope.ServiceProvider.GetRequiredService<IPathResolver>();
    var pathMigrationService = scope.ServiceProvider.GetRequiredService<PathMigrationService>();

    var migrationResult = pathMigrationService.MigrateLegacyDataLayout();
    if (migrationResult.FilesMoved > 0 || migrationResult.DirectoriesMoved > 0)
    {
        logger.LogInformation("Migrated legacy data layout (files: {FilesMoved}, dirs: {DirsMoved})",
            migrationResult.FilesMoved, migrationResult.DirectoriesMoved);
    }

    // On Windows dev, ensure PostgreSQL Docker container is running before attempting migration
    await WindowsPostgresManager.EnsurePostgresRunningAsync(dbConnectionString, logger);

    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        logger.LogInformation("Checking database migrations...");

        // Allow long-running migrations (e.g., column type changes that rewrite large tables)
        dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

        // This will create the database if it doesn't exist and apply all pending migrations
        await dbContext.Database.MigrateAsync();
        await DatabaseSchemaFixer.ApplyPostMigrationFixesAsync(dbContext, logger);

        // Reset any incorrectly set eviction flags from initial reconciliation run
        var evictedCount = await dbContext.Downloads
            .Where(d => d.IsEvicted)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsEvicted, false));
        if (evictedCount > 0)
        {
            logger.LogInformation("Reset {Count} incorrectly evicted downloads", evictedCount);
        }

        logger.LogInformation("Database migrations applied successfully");

        // Verify connection
        var canConnect = await dbContext.Database.CanConnectAsync();
        if (!canConnect)
        {
            throw new Exception("Cannot connect to database after migration");
        }

        // Note: LancacheMetricsService will start automatically as IHostedService

        logger.LogInformation("Database initialization complete");

        if (migrateOnly)
        {
            logger.LogInformation("Migration-only mode completed successfully. Exiting without starting the web host.");
            return;
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed");
        throw; // Fail fast if database init fails
    }

    // Migrate operation files from old data directory to new operations subdirectory
    var migratedCount = pathResolver.MigrateOperationFilesToNewLocation();
    if (migratedCount > 0)
    {
        logger.LogInformation("Migrated {Count} operation files to operations directory", migratedCount);
    }

    // Clean up old operation progress files (cache_clear, corruption_removal, etc.)
    var cleanedCount = pathResolver.CleanupOldOperationFiles(maxAgeHours: 24);
    if (cleanedCount > 0)
    {
        logger.LogInformation("Cleaned up {Count} old operation files on startup", cleanedCount);
    }
}

// Display API key to console on startup
var apiKeyService = app.Services.GetRequiredService<ApiKeyService>();
apiKeyService.DisplayApiKey(app.Configuration);

// If a new API key was generated (data folder was deleted), invalidate all old sessions
// so existing browser cookies cannot authenticate against the new key.
if (apiKeyService.WasNewKeyGenerated)
{
    using var startupScope = app.Services.CreateScope();
    var sessionService = startupScope.ServiceProvider.GetRequiredService<SessionService>();
    await sessionService.ClearAllSessionsAsync();
}

// MUST be first: Handle forwarded headers from reverse proxies (nginx, Cloudflare, etc.)
// This ensures HttpContext.Connection.RemoteIpAddress returns the real client IP
app.UseForwardedHeaders();


app.UseCors("AllowAll");

// Global exception handler - must run early to catch all exceptions
app.UseGlobalExceptionHandler();

// GC Middleware - must run BEFORE static files to catch all requests
app.UseMiddleware<GcMiddleware>();

// Serve static files (UseDefaultFiles rewrites / to /index.html for faster static serving)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// Rate limiting - must be after routing to access endpoint metadata
app.UseRateLimiter();

// ASP.NET Core authentication & authorization pipeline
// SessionAuthenticationHandler populates HttpContext.Items["Session"] for backward compatibility.
app.UseAuthentication();
app.UseAuthorization();

// Add Metrics Authentication Middleware (optional API key for /metrics)
app.UseMiddleware<MetricsAuthenticationMiddleware>();

// Swagger authentication middleware (requires API key when Security:ProtectSwagger=true)
app.UseMiddleware<SwaggerAuthenticationMiddleware>();

// Enable Swagger in all environments
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "LancacheManager API V1");
    c.RoutePrefix = "swagger"; // Access at /swagger

    // Note: We intentionally do NOT call EnablePersistAuthorization()
    // This prevents storing API keys in browser localStorage (security risk)
    // Users must re-enter the API key on each page load, but it's more secure
});

// Map endpoints
app.MapControllers();
app.MapHub<DownloadHub>("/hubs/downloads");
app.MapHub<SteamDaemonHub>("/hubs/steam-daemon");
app.MapHub<EpicPrefillDaemonHub>("/hubs/epic-prefill-daemon");

// Map Prometheus metrics endpoint for Grafana
app.MapPrometheusScrapingEndpoint();

// Explicit route mapping for OperationState controller to fix 404 issues
app.MapControllerRoute(
    name: "operationstate_patch",
    pattern: "api/operationstate/{key}",
    defaults: new { controller = "OperationState", action = "UpdateState" },
    constraints: new { httpMethod = new HttpMethodRouteConstraint("PATCH") });

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    service = "LancacheManager",
    version = Environment.GetEnvironmentVariable("LANCACHE_MANAGER_VERSION") ?? "dev"
}));

// Version endpoint
app.MapGet("/api/version", () =>
{
    var version = Environment.GetEnvironmentVariable("LANCACHE_MANAGER_VERSION") ?? "dev";
    return Results.Ok(new LancacheManager.Models.VersionResponse { Version = version });
});

// Fallback to index.html for client-side routing
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/hubs") ||
        context.Request.Path.StartsWithSegments("/metrics"))
    {
        context.Response.StatusCode = 404;
        return;
    }

    var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(indexPath);
    }
    else
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("index.html not found");
    }
});

// Log depot count for diagnostics (non-blocking background task after scope is disposed)
_ = Task.Run(async () =>
{
    try
    {
        // Small delay to ensure app.Services is fully ready
        await Task.Delay(100);

        using var backgroundScope = app.Services.CreateScope();
        var backgroundContext = backgroundScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var backgroundLogger = backgroundScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        var depotCount = await backgroundContext.SteamDepotMappings.CountAsync();
        backgroundLogger.LogInformation("Database has {DepotCount} depot mappings", depotCount);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to check depot count: {ex.Message}");
    }
});

app.Run();

// Runs pre-DI; cannot use IPathResolver. Mirrors PathResolverBase.GetDataProtectionKeysPath()
static string FindProjectRootForDataProtection()
{
    var currentDir = Directory.GetCurrentDirectory().Replace('/', '\\');

    // Quick check: if we're in Api\LancacheManager, go up two levels
    if (currentDir.EndsWith("\\Api\\LancacheManager", StringComparison.OrdinalIgnoreCase))
    {
        var projectRoot = Directory.GetParent(currentDir)?.Parent?.FullName;
        if (projectRoot != null && Directory.Exists(Path.Combine(projectRoot, "Api")) &&
            Directory.Exists(Path.Combine(projectRoot, "Web")))
        {
            return projectRoot;
        }
    }

    // Search up the directory tree
    var dir = new DirectoryInfo(currentDir);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Api")) &&
            Directory.Exists(Path.Combine(dir.FullName, "Web")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException($"Could not find project root from: {currentDir}");
}

// Helper function to migrate Data Protection keys from legacy path to new security directory
static void MigrateDataProtectionKeys(string legacyKeyPath, string securityDir, string dataProtectionKeyPath)
{
    try
    {
        if (!Directory.Exists(legacyKeyPath))
            return;

        Directory.CreateDirectory(securityDir);

        if (!Directory.Exists(dataProtectionKeyPath))
        {
            Directory.Move(legacyKeyPath, dataProtectionKeyPath);
            Console.WriteLine($"Migrated Data Protection keys to: {dataProtectionKeyPath}");
            return;
        }

        // Target directory already exists - merge files individually
        foreach (var file in Directory.GetFiles(legacyKeyPath))
        {
            var destFile = Path.Combine(dataProtectionKeyPath, Path.GetFileName(file));
            if (!File.Exists(destFile))
            {
                File.Move(file, destFile);
            }
        }

        if (Directory.GetFileSystemEntries(legacyKeyPath).Length == 0)
        {
            Directory.Delete(legacyKeyPath);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to migrate Data Protection keys: {ex.Message}");
    }
}

// Custom DbContext factory implementation for singleton services
class CustomDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly string _connectionString;

    public CustomDbContextFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public AppDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(_connectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null
            );
        });
        return new AppDbContext(optionsBuilder.Options);
    }
}
