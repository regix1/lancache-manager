using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Services;
using LancacheManager.Hubs;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://localhost:5173",
                "http://localhost:5174",
                "http://127.0.0.1:3000",
                "http://127.0.0.1:5173",
                "http://127.0.0.1:5174"
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
    
    // Also add a more permissive policy for development
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// Register PathHelperService as singleton first
builder.Services.AddSingleton<PathHelperService>();

// Configure database with cross-platform path
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    var pathHelper = serviceProvider.GetRequiredService<PathHelperService>();
    var dbPath = pathHelper.GetDatabasePath();
    
    // Ensure the directory exists
    var dbDir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
    {
        Directory.CreateDirectory(dbDir);
    }
    
    options.UseSqlite($"Data Source={dbPath}");
});

// Register other services
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddHostedService<LogWatcherService>();
builder.Services.AddHostedService<DownloadCleanupService>();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Log platform information during configuration (without building service provider)
builder.Logging.AddConsole(options => options.IncludeScopes = true);
Console.WriteLine($"Starting LancacheManager on {RuntimeInformation.OSDescription}");
Console.WriteLine($"Platform: {(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux/Unix")}");

var app = builder.Build();

// Now we can safely use the logger after the app is built
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation($"LancacheManager started on {RuntimeInformation.OSDescription}");

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // Use the more permissive CORS in development
    app.UseCors("AllowAll");
}
else
{
    app.UseCors("AllowFrontend");
}

// Serve static files (your React app)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

// Map endpoints in specific order - API routes first
app.MapControllers();
app.MapHub<DownloadHub>("/hubs/downloads");

// Simple health check endpoint
app.MapGet("/health", () => Results.Ok(new { 
    status = "healthy", 
    timestamp = DateTime.UtcNow,
    service = "LancacheManager",
    platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux"
}));

// Fallback to index.html for client-side routing - MUST BE LAST
app.MapFallback(async context =>
{
    // Don't catch API routes or other endpoints
    if (context.Request.Path.StartsWithSegments("/api") || 
        context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/hubs") ||
        context.Request.Path.StartsWithSegments("/swagger"))
    {
        context.Response.StatusCode = 404;
        return;
    }
    
    // Serve index.html for client-side routing
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
    var pathHelper = scope.ServiceProvider.GetRequiredService<PathHelperService>();
    
    dbContext.Database.EnsureCreated();
    logger.LogInformation($"Database initialized at: {pathHelper.GetDatabasePath()}");
}

app.Run();