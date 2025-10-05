using LancacheManager.Data;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace LancacheManager.Services;

/// <summary>
/// Service for managing PICS depot mapping data in JSON format
/// </summary>
public class PicsDataService
{
    private readonly ILogger<PicsDataService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly string _picsJsonFile;
    private readonly object _fileLock = new object();

    public PicsDataService(ILogger<PicsDataService> logger, IServiceScopeFactory scopeFactory, IPathResolver pathResolver, StateService stateService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _picsJsonFile = Path.Combine(_pathResolver.GetDataDirectory(), "pics_depot_mappings.json");
    }

    /// <summary>
    /// Save PICS depot mappings to JSON file
    /// </summary>
    public Task SavePicsDataToJsonAsync(Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames, uint lastChangeNumber = 0)
    {
        try
        {
            var picsData = new PicsJsonData
            {
                Metadata = new PicsMetadata
                {
                    LastUpdated = DateTime.UtcNow,
                    TotalMappings = depotMappings.Sum(kvp => kvp.Value.Count),
                    Version = "1.0",
                    NextUpdateDue = DateTime.UtcNow.AddHours(24),
                    LastChangeNumber = lastChangeNumber
                },
                DepotMappings = new Dictionary<string, PicsDepotMapping>()
            };

            foreach (var (depotId, appIds) in depotMappings)
            {
                var appIdsList = appIds.ToList();
                var appNamesList = appIdsList.Select(appId =>
                    appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
                ).ToList();

                picsData.DepotMappings[depotId.ToString()] = new PicsDepotMapping
                {
                    AppIds = appIdsList,
                    AppNames = appNamesList,
                    Source = "SteamKit2-PICS",
                    DiscoveredAt = DateTime.UtcNow
                };
            }

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var jsonContent = JsonSerializer.Serialize(picsData, jsonOptions);

            // Ensure data directory exists
            var dataDir = Path.GetDirectoryName(_picsJsonFile);
            if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            lock (_fileLock)
            {
                File.WriteAllText(_picsJsonFile, jsonContent);
            }

            _logger.LogInformation($"Saved {picsData.Metadata.TotalMappings} PICS depot mappings to JSON file: {_picsJsonFile}");

            // Update state to indicate data is loaded
            _stateService.SetDataLoaded(true, picsData.Metadata.TotalMappings);

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving PICS data to JSON file");
            return Task.FromException(ex);
        }
    }

