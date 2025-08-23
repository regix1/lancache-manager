using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class SteamService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SteamService> _logger;
    private readonly SemaphoreSlim _rateLimiter;
    private readonly Dictionary<string, string> _memoryCache = new();

    public SteamService(
        IHttpClientFactory httpClientFactory, 
        IServiceProvider serviceProvider,
        ILogger<SteamService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _rateLimiter = new SemaphoreSlim(1, 1);
    }

    public async Task<string> GetAppNameAsync(string appId, string service)
    {
        if (string.IsNullOrEmpty(appId))
            return FormatServiceName(service);

        // For Steam apps, fetch from API
        if (service.ToLower() == "steam")
        {
            return await GetSteamAppNameAsync(appId);
        }

        // For other services, extract name from depot/app ID
        return ExtractGameNameFromId(service, appId);
    }

    private async Task<string> GetSteamAppNameAsync(string appId)
    {
        // Check memory cache first
        var cacheKey = $"steam:{appId}";
        if (_memoryCache.TryGetValue(cacheKey, out var cachedName))
            return cachedName;

        // Check database cache
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var steamApp = await dbContext.SteamApps.FindAsync(appId);
        if (steamApp != null && steamApp.LastUpdated > DateTime.UtcNow.AddDays(-30))
        {
            _memoryCache[cacheKey] = steamApp.Name;
            return steamApp.Name;
        }

        // Fetch from Steam API
        var name = await FetchFromSteamApiAsync(appId);
        
        // Cache the result
        if (!string.IsNullOrEmpty(name) && !name.StartsWith("Steam App"))
        {
            if (steamApp == null)
            {
                steamApp = new SteamApp { AppId = appId };
                dbContext.SteamApps.Add(steamApp);
            }
            
            steamApp.Name = name;
            steamApp.LastUpdated = DateTime.UtcNow;
            
            try
            {
                await dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cache Steam app name for {AppId}", appId);
            }
            
            _memoryCache[cacheKey] = name;
        }

        return name;
    }

    private async Task<string> FetchFromSteamApiAsync(string appId)
    {
        await _rateLimiter.WaitAsync();
        try
        {
            await Task.Delay(100); // Rate limiting

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            
            try
            {
                var response = await httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Steam API returned {StatusCode} for app {AppId}", response.StatusCode, appId);
                    return $"Steam App {appId}";
                }

                // FIX: Use Content.ReadAsStringAsync() instead of ReadAsStringAsync()
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                
                if (doc.RootElement.TryGetProperty(appId, out var appData) &&
                    appData.TryGetProperty("success", out var success) &&
                    success.GetBoolean() &&
                    appData.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("name", out var nameElement))
                {
                    var name = nameElement.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        _logger.LogInformation("Fetched name for Steam app {AppId}: {Name}", appId, name);
                        return name;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Timeout fetching Steam app {AppId}", appId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Steam app {AppId}", appId);
            }

            return $"Steam App {appId}";
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private string ExtractGameNameFromId(string service, string depotId)
    {
        // Try to extract a readable name from the depot/app ID
        var parts = depotId.Split(new[] { '/', '\\', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length > 0)
        {
            // Clean up the first meaningful part
            var gameName = parts[0];
            
            // Remove common prefixes/suffixes
            gameName = Regex.Replace(gameName, @"^(game|app|depot|product|client)", "", RegexOptions.IgnoreCase);
            gameName = Regex.Replace(gameName, @"(game|app|depot|product|client)$", "", RegexOptions.IgnoreCase);
            
            // Convert from various naming conventions to readable format
            gameName = ConvertToReadableName(gameName);
            
            if (!string.IsNullOrWhiteSpace(gameName))
            {
                return $"{FormatServiceName(service)}: {gameName}";
            }
        }

        return $"{FormatServiceName(service)} Game";
    }

    private string ConvertToReadableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        // Handle camelCase and PascalCase
        name = Regex.Replace(name, @"([a-z])([A-Z])", "$1 $2");
        name = Regex.Replace(name, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
        
        // Handle snake_case and kebab-case
        name = name.Replace('_', ' ').Replace('-', ' ');
        
        // Handle numbers
        name = Regex.Replace(name, @"(\d+)", " $1 ");
        
        // Clean up multiple spaces
        name = Regex.Replace(name, @"\s+", " ");
        
        // Capitalize first letter of each word
        var words = name.Trim().Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            if (!string.IsNullOrEmpty(words[i]))
            {
                words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
            }
        }
        
        return string.Join(" ", words);
    }

    private string FormatServiceName(string service)
    {
        return service.ToLower() switch
        {
            "steam" => "Steam",
            "epic" => "Epic Games",
            "origin" => "Origin/EA",
            "blizzard" => "Battle.net",
            "uplay" => "Ubisoft Connect",
            "riot" => "Riot Games",
            "wsus" => "Windows Update",
            "apple" => "Apple",
            "xboxlive" => "Xbox",
            _ => ConvertToReadableName(service)
        };
    }
}