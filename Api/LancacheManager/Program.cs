using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Configuration;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Core.Interfaces.Repositories;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces.Services;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Metrics;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Configure session management with cookies
// Sessions are stored in memory (cleared on app restart)
// Session auto-restore logic in AuthController handles app restarts by validating device from DB
builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(24); // Session timeout after 24 hours of inactivity
    options.Cookie.HttpOnly = true; // Prevent JavaScript access (XSS protection)
    // Option A: cookie-based auth for images/guests. Requires HTTPS when cross-site.
    // SameSite=None allows cookies to be sent with image requests across origins.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.Name = "LancacheManager.Session"; // Custom cookie name
    options.Cookie.IsEssential = true; // Required for GDPR compliance
    options.Cookie.MaxAge = TimeSpan.FromDays(30); // Cookie persists for 30 days (survives browser restarts)
});
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
    options.OnAppendCookie = context => AdjustSameSite(context.Context, context.CookieOptions);
    options.OnDeleteCookie = context => AdjustSameSite(context.Context, context.CookieOptions);
});
builder.Services.AddSwaggerGen(c =>
{
    // Add API Key authentication support to Swagger UI
    c.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "API Key authentication. Enter your API key from the Management tab."
    });

    // Apply API Key security requirement to all endpoints
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            new string[] {}
        }
    });
});
builder.Services.AddSignalR(options =>
{
    // Increase timeouts to prevent disconnections during long-running operations (PICS scans, log processing, corruption analysis)
    // PICS scanning can process 740+ batches which takes significant time even with frequent progress updates
    options.KeepAliveInterval = TimeSpan.FromSeconds(10); // Send keepalive every 10 seconds (default: 15)
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(10); // Client timeout after 10 minutes (default: 30s) - generous for slow Steam API responses
    options.HandshakeTimeout = TimeSpan.FromSeconds(30); // Handshake timeout (default: 15)
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
    options.KnownNetworks.Clear();
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

    // Log environment variable overrides if present
    var mallocArenaMax = Environment.GetEnvironmentVariable("MALLOC_ARENA_MAX");
    var mallocTrimThreshold = Environment.GetEnvironmentVariable("MALLOC_TRIM_THRESHOLD_");

    if (!string.IsNullOrEmpty(mallocArenaMax))
    {
        Console.WriteLine($"  MALLOC_ARENA_MAX environment override detected: {mallocArenaMax}");
    }
    if (!string.IsNullOrEmpty(mallocTrimThreshold))
    {
        Console.WriteLine($"  MALLOC_TRIM_THRESHOLD_ environment override detected: {mallocTrimThreshold}");
    }
    if (string.IsNullOrEmpty(mallocArenaMax) && string.IsNullOrEmpty(mallocTrimThreshold))
    {
        Console.WriteLine("  Using automatic malloc configuration (M_ARENA_MAX=4, M_TRIM_THRESHOLD=128KB)");
    }
}
else
{
    throw new PlatformNotSupportedException($"Unsupported operating system: {OperatingSystemDetector.Description}");
}

// Configure Data Protection for encrypting sensitive data
// Keys are stored in the data directory and are machine-specific
// Determine the path based on OS - must match PathResolver logic but cannot use DI yet
string dataProtectionKeyPath;
if (OperatingSystemDetector.IsWindows)
{
    // Windows: Find project root and use data directory
    var projectRoot = FindProjectRootForDataProtection();
    dataProtectionKeyPath = Path.Combine(projectRoot, "data", "DataProtection-Keys");
}
else // Linux/Docker
{
    // Linux: /data/DataProtection-Keys
    dataProtectionKeyPath = Path.Combine("/data", "DataProtection-Keys");
}

// Ensure the directory exists
Directory.CreateDirectory(dataProtectionKeyPath);

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
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ProcessManager>());

// Register Rust process helper for common Rust process operations
builder.Services.AddSingleton<RustProcessHelper>();

// Register repositories with their interfaces
builder.Services.AddSingleton<ISteamAuthRepository, SteamAuthRepository>();
builder.Services.AddSingleton<IStateRepository, StateRepository>();
builder.Services.AddScoped<IDatabaseRepository, DatabaseRepository>();
builder.Services.AddScoped<IStatsRepository, StatsRepository>();
builder.Services.AddScoped<IEventsRepository, EventsRepository>();
builder.Services.AddScoped<IClientGroupsRepository, ClientGroupsRepository>();
builder.Services.AddSingleton<ISettingsRepository, SettingsRepository>();

// Register image caching service
builder.Services.AddSingleton<IImageCacheService, ImageCacheService>();

