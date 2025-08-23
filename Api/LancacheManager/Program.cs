using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Add Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add HttpClient
builder.Services.AddHttpClient();

// Register Services
builder.Services.AddSingleton<LogParserService>();
builder.Services.AddSingleton<CacheManagementService>();
builder.Services.AddScoped<DatabaseService>();

// Register Background Services
builder.Services.AddHostedService<LogWatcherService>();
builder.Services.AddHostedService<DownloadCleanupService>();

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHub<DownloadHub>("/downloadHub");

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();