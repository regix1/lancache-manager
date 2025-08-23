using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "LanCache Manager API", Version = "v1" });
});

// Add SignalR
builder.Services.AddSignalR();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy =>
        {
            policy.WithOrigins(
                    "http://localhost:3000",
                    "http://localhost:5173",
                    "http://localhost:5174"
                )
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
});

// Add Database
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
        ?? "Data Source=lancache.db;";
    options.UseSqlite(connectionString);
});

// Add HttpClient for external API calls
builder.Services.AddHttpClient();

// Register Services
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddScoped<DatabaseService>();
builder.Services.AddScoped<SteamService>();

// Register Background Services
builder.Services.AddHostedService<LogWatcherService>();
builder.Services.AddHostedService<DownloadCleanupService>();

// Conditionally add sample log generator for development
if (builder.Configuration.GetValue<bool>("LanCache:GenerateSampleLogs", false))
{
    builder.Services.AddHostedService<SampleLogGeneratorService>();
}

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "LanCache Manager API v1");
        c.RoutePrefix = "swagger";
    });
}

// Use CORS
app.UseCors("AllowReactApp");

// Map controllers
app.MapControllers();

// Map SignalR hub
app.MapHub<DownloadHub>("/hubs/downloads");

// Create database on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Database initialized at: {Path}", dbContext.Database.GetDbConnection().DataSource);
}

// Add health check endpoint
app.MapGet("/health", () => Results.Ok(new 
{ 
    status = "healthy", 
    timestamp = DateTime.UtcNow 
}));

// Add root endpoint
app.MapGet("/", () => Results.Ok(new
{
    name = "LanCache Manager API",
    version = "1.0.0",
    endpoints = new
    {
        swagger = "/swagger",
        health = "/health",
        downloads = "/api/downloads",
        games = "/api/games",
        stats = "/api/stats",
        management = "/api/management",
        signalr = "/hubs/downloads"
    }
}));

app.Run();