// Register concrete classes (for code that directly references them)
builder.Services.AddSingleton(sp => (SteamAuthRepository)sp.GetRequiredService<ISteamAuthRepository>());
builder.Services.AddSingleton(sp => (StateRepository)sp.GetRequiredService<IStateRepository>());
builder.Services.AddScoped(sp => (DatabaseRepository)sp.GetRequiredService<IDatabaseRepository>());
builder.Services.AddScoped(sp => (StatsRepository)sp.GetRequiredService<IStatsRepository>());
builder.Services.AddScoped(sp => (EventsRepository)sp.GetRequiredService<IEventsRepository>());
builder.Services.AddSingleton(sp => (SettingsRepository)sp.GetRequiredService<ISettingsRepository>());

// Database configuration (now can use IPathResolver)
var dbPathInitialized = false;
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    // Get the path resolver to determine the database path
    var pathResolver = serviceProvider.GetRequiredService<IPathResolver>();
    var dbPath = Path.Combine(pathResolver.GetDataDirectory(), "LancacheManager.db");

    // Ensure the directory exists (only log once)
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
    {
        Directory.CreateDirectory(dbDir);
        var logger = serviceProvider.GetService<ILogger<Program>>();
        logger?.LogInformation("Created database directory: {DbDir}", dbDir);
    }

    // Only log the path once at startup
    if (!dbPathInitialized)
    {
        var logger = serviceProvider.GetService<ILogger<Program>>();
        logger?.LogInformation("Using database path: {DbPath}", dbPath);
        dbPathInitialized = true;
    }

    // CRITICAL FIX: Simplified connection string - no cache sharing, no pooling
    // Testing if basic connection string prevents memory leak
    options.UseSqlite($"Data Source={dbPath}");
});

// Register DbContextFactory for singleton services that need to create multiple contexts
// Use a custom factory to avoid lifetime conflicts with AddDbContext
builder.Services.AddSingleton<IDbContextFactory<AppDbContext>>(serviceProvider =>
{
    var pathResolver = serviceProvider.GetRequiredService<IPathResolver>();
    var dbPath = Path.Combine(pathResolver.GetDataDirectory(), "LancacheManager.db");
    var connectionString = $"Data Source={dbPath}";

    // Create a factory that returns new DbContext instances on demand
    return new CustomDbContextFactory(connectionString);
});

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
builder.Services.AddSingleton<DeviceAuthService>();
builder.Services.AddSingleton<GuestSessionService>();
builder.Services.AddSingleton<LancacheManager.Core.Services.UserPreferencesService>();
builder.Services.AddSingleton<LancacheManager.Core.Services.SessionMigrationService>();

// Register SignalR connection tracking service for targeted messaging
builder.Services.AddSingleton<LancacheManager.Core.Services.ConnectionTrackingService>();

// Register SteamKit2Service for real-time Steam depot mapping
builder.Services.AddSingleton<SteamKit2Service>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamKit2Service>());

// Register SteamService as singleton and hosted service (replaces old SteamDepotMappingService)
builder.Services.AddSingleton<SteamService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamService>());

// Register SteamWebApiService for V2/V1 fallback
builder.Services.AddSingleton<SteamWebApiService>();

// Register DatasourceService for multi-datasource support
builder.Services.AddSingleton<DatasourceService>();

// Register services (repositories already registered above)
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddSingleton<RemovalOperationTracker>();
builder.Services.AddSingleton<PicsDataService>();

// Register cache snapshot service for historical cache size tracking
builder.Services.AddSingleton<CacheSnapshotService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheSnapshotService>());

// Register metrics service for Prometheus/Grafana as both singleton and hosted service
builder.Services.AddSingleton<LancacheMetricsService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<LancacheMetricsService>());

// Register Rust log processor service (replaces old C# LogProcessingService and LogWatcherService)
builder.Services.AddSingleton<RustLogProcessorService>();

// Register Rust database reset service
builder.Services.AddSingleton<RustDatabaseResetService>();

// Register Rust log removal service
builder.Services.AddSingleton<RustLogRemovalService>();

// Register nginx log rotation service (signals nginx to reopen logs after manipulation)
builder.Services.AddSingleton<NginxLogRotationService>();

// Register nginx log rotation hosted service (runs at startup and on schedule)
builder.Services.AddSingleton<NginxLogRotationHostedService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NginxLogRotationHostedService>());

// Register CacheClearingService
builder.Services.AddSingleton<CacheClearingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheClearingService>());

// Register GameCacheDetectionService
builder.Services.AddSingleton<GameCacheDetectionService>();

// Register CorruptionDetectionService
builder.Services.AddSingleton<CorruptionDetectionService>();

// Register PrefillSessionService for ban management and session persistence
builder.Services.AddSingleton<PrefillSessionService>();

// Register PrefillCacheService for tracking cached depots across sessions
builder.Services.AddSingleton<PrefillCacheService>();

