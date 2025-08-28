using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Services;
using LancacheManager.Hubs;

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

// Database configuration for Linux/Docker
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var dbPath = "/data/lancache.db";
    
    // Ensure the directory exists
    if (!Directory.Exists("/data"))
    {
        Directory.CreateDirectory("/data");
    }
    
    options.UseSqlite($"Data Source={dbPath}");
});

// Register HttpClientFactory for better HTTP client management
builder.Services.AddHttpClient();

// Register HttpClient for SteamService
builder.Services.AddHttpClient<SteamService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
});

// Register the Steam depot mapping service as singleton and hosted service
builder.Services.AddSingleton<SteamDepotMappingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SteamDepotMappingService>());

// Register SteamService as singleton (updated to use depot mapping service)
builder.Services.AddSingleton<SteamService>();

// Register services
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddSingleton<CacheManagementService>();

// Register the new CacheClearingService for async cache operations
builder.Services.AddSingleton<CacheClearingService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<CacheClearingService>());

// Register OperationStateService for persistent operation tracking
builder.Services.AddSingleton<OperationStateService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OperationStateService>());

// Register background services
builder.Services.AddHostedService<LogWatcherService>();
builder.Services.AddHostedService<DownloadCleanupService>();

// Add memory cache for storing stats
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<StatsCache>();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");

// Serve static files
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// Map endpoints
app.MapControllers();
app.MapHub<DownloadHub>("/hubs/downloads");

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

// Apply migrations automatically - no user interaction required
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        var dbPath = "/data/lancache.db";
        bool hasExistingDatabase = File.Exists(dbPath);
        
        if (hasExistingDatabase)
        {
            logger.LogInformation($"Found existing database at: {dbPath}");
            
            // Check if this is a pre-migration database
            bool hasMigrationsTable = false;
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "SELECT COUNT(*) FROM __EFMigrationsHistory LIMIT 1");
                hasMigrationsTable = true;
            }
            catch
            {
                hasMigrationsTable = false;
            }
            
            if (!hasMigrationsTable)
            {
                logger.LogWarning("Existing database without migrations detected. Auto-marking InitialCreate as applied...");
                
                // Create migrations history table
                await dbContext.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (
                        MigrationId TEXT NOT NULL PRIMARY KEY,
                        ProductVersion TEXT NOT NULL
                    );");
                
                // Get all migrations
                var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
                
                if (pendingMigrations.Any())
                {
                    // Mark the first migration (InitialCreate) as already applied
                    // This prevents EF from trying to recreate existing tables
                    var initialMigration = pendingMigrations.First();
                    
                    await dbContext.Database.ExecuteSqlRawAsync(
                        "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({0}, {1})",
                        initialMigration, "8.0.0");
                    
                    logger.LogInformation($"Marked '{initialMigration}' as applied to preserve existing data");
                    
                    // Re-check for any remaining migrations (like AddSteamDepotMappings if you create it)
                    pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
                }
            }
            else
            {
                // Has migrations table, check for pending migrations normally
                var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
                
                if (pendingMigrations.Any())
                {
                    logger.LogInformation($"Found {pendingMigrations.Count()} pending migrations:");
                    foreach (var migration in pendingMigrations)
                    {
                        logger.LogInformation($"  - {migration}");
                    }
                }
            }
        }
        
        // Apply any pending migrations
        var finalPendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
        if (finalPendingMigrations.Any())
        {
            logger.LogInformation("Applying pending migrations...");
            await dbContext.Database.MigrateAsync();
            logger.LogInformation("Migrations completed successfully");
        }
        else if (!hasExistingDatabase)
        {
            // No existing database, create fresh with all migrations
            logger.LogInformation("Creating new database with all migrations...");
            await dbContext.Database.MigrateAsync();
            logger.LogInformation("Database created successfully");
        }
        else
        {
            logger.LogInformation("Database is up to date");
        }
        
        // Ensure SteamDepotMappings table exists (fallback for migration issues)
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("SELECT 1 FROM SteamDepotMappings LIMIT 1");
        }
        catch
        {
            logger.LogWarning("SteamDepotMappings table missing despite migrations - creating it manually...");
            await dbContext.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS SteamDepotMappings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DepotId INTEGER NOT NULL,
                    AppId INTEGER NOT NULL,
                    AppName TEXT,
                    Source TEXT,
                    Confidence INTEGER NOT NULL,
                    DiscoveredAt TEXT NOT NULL
                );
                
                CREATE UNIQUE INDEX IF NOT EXISTS IX_SteamDepotMappings_DepotId 
                ON SteamDepotMappings (DepotId);
                
                CREATE INDEX IF NOT EXISTS IX_SteamDepotMappings_AppId 
                ON SteamDepotMappings (AppId);
            ");
            logger.LogInformation("SteamDepotMappings table created manually");
        }
        
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
        
        // In Docker, we want to fail fast if database init fails
        throw;
    }
}

app.Run();