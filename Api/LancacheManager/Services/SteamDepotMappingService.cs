using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class SteamDepotMappingService : IHostedService
{
    private readonly ILogger<SteamDepotMappingService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<uint, uint> _depotToAppMap = new();
    private readonly ConcurrentDictionary<uint, SteamAppInfo> _appInfoCache = new();
    private readonly string _cacheFilePath = "/data/steam_depot_mappings.json";
    private readonly string _appListCachePath = "/data/steam_applist.json";
    private Timer? _updateTimer;
    private bool _isInitialized = false;

    public SteamDepotMappingService(
        ILogger<SteamDepotMappingService> logger,
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public class SteamAppInfo
    {
        public uint AppId { get; set; }
        public string Name { get; set; } = "Unknown";
        public List<uint> Depots { get; set; } = new();
        public DateTime CachedAt { get; set; } = DateTime.UtcNow;
    }

    public class SteamAppListResponse
    {
        public AppList? applist { get; set; }
    }

    public class AppList
    {
        public List<AppItem>? apps { get; set; }
    }

    public class AppItem
    {
        public uint appid { get; set; }
        public string? name { get; set; }
    }

    public class CachedMappings
    {
        public Dictionary<uint, uint> DepotToApp { get; set; } = new();
        public Dictionary<uint, SteamAppInfo> AppInfo { get; set; } = new();
        public DateTime LastUpdated { get; set; }
        public int Version { get; set; } = 1;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Steam Depot Mapping Service");
        
        _ = Task.Run(async () => await InitializeAsync(cancellationToken), cancellationToken);
        
        _updateTimer = new Timer(
            async _ => await UpdateMappingsAsync(),
            null,
            TimeSpan.FromHours(24),
            TimeSpan.FromHours(24)
        );
        
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _updateTimer?.Dispose();
        return SaveCachedMappings();
    }

    public uint? GetAppIdFromDepot(uint depotId)
    {
        if (_depotToAppMap.TryGetValue(depotId, out var appId))
        {
            return appId;
        }
        
        _ = Task.Run(async () => await TryLoadDepotFromDatabase(depotId));
        
        var guessed = GuessAppIdFromDepot(depotId);
        if (guessed.HasValue)
        {
            _ = Task.Run(async () => await SaveDepotMapping(depotId, guessed.Value, "pattern", 30));
            _depotToAppMap.TryAdd(depotId, guessed.Value);
            return guessed;
        }
        
        return null;
    }

    public async Task AddObservedMapping(uint depotId, uint appId, string source = "observed")
    {
        _depotToAppMap[depotId] = appId;
        
        if (_appInfoCache.TryGetValue(appId, out var appInfo))
        {
            if (!appInfo.Depots.Contains(depotId))
            {
                appInfo.Depots.Add(depotId);
            }
        }
        
        await SaveDepotMapping(depotId, appId, source, 90);
    }

    public SteamAppInfo? GetAppInfo(uint appId)
    {
        if (_appInfoCache.TryGetValue(appId, out var appInfo))
        {
            return appInfo;
        }
        return null;
    }

    public bool IsReady => _isInitialized && _depotToAppMap.Count > 0;

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadCachedMappings();
            await LoadFromDatabase();
            await FetchSteamAppList();
            
            if (_depotToAppMap.Count < 1000)
            {
                await BuildCommonMappings();
            }
            
            await AnalyzeRecentDownloads();
            
            _isInitialized = true;
            _logger.LogInformation($"Initialized with {_depotToAppMap.Count} depot mappings and {_appInfoCache.Count} apps");
            
            await SaveCachedMappings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing Steam depot mappings");
            _isInitialized = true;
        }
    }

    private async Task LoadFromDatabase()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            try
            {
                var mappings = await context.SteamDepotMappings
                    .Where(m => m.Confidence > 20)
                    .ToListAsync();
                
                foreach (var mapping in mappings)
                {
                    _depotToAppMap[mapping.DepotId] = mapping.AppId;
                    
                    if (!string.IsNullOrEmpty(mapping.AppName))
                    {
                        if (!_appInfoCache.ContainsKey(mapping.AppId))
                        {
                            _appInfoCache[mapping.AppId] = new SteamAppInfo
                            {
                                AppId = mapping.AppId,
                                Name = mapping.AppName
                            };
                        }
                        _appInfoCache[mapping.AppId].Depots.Add(mapping.DepotId);
                    }
                }
                
                _logger.LogInformation($"Loaded {mappings.Count} depot mappings from database");
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Depot mappings table not available: {Message}", ex.Message);
            }
            
            var knownMappings = await context.Downloads
                .Where(d => d.GameAppId.HasValue && !string.IsNullOrEmpty(d.LastUrl))
                .Select(d => new { d.GameAppId, d.GameName, d.LastUrl })
                .Distinct()
                .Take(5000)
                .ToListAsync();
            
            foreach (var mapping in knownMappings)
            {
                if (mapping.GameAppId.HasValue && !string.IsNullOrEmpty(mapping.LastUrl))
                {
                    var depotMatch = System.Text.RegularExpressions.Regex.Match(
                        mapping.LastUrl, @"/depot/(\d+)/");
                    
                    if (depotMatch.Success && uint.TryParse(depotMatch.Groups[1].Value, out var depotId))
                    {
                        _depotToAppMap[depotId] = mapping.GameAppId.Value;
                        
                        if (!_appInfoCache.ContainsKey(mapping.GameAppId.Value))
                        {
                            _appInfoCache[mapping.GameAppId.Value] = new SteamAppInfo
                            {
                                AppId = mapping.GameAppId.Value,
                                Name = mapping.GameName ?? $"App {mapping.GameAppId.Value}"
                            };
                        }
                        _appInfoCache[mapping.GameAppId.Value].Depots.Add(depotId);
                    }
                }
            }
            
            _logger.LogInformation($"Analyzed {knownMappings.Count} downloads for depot patterns");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading mappings from database");
        }
    }

    private async Task TryLoadDepotFromDatabase(uint depotId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            var mapping = await context.SteamDepotMappings
                .FirstOrDefaultAsync(m => m.DepotId == depotId);
                
            if (mapping != null)
            {
                _depotToAppMap[depotId] = mapping.AppId;
                _logger.LogDebug($"Found depot {depotId} -> app {mapping.AppId} in database");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error loading depot {DepotId} from database: {Message}", depotId, ex.Message);
        }
    }

    private async Task SaveDepotMapping(uint depotId, uint appId, string source, int confidence)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            var existing = await context.SteamDepotMappings
                .FirstOrDefaultAsync(m => m.DepotId == depotId);
            
            if (existing == null)
            {
                var appName = _appInfoCache.TryGetValue(appId, out var appInfo) 
                    ? appInfo.Name 
                    : null;
                    
                context.SteamDepotMappings.Add(new SteamDepotMapping
                {
                    DepotId = depotId,
                    AppId = appId,
                    AppName = appName,
                    Source = source,
                    Confidence = confidence,
                    DiscoveredAt = DateTime.UtcNow
                });
                
                await context.SaveChangesAsync();
                _logger.LogDebug($"Saved new depot mapping: {depotId} -> {appId} ({source})");
            }
            else if (existing.Confidence < confidence)
            {
                existing.AppId = appId;
                existing.Source = source;
                existing.Confidence = confidence;
                existing.DiscoveredAt = DateTime.UtcNow;
                
                await context.SaveChangesAsync();
                _logger.LogDebug($"Updated depot mapping: {depotId} -> {appId} ({source})");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error saving depot mapping: {Message}", ex.Message);
        }
    }

    private async Task AnalyzeRecentDownloads()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            var recentIdentified = await context.Downloads
                .Where(d => d.GameAppId.HasValue && 
                           !string.IsNullOrEmpty(d.GameName) && 
                           d.GameName != "Unknown Steam Game" &&
                           !string.IsNullOrEmpty(d.LastUrl))
                .OrderByDescending(d => d.StartTime)
                .Take(1000)
                .Select(d => new { d.GameAppId, d.GameName, d.LastUrl })
                .ToListAsync();
            
            int newMappings = 0;
            foreach (var download in recentIdentified)
            {
                var depotMatch = System.Text.RegularExpressions.Regex.Match(
                    download.LastUrl, @"/depot/(\d+)/");
                
                if (depotMatch.Success && 
                    uint.TryParse(depotMatch.Groups[1].Value, out var depotId) &&
                    download.GameAppId.HasValue)
                {
                    if (!_depotToAppMap.ContainsKey(depotId))
                    {
                        _depotToAppMap[depotId] = download.GameAppId.Value;
                        await SaveDepotMapping(depotId, download.GameAppId.Value, "analyzed", 80);
                        newMappings++;
                    }
                }
            }
            
            if (newMappings > 0)
            {
                _logger.LogInformation($"Discovered {newMappings} new depot mappings from recent downloads");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing recent downloads");
        }
    }

    private async Task FetchSteamAppList()
    {
        try
        {
            if (File.Exists(_appListCachePath))
            {
                var fileInfo = new FileInfo(_appListCachePath);
                if (fileInfo.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7))
                {
                    var cachedJson = await File.ReadAllTextAsync(_appListCachePath);
                    var cachedData = JsonSerializer.Deserialize<SteamAppListResponse>(cachedJson);
                    if (cachedData?.applist?.apps != null)
                    {
                        foreach (var app in cachedData.applist.apps)
                        {
                            _appInfoCache[app.appid] = new SteamAppInfo
                            {
                                AppId = app.appid,
                                Name = app.name ?? "Unknown"
                            };
                        }
                        _logger.LogInformation($"Loaded {_appInfoCache.Count} apps from cache");
                        return;
                    }
                }
            }

            _logger.LogInformation("Fetching app list from Steam API");
            var response = await _httpClient.GetAsync("https://api.steampowered.com/ISteamApps/GetAppList/v2/");
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                
                await File.WriteAllTextAsync(_appListCachePath, json);
                
                var data = JsonSerializer.Deserialize<SteamAppListResponse>(json);
                if (data?.applist?.apps != null)
                {
                    foreach (var app in data.applist.apps)
                    {
                        _appInfoCache[app.appid] = new SteamAppInfo
                        {
                            AppId = app.appid,
                            Name = app.name ?? "Unknown"
                        };
                    }
                    _logger.LogInformation($"Fetched {_appInfoCache.Count} apps from Steam API");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Steam app list");
        }
    }

    private async Task BuildCommonMappings()
    {
        foreach (var app in _appInfoCache.Values.Take(1000))
        {
            for (uint offset = 1; offset <= 10; offset++)
            {
                var depotId = app.AppId + offset;
                
                if (depotId < app.AppId + 100)
                {
                    _depotToAppMap.TryAdd(depotId, app.AppId);
                    app.Depots.Add(depotId);
                }
            }
        }
        
        _logger.LogInformation($"Built {_depotToAppMap.Count} depot mappings from patterns");
    }

    private async Task UpdateMappingsAsync()
    {
        try
        {
            _logger.LogInformation("Updating Steam depot mappings");
            
            await FetchSteamAppList();
            await LoadFromDatabase();
            await AnalyzeRecentDownloads();
            await SaveCachedMappings();
            
            _logger.LogInformation($"Updated mappings: {_depotToAppMap.Count} depot mappings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating depot mappings");
        }
    }

    private async Task LoadCachedMappings()
    {
        try
        {
            if (File.Exists(_cacheFilePath))
            {
                var json = await File.ReadAllTextAsync(_cacheFilePath);
                var cached = JsonSerializer.Deserialize<CachedMappings>(json);
                
                if (cached != null && cached.LastUpdated > DateTime.UtcNow.AddDays(-7))
                {
                    foreach (var kvp in cached.DepotToApp)
                    {
                        _depotToAppMap[kvp.Key] = kvp.Value;
                    }
                    
                    foreach (var kvp in cached.AppInfo)
                    {
                        _appInfoCache[kvp.Key] = kvp.Value;
                    }
                    
                    _logger.LogInformation($"Loaded {_depotToAppMap.Count} cached depot mappings");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading cached mappings");
        }
    }

    private async Task SaveCachedMappings()
    {
        try
        {
            var cache = new CachedMappings
            {
                DepotToApp = _depotToAppMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AppInfo = _appInfoCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                LastUpdated = DateTime.UtcNow
            };
            
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            
            await File.WriteAllTextAsync(_cacheFilePath, json);
            _logger.LogDebug("Saved depot mappings to cache");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving cached mappings");
        }
    }

    private uint? GuessAppIdFromDepot(uint depotId)
    {
        for (uint offset = 1; offset <= 20; offset++)
        {
            if (depotId > offset)
            {
                var possibleAppId = depotId - offset;
                
                if (_appInfoCache.ContainsKey(possibleAppId))
                {
                    return possibleAppId;
                }
                
                if (possibleAppId % 10 == 0 || possibleAppId < 1000000)
                {
                    return possibleAppId;
                }
            }
        }
        
        return null;
    }
}