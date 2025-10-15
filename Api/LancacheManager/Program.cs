using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Middleware;
using LancacheManager.Security;
using LancacheManager.Services;
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
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

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
}
else if (OperatingSystemDetector.IsLinux)
{
    builder.Services.AddSingleton<IPathResolver, LinuxPathResolver>();
}
else
{
    throw new PlatformNotSupportedException($"Unsupported operating system: {OperatingSystemDetector.Description}");
}

// Configure Data Protection for encrypting sensitive data
// Keys are stored in the data directory and are machine-specific
// Determine path based on OS without creating service provider
var dataProtectionKeyPath = OperatingSystemDetector.IsWindows
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LancacheManager", "DataProtection-Keys")
    : Path.Combine("/data", "DataProtection-Keys");

var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));

// On Windows, use DPAPI to encrypt keys at rest
// On Linux/Docker, keys are protected by filesystem permissions (chmod 700)
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi();
}

// Register encryption service for state.json sensitive fields
builder.Services.AddSingleton<SecureStateEncryptionService>();

// Register Steam authentication storage service (separate encrypted file with Microsoft Data Protection API)
builder.Services.AddSingleton<SteamAuthStorageService>();

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

    options.UseSqlite($"Data Source={dbPath};Cache=Shared;Pooling=false");
});

// Register HttpClientFactory for better HTTP client management
builder.Services.AddHttpClient();

// Register HttpClient for SteamService
builder.Services.AddHttpClient<SteamService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
});

// Register Authentication Services
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<DeviceAuthService>();

// Register SteamKit2Service for real-time Steam depot mapping
builder.Services.AddSingleton<SteamKit2Service>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamKit2Service>());

// Register SteamService as singleton and hosted service (replaces old SteamDepotMappingService)
builder.Services.AddSingleton<SteamService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamService>());

// Register services
builder.Services.AddSingleton<StateService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddSingleton<PicsDataService>();

// Register metrics service for Prometheus/Grafana
builder.Services.AddSingleton<LancacheMetricsService>();

// Register Rust log processor service (replaces old C# LogProcessingService and LogWatcherService)
builder.Services.AddSingleton<RustLogProcessorService>();

// Register Rust database reset service
builder.Services.AddSingleton<RustDatabaseResetService>();

// Register CacheClearingService
builder.Services.AddSingleton<CacheClearingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheClearingService>());

// Register OperationStateService
builder.Services.AddSingleton<OperationStateService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OperationStateService>());

// Register background services
builder.Services.AddHostedService<DownloadCleanupService>();
builder.Services.AddHostedService<LiveLogMonitorService>();

// Add memory cache for storing stats
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<StatsCache>();

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
builder.Logging.AddFilter("LancacheManager.Services.WindowsPathResolver", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Security.ApiKeyService", LogLevel.Information);
builder.Logging.AddFilter("LancacheManager.Services.RustLogProcessorService", LogLevel.Information);
builder.Logging.AddFilter("LancacheManager.Services.CacheManagementService", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Services.PicsDataService", LogLevel.Information);
builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();

// Initialize API Key on startup
using (var scope = app.Services.CreateScope())
{
    var apiKeyService = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
    apiKeyService.DisplayApiKey(); // This will create and display the API key
}

// Enable Swagger in all environments
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "LancacheManager API V1");
    c.RoutePrefix = "swagger"; // Access at /swagger
});

app.UseCors("AllowAll");

// Serve static files
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// Add Authentication Middleware (after routing so endpoints are resolved)
app.UseMiddleware<AuthenticationMiddleware>();

// Add Metrics Authentication Middleware (optional API key for /metrics)
app.UseMiddleware<MetricsAuthenticationMiddleware>();

// Minimal API endpoint for canceling log processing - NO database access required
// This endpoint must work even when database is locked by Rust process
app.MapPost("/api/management/cancel-processing", (IPathResolver pathResolver, ILogger<Program> logger) =>
{
    try
    {
        // Create cancel marker file IMMEDIATELY - no database access needed
        var dataDirectory = pathResolver.GetDataDirectory();
        var cancelMarkerPath = Path.Combine(dataDirectory, "cancel_processing.marker");
        File.WriteAllText(cancelMarkerPath, DateTime.UtcNow.ToString());
        logger.LogInformation("Cancel marker created at {Path}", cancelMarkerPath);

        return Results.Ok(new { message = "Log processing cancelled" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error creating cancel marker");
        return Results.Problem("Failed to cancel processing: " + ex.Message, statusCode: 500);
    }
}); // Authentication handled by middleware, no RequireAuthorization needed

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
app.MapGet("/health", () => Results.Ok(new {
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
        context.Request.Path.StartsWithSegments("/swagger") ||
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

// Apply EF Core migrations
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

        // Initialize metrics service to start background metric collection
        var metricsService = scope.ServiceProvider.GetRequiredService<LancacheMetricsService>();
        logger.LogInformation("LancacheMetricsService initialized");

        // Log depot count for diagnostics
        try
        {
            var depotCount = await dbContext.SteamDepotMappings.CountAsync();
            logger.LogInformation("Database has {DepotCount} depot mappings", depotCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check depot count");
        }

        logger.LogInformation("Database initialization complete");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed");
        throw; // Fail fast if database init fails
    }
}

app.Run();