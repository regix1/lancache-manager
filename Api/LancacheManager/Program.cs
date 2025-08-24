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

// Simple database configuration - NO POOLING, NO OPTIMIZATIONS
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var dbPath = "/data/lancache.db";
    
    // Ensure the directory exists
    if (!Directory.Exists("/data"))
    {
        Directory.CreateDirectory("/data");
    }
    
    // Simple connection string
    options.UseSqlite($"Data Source={dbPath}");
});

// Register services
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddSingleton<CacheManagementService>();
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

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    
    try
    {
        await dbContext.Database.EnsureCreatedAsync();
        logger.LogInformation($"Database initialized at: /data/lancache.db");
        
        // Load initial stats into cache
        var statsCache = scope.ServiceProvider.GetRequiredService<StatsCache>();
        await statsCache.RefreshFromDatabase(dbContext);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to initialize database");
    }
}

app.Run();