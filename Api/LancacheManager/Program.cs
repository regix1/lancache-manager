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

// Add Authentication Middleware
app.UseMiddleware<AuthenticationMiddleware>();

// Serve static files
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

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
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed");
        throw; // Fail fast if database init fails
    }
}

app.Run();