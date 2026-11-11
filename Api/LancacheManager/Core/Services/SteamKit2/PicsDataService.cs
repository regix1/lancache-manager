using System.Text.Json;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Npgsql;
using NpgsqlTypes;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.SteamKit2;

/// <summary>
/// Service for managing PICS depot mapping data in JSON format
/// </summary>
public class PicsDataService
{
    private readonly ILogger<PicsDataService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPathResolver _pathResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, string, bool> _moveFile;
    private readonly string _picsJsonFile;
    private readonly object _fileLock = new object();

    public PicsDataService(ILogger<PicsDataService> logger, IServiceScopeFactory scopeFactory, IPathResolver pathResolver, StateService stateService)
        : this(logger, scopeFactory, pathResolver, stateService, TimeProvider.System)
    {
    }

    internal PicsDataService(ILogger<PicsDataService> logger, IServiceScopeFactory scopeFactory, IPathResolver pathResolver, StateService stateService, TimeProvider timeProvider, Action<string, string, bool>? moveFile = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _pathResolver = pathResolver;
        _timeProvider = timeProvider;
        _moveFile = moveFile ?? File.Move;
        _picsJsonFile = Path.Combine(_pathResolver.GetPicsDirectory(), "pics_depot_mappings.json");
    }

    /// <summary>
    /// Save PICS depot mappings to JSON file
    /// </summary>
    public Task SaveToJsonAsync(Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames, uint lastChangeNumber = 0, Dictionary<uint, uint>? depotOwners = null, Dictionary<uint, string>? depotNames = null)
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
                    appNames.TryGetValue(appId, out var name) ? name : null
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

            // Ensure data directory exists
            var dataDir = Path.GetDirectoryName(_picsJsonFile);
            if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            WritePicsJsonFile(picsData, jsonOptions);

            // Clear cache so next load reads the new file
            ClearCache();

            _logger.LogInformation($"Saved {picsData.Metadata.TotalMappings} PICS depot mappings to JSON file: {_picsJsonFile}");

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
    public async Task MergeToJsonAsync(Dictionary<uint, HashSet<uint>> newDepotMappings, Dictionary<uint, string> appNames, uint lastChangeNumber = 0, bool validateExisting = true, Dictionary<uint, uint>? depotOwners = null, Dictionary<uint, string>? depotNames = null)
    {
        // Load existing data or create new if doesn't exist
        var existingData = await LoadFromJsonAsync() ?? new PicsJsonData
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
                appNames.TryGetValue(appId, out var name) ? name : null
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

        // Ensure data directory exists
        var dataDir = Path.GetDirectoryName(_picsJsonFile);
        if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }

        WritePicsJsonFile(existingData, jsonOptions);

        // Clear cache so next load reads the new file
        ClearCache();