    /// <summary>
    /// Merge incremental PICS depot mappings into existing JSON file with validation
    /// </summary>
    public async Task MergePicsDataToJsonAsync(Dictionary<uint, HashSet<uint>> newDepotMappings, Dictionary<uint, string> appNames, uint lastChangeNumber = 0, bool validateExisting = true)
    {
        try
        {
            // Load existing data or create new if doesn't exist
            var existingData = await LoadPicsDataFromJsonAsync() ?? new PicsJsonData
            {
                Metadata = new PicsMetadata
                {
                    LastUpdated = DateTime.MinValue,
                    TotalMappings = 0,
                    Version = "1.0",
                    NextUpdateDue = DateTime.UtcNow.AddHours(24),
                    LastChangeNumber = 0
                },
                DepotMappings = new Dictionary<string, PicsDepotMapping>()
            };

            var updatedCount = 0;
            var newCount = 0;
            var validatedCount = 0;
            var removedCount = 0;

            // Validate existing entries if requested
            if (validateExisting && existingData.DepotMappings != null)
            {
                var keysToRemove = new List<string>();
                foreach (var (depotKey, mapping) in existingData.DepotMappings)
                {
                    // Check for corrupted entries
                    if (mapping?.AppIds == null || mapping.AppIds.Count == 0 ||
                        mapping.AppNames == null || mapping.AppNames.Count != mapping.AppIds.Count)
                    {
                        keysToRemove.Add(depotKey);
                        _logger.LogWarning($"Removing corrupted depot mapping: {depotKey}");
                    }
                    validatedCount++;
                }

                foreach (var key in keysToRemove)
                {
                    existingData.DepotMappings.Remove(key);
                    removedCount++;
                }
            }

            // Merge new depot mappings with existing ones
            foreach (var (depotId, appIds) in newDepotMappings)
            {
                var depotKey = depotId.ToString();
                var appIdsList = appIds.ToList();
                var appNamesList = appIdsList.Select(appId =>
                    appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
                ).ToList();

                var newMapping = new PicsDepotMapping
                {
                    AppIds = appIdsList,
                    AppNames = appNamesList,
                    Source = "SteamKit2-PICS",
                    DiscoveredAt = DateTime.UtcNow
                };

                if (existingData.DepotMappings.ContainsKey(depotKey))
                {
                    existingData.DepotMappings[depotKey] = newMapping;
                    updatedCount++;
                }
                else
                {
                    existingData.DepotMappings[depotKey] = newMapping;
                    newCount++;
                }
            }

            // Update metadata
            existingData.Metadata.LastUpdated = DateTime.UtcNow;
            existingData.Metadata.TotalMappings = existingData.DepotMappings.Sum(kvp => kvp.Value.AppIds.Count);
            existingData.Metadata.NextUpdateDue = DateTime.UtcNow.AddHours(24);
            existingData.Metadata.LastChangeNumber = lastChangeNumber;

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var jsonContent = JsonSerializer.Serialize(existingData, jsonOptions);

            // Ensure data directory exists
            var dataDir = Path.GetDirectoryName(_picsJsonFile);
            if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            lock (_fileLock)
            {
                File.WriteAllText(_picsJsonFile, jsonContent);
            }

            _logger.LogInformation(
                "Incrementally updated PICS JSON: {NewCount} new, {UpdatedCount} updated, {RemovedCount} removed corrupted, {TotalMappingPairs} total app mappings (shared depots included)",
                newCount,
                updatedCount,
                removedCount,
                existingData.Metadata.TotalMappings);

            // Update state to indicate data is loaded with new count
            _stateService.SetDataLoaded(true, existingData.Metadata.TotalMappings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error merging PICS data to JSON file");
            throw;
        }
    }

    /// <summary>
    /// Load PICS depot mappings from JSON file
    /// </summary>
    public async Task<PicsJsonData?> LoadPicsDataFromJsonAsync()
    {
        try
        {
            if (!File.Exists(_picsJsonFile))
            {
                _logger.LogDebug("PICS JSON file not found: {FilePath}", _picsJsonFile);
                return null;
            }

            string jsonContent;
            lock (_fileLock)
            {
                jsonContent = File.ReadAllText(_picsJsonFile);
            }

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                _logger.LogWarning("PICS JSON file is empty");
                return null;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var picsData = JsonSerializer.Deserialize<PicsJsonData>(jsonContent, jsonOptions);

            if (picsData != null)
            {
                _logger.LogDebug($"Loaded PICS data with {picsData.Metadata?.TotalMappings ?? 0} mappings from JSON file");

                // Update state to indicate data is loaded
                if (picsData.Metadata?.TotalMappings > 0)
                {
                    _stateService.SetDataLoaded(true, picsData.Metadata.TotalMappings);
                }
            }

            return picsData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading PICS data from JSON file");
            return null;
        }
    }

    /// <summary>
    /// Check if PICS JSON data needs updating (always use incremental)
    /// </summary>
    public async Task<bool> NeedsUpdateAsync()
    {
        try
        {
            var picsData = await LoadPicsDataFromJsonAsync();
            if (picsData?.Metadata == null)
            {
                return true; // No data exists, needs initial update
            }

            // Always return false to use incremental updates only
            // The incremental system will handle new items and updates
            _logger.LogDebug($"PICS data exists with {picsData.Metadata.TotalMappings} mappings, using incremental updates only");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking if PICS data needs update, assuming it does");
            return true;
        }
    }

    /// <summary>
    /// Get app IDs for a depot from JSON data
    /// </summary>
    public async Task<List<uint>> GetAppIdsForDepotFromJsonAsync(uint depotId)
    {
        try
        {
            var picsData = await LoadPicsDataFromJsonAsync();
            if (picsData?.DepotMappings == null)
            {
                return new List<uint>();
            }

            var depotKey = depotId.ToString();
            if (picsData.DepotMappings.TryGetValue(depotKey, out var mapping))
            {
                return mapping.AppIds ?? new List<uint>();
            }

            return new List<uint>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error getting app IDs for depot {depotId} from JSON");
            return new List<uint>();
        }
    }

    /// <summary>
    /// Import PICS data from JSON file to database
    /// </summary>
    public async Task ImportJsonDataToDatabaseAsync()
    {
        await ImportJsonDataToDatabaseAsync(CancellationToken.None);
    }

    /// <summary>
    /// Import PICS data from JSON file to database with cancellation support
    /// </summary>
    public async Task ImportJsonDataToDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var picsData = await LoadPicsDataFromJsonAsync();
            if (picsData?.DepotMappings == null)
            {
                _logger.LogWarning("No PICS JSON data to import");
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var mappingsToImport = new List<SteamDepotMapping>();
            var processedCount = 0;

            foreach (var (depotIdStr, mapping) in picsData.DepotMappings)
            {
                if (!uint.TryParse(depotIdStr, out var depotId))
                {
                    continue;
                }

                if (mapping.AppIds != null)
                {
                    for (int i = 0; i < mapping.AppIds.Count; i++)
                    {
                        var appId = mapping.AppIds[i];
                        var appName = mapping.AppNames?.ElementAtOrDefault(i) ?? $"App {appId}";

                        mappingsToImport.Add(new SteamDepotMapping
                        {
                            DepotId = depotId,
                            AppId = appId,
                            AppName = appName,
                            Source = mapping.Source ?? "JSON-Import",
                            DiscoveredAt = mapping.DiscoveredAt
                        });

                        processedCount++;
                        // Yield every 1000 records to prevent blocking
                        if (processedCount % 1000 == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await Task.Yield();
                        }
                    }
                }
            }

            // Get existing mappings to avoid duplicates
            var depotIds = mappingsToImport.Select(m => m.DepotId).Distinct().ToList();
            var existingMappings = await context.SteamDepotMappings
                .Where(m => depotIds.Contains(m.DepotId))
                .ToDictionaryAsync(m => $"{m.DepotId}_{m.AppId}");

            var newMappings = new List<SteamDepotMapping>();
            int updated = 0;
            int comparedCount = 0;

            foreach (var mapping in mappingsToImport)
            {
                var key = $"{mapping.DepotId}_{mapping.AppId}";
                if (existingMappings.TryGetValue(key, out var existing))
                {
                    // Update existing mapping if JSON data is newer
                    if (mapping.DiscoveredAt > existing.DiscoveredAt && existing.Source != "SteamKit2-PICS")
                    {
                        existing.AppName = mapping.AppName;
                        existing.Source = GetCombinedSource(existing.Source, mapping.Source);
                        existing.DiscoveredAt = mapping.DiscoveredAt;
                        updated++;
                    }
                }
                else
                {
                    newMappings.Add(mapping);
                }

                comparedCount++;
                // Yield every 1000 comparisons to prevent blocking
                if (comparedCount % 1000 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
            }

            // Batch insert new mappings to avoid huge single transactions
            const int batchSize = 5000;
            for (int i = 0; i < newMappings.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = newMappings.Skip(i).Take(batchSize).ToList();

                try
                {
                    await context.SteamDepotMappings.AddRangeAsync(batch, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                    _logger.LogDebug($"Imported batch {i / batchSize + 1}/{(newMappings.Count + batchSize - 1) / batchSize} ({batch.Count} mappings)");
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 19)
                {
                    // UNIQUE constraint violation - duplicates already exist, this is fine
                    // This can happen if the import is called multiple times or run concurrently
                    _logger.LogDebug($"Batch {i / batchSize + 1} contains duplicate mappings (already in database) - skipping");

                    // Clear the context to avoid tracking issues
                    context.ChangeTracker.Clear();
                }

                // Yield after each batch
                await Task.Yield();
            }

            // Save any remaining updates
            if (updated > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation($"Imported PICS data: {newMappings.Count} new mappings, {updated} updated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing PICS JSON data to database");
            throw;
        }
    }

    /// <summary>
    /// Get file path for PICS JSON file
    /// </summary>
    public string GetPicsJsonFilePath() => _picsJsonFile;

    /// <summary>
    /// Combine source information for depot mappings
    /// </summary>
    private string GetCombinedSource(string existingSource, string newSource)
    {
        if (existingSource == newSource)
            return existingSource;

        if (newSource == "SteamKit2-PICS")
            return "SteamKit2-PICS";

        if (existingSource == "PatternMatching" && newSource == "JSON-Import")
            return "PatternMatching+JSON";

        return existingSource;
    }
}

/// <summary>
/// Root structure for PICS JSON data
/// </summary>
public class PicsJsonData
{
    public PicsMetadata? Metadata { get; set; }
    public Dictionary<string, PicsDepotMapping>? DepotMappings { get; set; }
}

/// <summary>
/// Metadata for PICS JSON file
/// </summary>
public class PicsMetadata
{
    public DateTime LastUpdated { get; set; }
    public int TotalMappings { get; set; }
    public string Version { get; set; } = "1.0";
    public DateTime NextUpdateDue { get; set; }
    public uint LastChangeNumber { get; set; }   // NEW: Track PICS changelist position
}

/// <summary>
/// Depot mapping data in JSON format
/// </summary>
public class PicsDepotMapping
{
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