// Register SteamPrefillDaemonService for secure daemon-based prefill management
builder.Services.AddSingleton<SteamPrefillDaemonService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamPrefillDaemonService>());

// Register OperationStateService
builder.Services.AddSingleton<OperationStateService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OperationStateService>());

// Register background services
builder.Services.AddHostedService<LiveLogMonitorService>();
builder.Services.AddHostedService<DownloadCleanupService>();

// Register RustSpeedTrackerService for real-time per-game download speed monitoring (uses Rust for faster parsing)
builder.Services.AddSingleton<RustSpeedTrackerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RustSpeedTrackerService>());

// Add Output Caching for API endpoints
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("dashboard", builder =>
        builder.Expire(TimeSpan.FromSeconds(5)));
    options.AddPolicy("stats-short", builder =>
        builder.Expire(TimeSpan.FromSeconds(10))
               .SetVaryByQuery("startTime", "endTime", "since", "eventId", "includeExcluded", "cacheBust"));
    options.AddPolicy("stats-long", builder =>
        builder.Expire(TimeSpan.FromSeconds(30))
               .SetVaryByQuery("startTime", "endTime", "interval", "eventId", "cacheBust"));
});

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

// Suppress Data Protection key ring warnings when old session cookies are encountered
// This occurs when the data folder is deleted but browser still has cookies encrypted with old keys
// The session middleware gracefully handles this by ignoring invalid cookies
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection.KeyManagement.KeyRingBasedDataProtector", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Session.SessionMiddleware", LogLevel.Error);

builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();

// IMPORTANT: Apply database migrations FIRST before any service tries to access the database
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        logger.LogInformation("Checking database migrations...");

        // Fix schema issues before migrations (SQLite doesn't support ADD COLUMN IF NOT EXISTS)
        await LancacheManager.Infrastructure.Data.DatabaseSchemaFixer.ApplyPreMigrationFixesAsync(dbContext, logger);

        // This will create the database if it doesn't exist and apply all pending migrations
        await dbContext.Database.MigrateAsync();

        logger.LogInformation("Database migrations applied successfully");

        // Verify connection
        var canConnect = await dbContext.Database.CanConnectAsync();
        if (!canConnect)
        {
            throw new Exception("Cannot connect to database after migration");
        }

        // Note: LancacheMetricsService will start automatically as IHostedService

        logger.LogInformation("Database initialization complete");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed");
        throw; // Fail fast if database init fails
    }

    // Migrate operation files from old data directory to new operations subdirectory
    var pathResolver = scope.ServiceProvider.GetRequiredService<IPathResolver>();
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

// NOW it's safe to initialize services that depend on the database
using (var scope = app.Services.CreateScope())
{
    var apiKeyService = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
    var deviceAuthService = scope.ServiceProvider.GetRequiredService<DeviceAuthService>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    apiKeyService.DisplayApiKey(configuration, deviceAuthService); // This will create and display the API key (or show auth disabled message)
}

// MUST be first: Handle forwarded headers from reverse proxies (nginx, Cloudflare, etc.)
// This ensures HttpContext.Connection.RemoteIpAddress returns the real client IP
app.UseForwardedHeaders();

app.UseCors("AllowAll");

// Global exception handler - must run early to catch all exceptions
app.UseGlobalExceptionHandler();

// GC Middleware - must run BEFORE static files to catch all requests
app.UseMiddleware<GcMiddleware>();

// Serve static files
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// Rate limiting - must be after routing to access endpoint metadata
app.UseRateLimiter();

app.UseCookiePolicy();

// Enable session middleware (must be before authentication middleware)
app.UseSession();

// Add Authentication Middleware (after routing and session so endpoints and sessions are resolved)
app.UseMiddleware<AuthenticationMiddleware>();

// Output cache must run AFTER authentication middleware, otherwise cached responses can bypass
// controller-level auth filters (e.g. [RequireGuestSession]).
app.UseOutputCache();

// Add Metrics Authentication Middleware (optional API key for /metrics)
app.UseMiddleware<MetricsAuthenticationMiddleware>();

// Swagger middleware (currently allows full access - authentication handled by Swagger UI's Authorize button)
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
app.MapHub<PrefillDaemonHub>("/hubs/prefill-daemon");

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

static void AdjustSameSite(HttpContext context, CookieOptions options)
{
    if (options.SameSite == SameSiteMode.None && !context.Request.IsHttps)
    {
        options.SameSite = SameSiteMode.Lax;
    }
}

// Helper function to find project root for Data Protection setup (before DI is available)
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
        optionsBuilder.UseSqlite(_connectionString);
        return new AppDbContext(optionsBuilder.Options);
    }
}
