using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;

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
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Use HTTPS in production
    options.Cookie.SameSite = SameSiteMode.Lax; // CSRF protection
    options.Cookie.Name = "LancacheManager.Session"; // Custom cookie name
    options.Cookie.IsEssential = true; // Required for GDPR compliance
    options.Cookie.MaxAge = TimeSpan.FromDays(30); // Cookie persists for 30 days (survives browser restarts)
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
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

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
builder.Services.AddSingleton<ISettingsRepository, SettingsRepository>();

// Register image caching service
builder.Services.AddSingleton<IImageCacheService, ImageCacheService>();

// Register concrete classes (for code that directly references them)
builder.Services.AddSingleton(sp => (SteamAuthRepository)sp.GetRequiredService<ISteamAuthRepository>());
builder.Services.AddSingleton(sp => (StateRepository)sp.GetRequiredService<IStateRepository>());
builder.Services.AddScoped(sp => (DatabaseRepository)sp.GetRequiredService<IDatabaseRepository>());
builder.Services.AddScoped(sp => (StatsRepository)sp.GetRequiredService<IStatsRepository>());
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
builder.Services.AddSingleton<LancacheManager.Services.UserPreferencesService>();
builder.Services.AddSingleton<LancacheManager.Services.SessionMigrationService>();

// Register SteamKit2Service for real-time Steam depot mapping
builder.Services.AddSingleton<SteamKit2Service>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamKit2Service>());

// Register SteamService as singleton and hosted service (replaces old SteamDepotMappingService)
builder.Services.AddSingleton<SteamService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamService>());

// Register SteamWebApiService for V2/V1 fallback
builder.Services.AddSingleton<SteamWebApiService>();

// Register services (repositories already registered above)
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddSingleton<RemovalOperationTracker>();
builder.Services.AddSingleton<PicsDataService>();

// Depot data initialization service disabled - user must manually download depot data
// builder.Services.AddHostedService<DepotDataInitializationService>();

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

// Register CacheClearingService
builder.Services.AddSingleton<CacheClearingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheClearingService>());

// Register GameCacheDetectionService
builder.Services.AddSingleton<GameCacheDetectionService>();

// Register OperationStateService
builder.Services.AddSingleton<OperationStateService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OperationStateService>());

// Register background services
builder.Services.AddHostedService<LiveLogMonitorService>();
builder.Services.AddHostedService<DownloadCleanupService>();

// Add memory cache for storing stats - use expiration times to control memory
builder.Services.AddMemoryCache(options =>
{
    options.CompactionPercentage = 0.25; // Remove 25% of entries during compaction
    options.ExpirationScanFrequency = TimeSpan.FromSeconds(30); // Scan for expired entries frequently
});
builder.Services.AddSingleton<StatsCache>();

// Add Output Caching for API endpoints
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("dashboard", builder =>
        builder.Expire(TimeSpan.FromSeconds(5)));
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
builder.Logging.AddFilter("LancacheManager.Infrastructure.Services.WindowsPathResolver", LogLevel.Warning);
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

        // This will create the database if it doesn't exist and apply all pending migrations
        await dbContext.Database.MigrateAsync();

        logger.LogInformation("Database migrations applied successfully");

        // Verify connection
        var canConnect = await dbContext.Database.CanConnectAsync();
        if (!canConnect)
        {
            throw new Exception("Cannot connect to database after migration");
        }

        // Load initial stats into cache
        var statsCache = scope.ServiceProvider.GetRequiredService<StatsCache>();
        await statsCache.RefreshFromDatabase(dbContext);

        // Note: LancacheMetricsService will start automatically as IHostedService

        logger.LogInformation("Database initialization complete");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed");
        throw; // Fail fast if database init fails
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

app.UseCors("AllowAll");

// Global exception handler - must run early to catch all exceptions
app.UseGlobalExceptionHandler();

// GC Middleware - must run BEFORE static files to catch all requests
app.UseMiddleware<GcMiddleware>();

// Serve static files
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// Enable session middleware (must be before authentication middleware)
app.UseSession();

app.UseOutputCache();
app.UseAuthorization();

// Add Authentication Middleware (after routing and session so endpoints and sessions are resolved)
app.UseMiddleware<AuthenticationMiddleware>();

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

    // Enable "Authorize" button and persist authorization in browser
    c.EnablePersistAuthorization();
});

// Map endpoints
app.MapControllers();
app.MapHub<DownloadHub>("/hubs/downloads");

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
    return Results.Ok(new { version });
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