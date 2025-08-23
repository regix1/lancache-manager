using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Services;
using LancacheManager.Hubs;
using LancacheManager.Data;

var builder = WebApplication.CreateBuilder(args);

// Set default connection string if not provided
if (string.IsNullOrEmpty(builder.Configuration.GetConnectionString("DefaultConnection")))
{
    // Use /data if it exists (Docker), otherwise use local directory
    var dataPath = Directory.Exists("/data") ? "/data" : ".";
    var dbPath = Path.Combine(dataPath, "lancache-manager.db");
    builder.Configuration["ConnectionStrings:DefaultConnection"] = $"Data Source={dbPath}";
    
    Console.WriteLine($"Using default database path: {dbPath}");
}

// Set default log path if not provided
if (string.IsNullOrEmpty(builder.Configuration["LanCache:LogPath"]))
{
    // The LogWatcherService will auto-detect, but we can set a hint
    if (Directory.Exists("/logs"))
    {
        builder.Configuration["LanCache:LogPath"] = "/logs/access.log";
    }
    else if (Directory.Exists("/var/lancache/logs"))
    {
        builder.Configuration["LanCache:LogPath"] = "/var/lancache/logs/access.log";
    }
}

// Set default cache path if not provided
if (string.IsNullOrEmpty(builder.Configuration["LanCache:CachePath"]))
{
    if (Directory.Exists("/cache"))
    {
        builder.Configuration["LanCache:CachePath"] = "/cache";
    }
    else if (Directory.Exists("/var/lancache/cache"))
    {
        builder.Configuration["LanCache:CachePath"] = "/var/lancache/cache";
    }
    else
    {
        builder.Configuration["LanCache:CachePath"] = Path.Combine(Path.GetTempPath(), "lancache/cache");
    }
}

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Lancache Manager API",
        Version = "v1",
        Description = "API for monitoring and managing Lancache"
    });
});

builder.Services.AddSignalR();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

// Add SQLite with automatic migration
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    options.UseSqlite(connectionString);
    
    // Enable sensitive data logging in development
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// Add custom services
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddHostedService<LogWatcherService>();
builder.Services.AddScoped<DatabaseService>();

// Add sample log generator only in development or if explicitly enabled
if (builder.Environment.IsDevelopment() || 
    builder.Configuration.GetValue<bool>("LanCache:GenerateSampleLogs", false))
{
    builder.Services.AddHostedService<SampleLogGeneratorService>();
}

// Add health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database");

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Lancache Manager API v1");
        c.RoutePrefix = "swagger";
    });
    
    // Development exception page
    app.UseDeveloperExceptionPage();
}
else
{
    // Production error handling
    app.UseExceptionHandler("/error");
}

app.UseCors("AllowAll");

// Health check endpoint
app.MapHealthChecks("/health");

// Serve static files from wwwroot (React app)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapHub<DownloadHub>("/downloadHub");

// Fallback to index.html for client-side routing
app.MapFallbackToFile("index.html");

// Ensure database is created and migrated
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        // Create database directory if it doesn't exist
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(connectionString))
        {
            var dbPath = connectionString.Replace("Data Source=", "").Trim();
            var dbDirectory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDirectory) && !Directory.Exists(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory);
                logger.LogInformation($"Created database directory: {dbDirectory}");
            }
        }
        
        // Apply any pending migrations or create database
        if (dbContext.Database.GetPendingMigrations().Any())
        {
            logger.LogInformation("Applying database migrations...");
            dbContext.Database.Migrate();
        }
        else
        {
            // Ensure database exists
            dbContext.Database.EnsureCreated();
        }
        
        logger.LogInformation("Database is ready");
        
        // Log configuration for debugging
        logger.LogInformation($"Environment: {app.Environment.EnvironmentName}");
        logger.LogInformation($"Database: {connectionString}");
        logger.LogInformation($"Log Path Hint: {builder.Configuration["LanCache:LogPath"] ?? "Auto-detect"}");
        logger.LogInformation($"Cache Path: {builder.Configuration["LanCache:CachePath"] ?? "Not configured"}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while initializing the database");
        throw;
    }
}

// Log startup information
var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation("==============================================");
startupLogger.LogInformation("Lancache Manager started successfully!");
startupLogger.LogInformation($"Environment: {app.Environment.EnvironmentName}");
startupLogger.LogInformation($"URLs: {builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000"}");
startupLogger.LogInformation("API Documentation: /swagger");
startupLogger.LogInformation("Health Check: /health");
startupLogger.LogInformation("==============================================");

app.Run();