        _logger.LogInformation(
            "Incrementally updated PICS JSON: {NewCount} new, {UpdatedCount} updated, {RemovedCount} removed corrupted, {TotalMappingPairs} total app mappings (shared depots included)",
            newCount,
            updatedCount,
            removedCount,
            existingData.Metadata.TotalMappings);

    }

    /// <summary>
    /// Load PICS depot mappings from JSON file
    /// </summary>
    // Cache for loaded PICS data to avoid repeated deserialization of the ~180MB file
    private PicsJsonData? _cachedPicsData = null;
    private DateTimeOffset _cacheLastLoaded = DateTimeOffset.MinValue;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Loads PICS depot mapping data from the JSON file, using the in-memory cache when fresh.
    /// Returns null both when no file exists yet AND when the load/parse fails (logged as a
    /// warning/error; a corrupted file is also deleted so it regenerates on the next scan) - the
    /// cases are indistinguishable to callers by design: absence of mapping data is always
    /// handled as "not loaded yet", never a silent default for a required value.
    /// </summary>
    public Task<PicsJsonData?> LoadFromJsonAsync()
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_picsJsonFile))
                {
                    return Task.FromResult<PicsJsonData?>(null);
                }

                // Return cached data if still valid. Once it has expired, drop the reference before
                // the reload so the old ~180MB depot graph is collectable while the replacement is
                // parsed, instead of both being resident at once.
                if (_cachedPicsData != null)
                {
                    if (_timeProvider.GetUtcNow() - _cacheLastLoaded < _cacheExpiration)
                    {
                        return Task.FromResult<PicsJsonData?>(_cachedPicsData);
                    }

                    _cachedPicsData = null;
                }

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                // Deserialize straight off the stream while holding the same lock the writers use.
                // The ~100 MB depot map would otherwise land in memory as a >200 MB UTF-16 string
                // before parsing even starts.
                PicsJsonData? picsData;
                lock (_fileLock)
                {
                    using var fileStream = File.OpenRead(_picsJsonFile);
                    if (fileStream.Length == 0)
                    {
                        _logger.LogWarning("PICS JSON file is empty");
                        return Task.FromResult<PicsJsonData?>(null);
                    }

                    picsData = JsonSerializer.Deserialize<PicsJsonData>(fileStream, jsonOptions);
                }

                // Dedup repeated strings across the ~250k mappings before caching: every mapping
                // carries its own copy of the constant Source string, and app/depot names repeat
                // across all depots of the same app - tens of MB of identical strings on a graph
                // that stays resident between refreshes.
                if (picsData?.DepotMappings != null)
                {
                    var stringPool = new Dictionary<string, string>(StringComparer.Ordinal);
                    string Pooled(string value) =>
                        stringPool.TryGetValue(value, out var pooled) ? pooled : stringPool[value] = value;

                    foreach (var mapping in picsData.DepotMappings.Values)
                    {
                        if (mapping == null)
                        {
                            continue;
                        }

                        mapping.Source = Pooled(mapping.Source ?? "SteamKit2-PICS");
                        if (mapping.DepotName != null)
                        {
                            mapping.DepotName = Pooled(mapping.DepotName);
                        }

                        if (mapping.AppNames != null)
                        {
                            for (var i = 0; i < mapping.AppNames.Count; i++)
                            {
                                var appName = mapping.AppNames[i];
                                if (appName != null)
                                {
                                    mapping.AppNames[i] = Pooled(appName);
                                }
                            }
                        }
                    }
                }

                // Cache the loaded data
                if (picsData != null)
                {
                    _cachedPicsData = picsData;
                    _cacheLastLoaded = _timeProvider.GetUtcNow();
                    _logger.LogInformation("PICS data loaded and cached ({TotalMappings} mappings)", picsData.Metadata?.TotalMappings ?? 0);

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
    }

    /// <summary>
    /// Import PICS data from JSON file to database
    /// </summary>
    public async Task ImportToDatabaseAsync()
    {
        await ImportToDatabaseAsync(CancellationToken.None, null);
    }

    /// <summary>
    /// Import PICS data from JSON file to database with cancellation support
    /// </summary>
    public async Task ImportToDatabaseAsync(CancellationToken cancellationToken)
    {
        await ImportToDatabaseAsync(cancellationToken, null);
    }

    /// <summary>
    /// Import PICS data from JSON file to database with cancellation support and progress reporting
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="progressCallback">Optional callback for progress updates (percent complete)</param>
    public async Task ImportToDatabaseAsync(CancellationToken cancellationToken, Func<int, Task>? progressCallback)
    {
        var picsData = await LoadFromJsonAsync()
            ?? throw new InvalidOperationException("No PICS JSON available to import");
        await ImportToDatabaseAsync(picsData, cancellationToken, progressCallback);
    }

    // Every source adds to the table and none removes what another found. beforeCommit runs inside the
    // import's transaction after every mapping write, so its rows commit with the import and no reader
    // sees the import without them.
    public async Task ImportToDatabaseAsync(PicsJsonData picsData, CancellationToken cancellationToken, Func<int, Task>? progressCallback = null, Func<AppDbContext, Task>? beforeCommit = null)
    {
        try
        {
            // Phase 1: Load PICS data from JSON (0-5%)
            if (progressCallback != null) await progressCallback(0);

            if (picsData.DepotMappings == null)
                throw new InvalidOperationException("No depot mappings available to import");

            if (progressCallback != null) await progressCallback(5);

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
                        var appName = mapping.AppNames?.ElementAtOrDefault(i);
                        // The published depot file is built by lancache-pics
                        // (PicsDataCollector/Services/DataPersistenceService.cs:144), which still writes
                        // "App {id}" for an app it has no name for, as did the files older versions of this
                        // app wrote; that placeholder is stored as no name.
                        if (appName == $"App {appId}")
                        {
                            appName = null;
                        }

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
                    await progressCallback(phaseProgress);
                }
            }

            if (progressCallback != null) await progressCallback(20);

            // Phase 3: UNNEST bulk upsert (20-95%)
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
                    // Take the owner-row key before touching any mapping row, the order prefill's
                    // mapping writer uses too, so neither side can wait on the other's row locks.
                    // The commit, or the rollback before a retry, releases it.
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock({PrefillCacheService.DepotOwnerLockKey})", cancellationToken);
                    // Read under the key so a prefill row committed before it is seen by the branch choice.
                    // An empty table takes the first-run plain INSERT (skips ON CONFLICT overhead).
                    var tableIsEmpty = !await db.SteamDepotMappings.AnyAsync(cancellationToken);

                    foreach (var batch in allMappings.Chunk(batchSize))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var depotIds = batch.Select(m => m.DepotId).ToArray();
                        var depotNames = batch.Select(m => m.DepotName).ToArray();
                        var appIds = batch.Select(m => m.AppId).ToArray();
                        var appNames = batch.Select(m => m.AppName).ToArray();
                        var isOwners = batch.Select(m => m.IsOwner).ToArray();
                        var discoveredAts = batch.Select(m => m.DiscoveredAt).ToArray();
                        var sources = batch.Select(m => m.Source).ToArray();

                        var pDepotIds = new NpgsqlParameter("p0", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = depotIds };
                        var pDepotNames = new NpgsqlParameter("p1", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = depotNames.Select(v => (object?)(v ?? (object)DBNull.Value)).ToArray() };
                        var pAppIds = new NpgsqlParameter("p2", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = appIds };
                        var pAppNames = new NpgsqlParameter("p3", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = appNames.Select(v => (object?)(v ?? (object)DBNull.Value)).ToArray() };
                        var pIsOwners = new NpgsqlParameter("p4", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = isOwners };
                        var pDiscoveredAts = new NpgsqlParameter("p5", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = discoveredAts };
                        var pSources = new NpgsqlParameter("p6", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = sources };

                        if (tableIsEmpty)
                        {
                            // First-run: plain INSERT - no conflict resolution overhead
                            await db.Database.ExecuteSqlRawAsync(@"
                            INSERT INTO ""SteamDepotMappings"" (""DepotId"", ""DepotName"", ""AppId"", ""AppName"", ""IsOwner"", ""DiscoveredAt"", ""Source"")
                            SELECT * FROM UNNEST(@p0::bigint[], @p1::text[], @p2::bigint[], @p3::text[], @p4::boolean[], @p5::timestamptz[], @p6::text[])
                            ON CONFLICT (""DepotId"", ""AppId"") DO NOTHING",
                                new object[] { pDepotIds, pDepotNames, pAppIds, pAppNames, pIsOwners, pDiscoveredAts, pSources }, cancellationToken);
                        }
                        else
                        {
                            // Upsert: ON CONFLICT on unique (DepotId, AppId) index - dedup server-side.
                            // A source with no name for a pair keeps the name another source stored.
                            await db.Database.ExecuteSqlRawAsync(@"
                            INSERT INTO ""SteamDepotMappings"" (""DepotId"", ""DepotName"", ""AppId"", ""AppName"", ""IsOwner"", ""DiscoveredAt"", ""Source"")
                            SELECT * FROM UNNEST(@p0::bigint[], @p1::text[], @p2::bigint[], @p3::text[], @p4::boolean[], @p5::timestamptz[], @p6::text[])
                            ON CONFLICT (""DepotId"", ""AppId"") DO UPDATE SET
                                ""DepotName""    = COALESCE(EXCLUDED.""DepotName"", ""SteamDepotMappings"".""DepotName""),
                                ""AppName""      = COALESCE(EXCLUDED.""AppName"", ""SteamDepotMappings"".""AppName""),
                                ""IsOwner""      = EXCLUDED.""IsOwner"",
                                ""DiscoveredAt"" = EXCLUDED.""DiscoveredAt"",
                                ""Source""       = EXCLUDED.""Source""",
                                new object[] { pDepotIds, pDepotNames, pAppIds, pAppNames, pIsOwners, pDiscoveredAts, pSources }, cancellationToken);
                        }

                        batchIndex++;
                        if (progressCallback != null)
                        {
                            var insertProgress = 20 + (int)(75.0 * batchIndex / Math.Max(1, totalBatches));
                            await progressCallback(insertProgress);
                        }

                        await Task.Yield();
                    }

                    // A depot has one owner. The owner this import names is the newest word on it, so
                    // an owner row another source wrote for the same depot steps down to a reference.
                    var owners = allMappings.Where(m => m.IsOwner).ToList();
                    await db.Database.ExecuteSqlRawAsync(@"
                    UPDATE ""SteamDepotMappings"" AS m SET ""IsOwner"" = false
                    FROM UNNEST(@p0::bigint[], @p1::bigint[]) AS incoming(""DepotId"", ""AppId"")
                    WHERE m.""DepotId"" = incoming.""DepotId"" AND m.""IsOwner"" AND m.""AppId"" <> incoming.""AppId""",
                        new object[]
                        {
                            new NpgsqlParameter("p0", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = owners.Select(m => m.DepotId).ToArray() },
                            new NpgsqlParameter("p1", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = owners.Select(m => m.AppId).ToArray() }
                        }, cancellationToken);

                    // A prefill row only records which app downloaded the depot; once PICS names an
                    // owner, the prefill row must not stay a second one.
                    await db.Database.ExecuteSqlRawAsync(@"
                    UPDATE ""SteamDepotMappings"" AS prefill SET ""IsOwner"" = false
                    WHERE prefill.""IsOwner"" AND prefill.""Source"" = 'Prefill'
                      AND EXISTS (
                        SELECT 1 FROM ""SteamDepotMappings"" AS other
                        WHERE other.""DepotId"" = prefill.""DepotId"" AND other.""IsOwner"" AND other.""Source"" <> 'Prefill')",
                        cancellationToken);

                    if (beforeCommit != null)
                    {
                        // The callback edits tracked rows, which SaveChanges finds only with change detection on.
                        db.ChangeTracker.AutoDetectChangesEnabled = true;
                        await beforeCommit(db);
                    }

                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation($"Imported PICS data: {allMappings.Count} mappings upserted (tableWasEmpty={tableIsEmpty})");
                });
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = true;
            }

            if (progressCallback != null) await progressCallback(95);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("PICS data import canceled");
            throw;
        }
    }

    /// <summary>
    /// Get file path for PICS JSON file
    /// </summary>
    public string GetPicsJsonFilePath() => _picsJsonFile;

    public static void ValidateSnapshot(PicsJsonData snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.Metadata is not { LastChangeNumber: > 0 } || snapshot.DepotMappings is not { Count: > 0 })
            throw new InvalidOperationException("Snapshot requires mappings and a positive change number");

        var pairs = new HashSet<(uint, uint)>();
        foreach (var (key, mapping) in snapshot.DepotMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!uint.TryParse(key, out var depotId) || depotId == 0 || mapping?.AppIds is not { Count: > 0 }
                || (mapping.AppNames != null && mapping.AppNames.Count != mapping.AppIds.Count)
                || (mapping.OwnerId.HasValue && !mapping.AppIds.Contains(mapping.OwnerId.Value))
                || (mapping.DiscoveredAt != default && mapping.DiscoveredAt.Kind != DateTimeKind.Utc))
                throw new InvalidOperationException($"Invalid snapshot mapping for depot {key}");
            foreach (var appId in mapping.AppIds)
            {
                if (appId == 0 || !pairs.Add((depotId, appId)))
                    throw new InvalidOperationException($"Invalid or duplicate snapshot pair for depot {key}");
            }
        }
    }

    /// <summary>
    /// Streams to a staged file and atomically publishes it under the shared file lock.
    /// </summary>
    private void WritePicsJsonFile(PicsJsonData picsData, JsonSerializerOptions jsonOptions)
        => WritePicsJsonFile(stream => JsonSerializer.Serialize(stream, picsData, jsonOptions));

    public void WritePicsJsonFile(string jsonContent)
        => WritePicsJsonFile(stream =>
        {
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(jsonContent);
            writer.Flush();
        });

    private void WritePicsJsonFile(Action<FileStream> write)
    {
        lock (_fileLock)
        {
            var stagedPath = _picsJsonFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fileStream = File.Create(stagedPath))
                {
                    write(fileStream);
                    fileStream.Flush(flushToDisk: true);
                }
                _moveFile(stagedPath, _picsJsonFile, true);
                ClearCache();
            }
            finally
            {
                if (File.Exists(stagedPath)) File.Delete(stagedPath);
            }
        }
    }

    /// <summary>
    /// Clear the cached PICS data - call this after updating the JSON file
    /// </summary>
    public void ClearCache()
    {
        lock (_fileLock)
        {
            _cachedPicsData = null;
            _cacheLastLoaded = DateTimeOffset.MinValue;
            _logger.LogInformation("PICS data cache cleared");
        }
    }
}
