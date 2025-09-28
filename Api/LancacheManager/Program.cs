using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LancacheManager.Data;
using LancacheManager.Services;
using LancacheManager.Security;
using LancacheManager.Hubs;
using LancacheManager.Constants;
using Microsoft.AspNetCore.Routing.Constraints;

var builder = WebApplication.CreateBuilder(args);

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

// Database configuration (now can use IPathResolver)
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    // Get the path resolver to determine the database path
    var pathResolver = serviceProvider.GetRequiredService<IPathResolver>();
    var dbPath = Path.Combine(pathResolver.GetDataDirectory(), "LancacheManager.db");

    // Ensure the directory exists
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
    {
        Directory.CreateDirectory(dbDir);
        Console.WriteLine($"Created database directory: {dbDir}");
    }

    Console.WriteLine($"Using database path: {dbPath}");
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
builder.Services.AddSingleton<PathResolverService>();
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddSingleton<StateService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddSingleton<PicsDataService>();

// Register LogProcessingService for high-performance log processing (manual trigger only)
builder.Services.AddSingleton<LogProcessingService>();
// NOTE: LogProcessingService is NOT registered as a hosted service - manual trigger only

// Register LogWatcherService (manual trigger only, not auto-started)
builder.Services.AddSingleton<LogWatcherService>();
// NOTE: LogWatcherService is NOT registered as a hosted service - manual trigger only

// Register CacheClearingService
builder.Services.AddSingleton<CacheClearingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheClearingService>());

// Register OperationStateService
builder.Services.AddSingleton<OperationStateService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OperationStateService>());

// Register background services
builder.Services.AddHostedService<DownloadCleanupService>();

// Add memory cache for storing stats
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<StatsCache>();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Apply additional filtering to reduce log noise
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database", LogLevel.None);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Services.WindowsPathResolver", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Security.ApiKeyService", LogLevel.Warning);
builder.Logging.AddFilter("LancacheManager.Services.LogProcessingService", LogLevel.Information);
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

// Add Authentication Middleware
app.UseMiddleware<AuthenticationMiddleware>();

// Serve static files
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// Map endpoints
app.MapControllers();
app.MapHub<DownloadHub>("/hubs/downloads");

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
    service = "LancacheManager"
}));

// Fallback to index.html for client-side routing
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") || 
        context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/hubs") ||
        context.Request.Path.StartsWithSegments("/swagger"))
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
        
        logger.LogInformation("Database initialization complete");

        // Disabled automatic depot mapping - users now choose initialization method via UI
        // _ = Task.Run(async () => await EnsureDepotMappingsUpdated(app.Services, logger));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed");
        throw; // Fail fast if database init fails
    }
}

// Background task to ensure depot mappings are updated after startup
static async Task EnsureDepotMappingsUpdated(IServiceProvider serviceProvider, ILogger<Program> logger)
{
    try
    {
        logger.LogInformation("Starting background depot mapping update task");

        // Wait for services to be fully started (longer delay to ensure SteamKit2 has time to initialize)
        await Task.Delay(TimeSpan.FromSeconds(30));

        // Create a scope to resolve scoped services
        using var scope = serviceProvider.CreateScope();
        var steamKit2Service = scope.ServiceProvider.GetRequiredService<SteamKit2Service>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Wait up to 10 minutes for SteamKit2Service to become ready or complete its initial crawl
        var maxWait = TimeSpan.FromMinutes(10);
        var waited = TimeSpan.Zero;
        var checkInterval = TimeSpan.FromSeconds(10);

        logger.LogInformation("Waiting for SteamKit2Service to be ready or complete initial crawl...");

        while (waited < maxWait && !steamKit2Service.IsReady)
        {
            await Task.Delay(checkInterval);
            waited += checkInterval;

            if (waited.TotalMinutes % 1 == 0) // Log every minute
            {
                logger.LogInformation($"Still waiting for SteamKit2Service... ({waited.TotalMinutes:F0}/{maxWait.TotalMinutes:F0} minutes)");
            }
        }

        if (steamKit2Service.IsReady)
        {
            logger.LogInformation("SteamKit2Service is ready, checking for downloads needing depot mapping");

            // Check if there are downloads with depot IDs but no game app IDs
            var unmappedDownloads = await dbContext.Downloads
                .Where(d => d.DepotId.HasValue && d.GameAppId == null)
                .CountAsync();

            if (unmappedDownloads > 0)
            {
                logger.LogInformation($"Found {unmappedDownloads} downloads needing depot mapping, triggering update...");

                // Create a scope for the GameInfoController to update depot mappings
                using var innerScope = serviceProvider.CreateScope();
                var gameInfoController = innerScope.ServiceProvider.GetRequiredService<LancacheManager.Controllers.GameInfoController>();

                // Trigger the depot mapping update
                var result = await gameInfoController.UpdateDepotMappings();
                logger.LogInformation("Automatic depot mapping update completed at startup");
            }
            else
            {
                logger.LogInformation("No downloads found needing depot mapping");
            }
        }
        else
        {
            logger.LogWarning($"SteamKit2Service did not become ready within {maxWait.TotalMinutes} minutes - depot mapping update skipped");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error in background depot mapping update task");
    }
}

app.Run();