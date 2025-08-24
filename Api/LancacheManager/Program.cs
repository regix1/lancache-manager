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

// Add response caching
builder.Services.AddResponseCaching();

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

// Configure database with connection pooling
builder.Services.AddDbContextPool<AppDbContext>(options =>
{
    var dbPath = "/data/lancache.db";
    
    // Ensure the directory exists
    if (!Directory.Exists("/data"))
    {
        Directory.CreateDirectory("/data");
    }
    
    // Use connection string WITHOUT Journal Mode (will be set as PRAGMA)
    var connectionString = $"Data Source={dbPath};" +
        "Cache=Shared;" +
        "Mode=ReadWriteCreate;";
    
    options.UseSqlite(connectionString);
}, poolSize: 128);

// Register services
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddHostedService<LogWatcherService>();
builder.Services.AddHostedService<DownloadCleanupService>();

// Add memory cache for API responses
builder.Services.AddMemoryCache();

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

// Use response caching
app.UseResponseCaching();

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

// Ensure database is created with optimizations
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        // Create database if it doesn't exist
        await dbContext.Database.EnsureCreatedAsync();
        
        // Apply SQLite performance optimizations via PRAGMA commands
        using (var connection = dbContext.Database.GetDbConnection())
        {
            await connection.OpenAsync();
            using (var command = connection.CreateCommand())
            {
                // Set WAL mode for better concurrency
                command.CommandText = "PRAGMA journal_mode=WAL";
                await command.ExecuteNonQueryAsync();
                
                // Other performance optimizations
                command.CommandText = "PRAGMA synchronous=NORMAL";
                await command.ExecuteNonQueryAsync();
                
                command.CommandText = "PRAGMA cache_size=10000";
                await command.ExecuteNonQueryAsync();
                
                command.CommandText = "PRAGMA temp_store=MEMORY";
                await command.ExecuteNonQueryAsync();
                
                command.CommandText = "PRAGMA mmap_size=30000000000";
                await command.ExecuteNonQueryAsync();
            }
        }
        
        logger.LogInformation($"Database initialized with performance optimizations at: /data/lancache.db");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize database");
    }
}

app.Run();