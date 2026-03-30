using System.Text.Json;
using LancacheManager.Extensions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Npgsql;
using NpgsqlTypes;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

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
        _picsJsonFile = Path.Combine(_pathResolver.GetPicsDirectory(), "pics_depot_mappings.json");
    }

    /// <summary>
    /// Save PICS depot mappings to JSON file
    /// </summary>
    public Task SavePicsDataToJsonAsync(Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames, uint lastChangeNumber = 0, Dictionary<uint, uint>? depotOwners = null, Dictionary<uint, string>? depotNames = null)
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
                // Ensure owner app is first in the list
                var appIdsList = new List<uint>();
                uint? ownerId = null;

                if (depotOwners != null && depotOwners.TryGetValue(depotId, out var ownerAppId) && appIds.Contains(ownerAppId))
                {
                    ownerId = ownerAppId;
                    // Add owner first
                    appIdsList.Add(ownerAppId);
                    // Add remaining apps
                    appIdsList.AddRange(appIds.Where(id => id != ownerAppId));
                }
                else
                {
                    // No owner tracked, just convert as-is
                    appIdsList = appIds.ToList();
                }

                var appNamesList = appIdsList.Select(appId =>
                    appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
                ).ToList();

                // Get depot name if available
                string? depotName = null;
                depotNames?.TryGetValue(depotId, out depotName);

                picsData.DepotMappings[depotId.ToString()] = new PicsDepotMapping
                {
                    OwnerId = ownerId,
                    DepotName = depotName,
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

            // Clear cache so next load reads the new file
            ClearCache();

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
    public async Task MergePicsDataToJsonAsync(Dictionary<uint, HashSet<uint>> newDepotMappings, Dictionary<uint, string> appNames, uint lastChangeNumber = 0, bool validateExisting = true, Dictionary<uint, uint>? depotOwners = null, Dictionary<uint, string>? depotNames = null)
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

                // Ensure owner app is first in the list
                var appIdsList = new List<uint>();
                uint? ownerId = null;

                if (depotOwners != null && depotOwners.TryGetValue(depotId, out var ownerAppId) && appIds.Contains(ownerAppId))
                {
                    ownerId = ownerAppId;
                    // Add owner first
                    appIdsList.Add(ownerAppId);
                    // Add remaining apps
                    appIdsList.AddRange(appIds.Where(id => id != ownerAppId));
                }
                else
                {
                    // No owner tracked, just convert as-is
                    appIdsList = appIds.ToList();
                }

                var appNamesList = appIdsList.Select(appId =>
                    appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
                ).ToList();

                // Get depot name if available
                string? depotName = null;
                depotNames?.TryGetValue(depotId, out depotName);

                var newMapping = new PicsDepotMapping
                {
                    OwnerId = ownerId,
                    DepotName = depotName,
                    AppIds = appIdsList,
                    AppNames = appNamesList,
                    Source = "SteamKit2-PICS",
                    DiscoveredAt = DateTime.UtcNow
                };

                if (existingData.DepotMappings?.ContainsKey(depotKey) == true)
                {
                    existingData.DepotMappings[depotKey] = newMapping;
                    updatedCount++;
                }
                else
                {
                    if (existingData.DepotMappings == null)
                    {
                        existingData.DepotMappings = new Dictionary<string, PicsDepotMapping>();
                    }
                    existingData.DepotMappings[depotKey] = newMapping;
                    newCount++;
                }
            }

            // Update metadata
            if (existingData.Metadata == null)
            {
                existingData.Metadata = new PicsMetadata();
            }
            existingData.Metadata.LastUpdated = DateTime.UtcNow;
            existingData.Metadata.TotalMappings = existingData.DepotMappings?.Sum(kvp => kvp.Value.AppIds?.Count ?? 0) ?? 0;
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

            // Clear cache so next load reads the new file
            ClearCache();

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
    // Cache for loaded PICS data to avoid repeated deserialization of 73MB+ file
    private PicsJsonData? _cachedPicsData = null;
    private DateTime _cacheLastLoaded = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

    public Task<PicsJsonData?> LoadPicsDataFromJsonAsync()
    {
        try
        {
            if (!File.Exists(_picsJsonFile))
            {
                return Task.FromResult<PicsJsonData?>(null);
            }

            // Return cached data if still valid
            if (_cachedPicsData != null && (DateTime.UtcNow - _cacheLastLoaded) < _cacheExpiration)
            {
                return Task.FromResult<PicsJsonData?>(_cachedPicsData);
            }

            string jsonContent;
            lock (_fileLock)
            {
                jsonContent = File.ReadAllText(_picsJsonFile);
            }

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                _logger.LogWarning("PICS JSON file is empty");
                return Task.FromResult<PicsJsonData?>(null);
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var picsData = JsonSerializer.Deserialize<PicsJsonData>(jsonContent, jsonOptions);

            // Cache the loaded data
            if (picsData != null)
            {
                _cachedPicsData = picsData;
                _cacheLastLoaded = DateTime.UtcNow;
                _logger.LogInformation("PICS data loaded and cached ({TotalMappings} mappings)", picsData.Metadata?.TotalMappings ?? 0);

                // Update state to indicate data is loaded
                if (picsData.Metadata?.TotalMappings > 0)
                {
                    _stateService.SetDataLoaded(true, picsData.Metadata.TotalMappings);
                }
            }

            return Task.FromResult(picsData);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "PICS JSON file is corrupted (truncated or malformed). Deleting so it will be regenerated on next depot mapping scan");
            try
            {
                lock (_fileLock)
                {
                    File.Delete(_picsJsonFile);
                }
            }
            catch (Exception deleteEx)
            {
                _logger.LogError(deleteEx, "Failed to delete corrupted PICS JSON file");
            }
            return Task.FromResult<PicsJsonData?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading PICS data from JSON file");
            return Task.FromResult<PicsJsonData?>(null);
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
    public async Task<List<long>> GetAppIdsForDepotFromJsonAsync(long depotId)
    {
        try
        {
            var picsData = await LoadPicsDataFromJsonAsync();
            if (picsData?.DepotMappings == null)
            {
                return new List<long>();
            }

            var depotKey = depotId.ToString();
            if (picsData.DepotMappings.TryGetValue(depotKey, out var mapping))
            {
                return mapping.AppIds?.Select(id => (long)id).ToList() ?? new List<long>();
            }

            return new List<long>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error getting app IDs for depot {depotId} from JSON");
            return new List<long>();
        }
    }

    /// <summary>
    /// Clear all depot mappings from the database
    /// </summary>
    public async Task ClearDepotMappingsAsync(CancellationToken cancellationToken = default, bool preserveOrphanResolved = false)
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            int count;
            if (preserveOrphanResolved)
            {
                // Preserve locally-resolved orphan depot mappings (delisted/removed games)
                // These mappings were discovered via direct PICS queries and won't be in GitHub data
                count = await scopedDb.DbContext.SteamDepotMappings
                    .Where(m => m.Source != "orphan-resolved")
                    .ExecuteDeleteAsync(cancellationToken);

                var preserved = await scopedDb.DbContext.SteamDepotMappings.CountAsync(cancellationToken);
                _logger.LogInformation("Cleared {Deleted} depot mappings from database (preserved {Preserved} orphan-resolved mappings)", count, preserved);
            }
            else
            {
                count = await scopedDb.DbContext.SteamDepotMappings.ExecuteDeleteAsync(cancellationToken);
                _logger.LogInformation("Successfully cleared {Count} depot mappings from database", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear depot mappings");
            throw;
        }
    }

    /// <summary>
    /// Import PICS data from JSON file to database
    /// </summary>
    public async Task ImportJsonDataToDatabaseAsync()
    {
        await ImportJsonDataToDatabaseAsync(CancellationToken.None, null);
    }

    /// <summary>
    /// Import PICS data from JSON file to database with cancellation support
    /// </summary>
    public async Task ImportJsonDataToDatabaseAsync(CancellationToken cancellationToken)
    {
        await ImportJsonDataToDatabaseAsync(cancellationToken, null);
    }

    /// <summary>
    /// Import PICS data from JSON file to database with cancellation support and progress reporting
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="progressCallback">Optional callback for progress updates (phase, percentComplete within phase)</param>
    public async Task ImportJsonDataToDatabaseAsync(CancellationToken cancellationToken, Func<string, int, Task>? progressCallback)
    {
        try
        {
            // Phase 1: Load PICS data from JSON (0-5%)
            if (progressCallback != null) await progressCallback("Loading depot data...", 0);

            var picsData = await LoadPicsDataFromJsonAsync();
            if (picsData?.DepotMappings == null)
            {
                _logger.LogWarning("No PICS JSON data to import");
                return;
            }

            if (progressCallback != null) await progressCallback("Processing depot mappings...", 5);

            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            // Phase 2: Build mappings list (5-20%)
            var allMappings = new List<SteamDepotMapping>();
            var processedCount = 0;
            var totalDepots = picsData.DepotMappings.Count;
            var depotIndex = 0;

            foreach (var (depotIdStr, mapping) in picsData.DepotMappings)
            {
                if (!uint.TryParse(depotIdStr, out var depotId))
                {
                    depotIndex++;
                    continue;
                }

                if (mapping.AppIds != null)
                {
                    for (int i = 0; i < mapping.AppIds.Count; i++)
                    {
                        var appId = mapping.AppIds[i];
                        var appName = mapping.AppNames?.ElementAtOrDefault(i) ?? $"App {appId}";

                        allMappings.Add(new SteamDepotMapping
                        {
                            DepotId = depotId,
                            DepotName = mapping.DepotName,
                            AppId = appId,
                            AppName = appName,
                            Source = mapping.Source ?? "JSON-Import",
                            DiscoveredAt = mapping.DiscoveredAt,
                            IsOwner = mapping.OwnerId.HasValue ? appId == mapping.OwnerId.Value : i == 0
                        });

                        processedCount++;
                        if (processedCount % 1000 == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await Task.Yield();
                        }
                    }
                }

                depotIndex++;
                if (progressCallback != null && depotIndex % (totalDepots / 10 + 1) == 0)
                {
                    var phaseProgress = 5 + (int)(15.0 * depotIndex / totalDepots);
                    await progressCallback($"Processing depot mappings... ({depotIndex:N0}/{totalDepots:N0})", phaseProgress);
                }
            }

            if (progressCallback != null) await progressCallback($"Inserting {allMappings.Count:N0} mappings...", 20);

            // Phase 3: UNNEST bulk upsert (20-95%)
            // Check if table is empty for first-run optimization (skip ON CONFLICT overhead)
            var tableIsEmpty = !await scopedDb.DbContext.SteamDepotMappings.AnyAsync(cancellationToken);

            var db = scopedDb.DbContext;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                const int batchSize = 5000;
                var totalBatches = (allMappings.Count + batchSize - 1) / batchSize;
                var batchIndex = 0;

                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                    // Reduce WAL write overhead — depot mappings are idempotent so this is safe
                    await db.Database.ExecuteSqlRawAsync("SET LOCAL synchronous_commit = 'off'", cancellationToken);

                    foreach (var batch in allMappings.Chunk(batchSize))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var depotIds      = batch.Select(m => m.DepotId).ToArray();
                        var depotNames    = batch.Select(m => m.DepotName).ToArray();
                        var appIds        = batch.Select(m => m.AppId).ToArray();
                        var appNames      = batch.Select(m => m.AppName).ToArray();
                        var isOwners      = batch.Select(m => m.IsOwner).ToArray();
                        var discoveredAts = batch.Select(m => m.DiscoveredAt).ToArray();
                        var sources       = batch.Select(m => m.Source).ToArray();

                        var pDepotIds      = new NpgsqlParameter("p0", NpgsqlDbType.Array | NpgsqlDbType.Bigint)     { Value = depotIds };
                        var pDepotNames    = new NpgsqlParameter("p1", NpgsqlDbType.Array | NpgsqlDbType.Text)        { Value = depotNames.Select(v => (object?)(v ?? (object)DBNull.Value)).ToArray() };
                        var pAppIds        = new NpgsqlParameter("p2", NpgsqlDbType.Array | NpgsqlDbType.Bigint)     { Value = appIds };
                        var pAppNames      = new NpgsqlParameter("p3", NpgsqlDbType.Array | NpgsqlDbType.Text)        { Value = appNames.Select(v => (object?)(v ?? (object)DBNull.Value)).ToArray() };
                        var pIsOwners      = new NpgsqlParameter("p4", NpgsqlDbType.Array | NpgsqlDbType.Boolean)    { Value = isOwners };
                        var pDiscoveredAts = new NpgsqlParameter("p5", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = discoveredAts };
                        var pSources       = new NpgsqlParameter("p6", NpgsqlDbType.Array | NpgsqlDbType.Text)        { Value = sources };

                        if (tableIsEmpty)
                        {
                            // First-run: plain INSERT — no conflict resolution overhead
                            await db.Database.ExecuteSqlRawAsync(@"
                            INSERT INTO ""SteamDepotMappings"" (""DepotId"", ""DepotName"", ""AppId"", ""AppName"", ""IsOwner"", ""DiscoveredAt"", ""Source"")
                            SELECT * FROM UNNEST(@p0::bigint[], @p1::text[], @p2::bigint[], @p3::text[], @p4::boolean[], @p5::timestamptz[], @p6::text[])",
                                pDepotIds, pDepotNames, pAppIds, pAppNames, pIsOwners, pDiscoveredAts, pSources);
                        }
                        else
                        {
                            // Upsert: ON CONFLICT on unique (DepotId, AppId) index — dedup server-side
                            await db.Database.ExecuteSqlRawAsync(@"
                            INSERT INTO ""SteamDepotMappings"" (""DepotId"", ""DepotName"", ""AppId"", ""AppName"", ""IsOwner"", ""DiscoveredAt"", ""Source"")
                            SELECT * FROM UNNEST(@p0::bigint[], @p1::text[], @p2::bigint[], @p3::text[], @p4::boolean[], @p5::timestamptz[], @p6::text[])
                            ON CONFLICT (""DepotId"", ""AppId"") DO UPDATE SET
                                ""DepotName""    = EXCLUDED.""DepotName"",
                                ""AppName""      = EXCLUDED.""AppName"",
                                ""IsOwner""      = EXCLUDED.""IsOwner"",
                                ""DiscoveredAt"" = EXCLUDED.""DiscoveredAt"",
                                ""Source""       = EXCLUDED.""Source""",
                                pDepotIds, pDepotNames, pAppIds, pAppNames, pIsOwners, pDiscoveredAts, pSources);
                        }

                        batchIndex++;
                        if (progressCallback != null)
                        {
                            var insertProgress = 20 + (int)(75.0 * batchIndex / Math.Max(1, totalBatches));
                            var insertedCount = Math.Min(batchIndex * batchSize, allMappings.Count);
                            await progressCallback($"Inserting mappings... ({insertedCount:N0}/{allMappings.Count:N0})", insertProgress);
                        }

                        await Task.Yield();
                    }

                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation($"Imported PICS data: {allMappings.Count} mappings upserted (tableWasEmpty={tableIsEmpty})");
                });
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = true;
            }

            if (progressCallback != null) await progressCallback("Import complete.", 95);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("PICS data import cancelled");
            throw;
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
    /// Update the LastChangeNumber in the JSON file metadata (used after GitHub downloads)
    /// </summary>
    public async Task UpdateLastChangeNumberAsync(uint newChangeNumber)
    {
        try
        {
            // Load existing data
            var existingData = await LoadPicsDataFromJsonAsync();
            if (existingData == null || existingData.Metadata == null)
            {
                _logger.LogWarning("Cannot update change number - no existing PICS data found");
                return;
            }

            // Update metadata
            existingData.Metadata.LastChangeNumber = newChangeNumber;
            existingData.Metadata.LastUpdated = DateTime.UtcNow;

            // Save back to file
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var jsonContent = JsonSerializer.Serialize(existingData, jsonOptions);

            lock (_fileLock)
            {
                File.WriteAllText(_picsJsonFile, jsonContent);
            }

            // Clear cache
            ClearCache();

            _logger.LogInformation("Updated PICS JSON metadata: LastChangeNumber = {ChangeNumber}", newChangeNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating change number in PICS JSON file");
            throw;
        }
    }

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

    /// <summary>
    /// Clear the cached PICS data - call this after updating the JSON file
    /// </summary>
    public void ClearCache()
    {
        _cachedPicsData = null;
        _cacheLastLoaded = DateTime.MinValue;
        _logger.LogInformation("PICS data cache cleared");
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
    public uint? OwnerId { get; set; }  // The app that owns this depot (from depotfromapp PICS field)
    public string? DepotName { get; set; }  // Name of the depot from PICS (e.g., "Ubisoft Connect PC Client Content")
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
