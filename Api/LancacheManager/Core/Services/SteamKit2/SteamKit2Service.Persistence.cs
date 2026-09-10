using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    private async Task SaveAllMappingsToJsonAsync(bool incrementalOnly = false)
    {
        try
        {
            // Convert ConcurrentDictionary to Dictionary for the service call
            var (depotMappingsDict, appNamesDict, depotOwnersDict, depotNamesDict) = SteamKit2Helpers.ToPlainDictionaries(
                _depotToAppMappings, _appNames, _depotOwners, _depotNames);

            if (incrementalOnly)
            {
                // Pass validateExisting=true to clean up corrupted entries during incremental updates
                await _picsDataService.MergeToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen, validateExisting: true, depotOwners: depotOwnersDict, depotNames: depotNamesDict);
                _logger.LogInformation(
                    "Merged {DepotCount} unique depot mappings to JSON (incremental); JSON metadata totals will list depot/app pairs when depots are shared",
                    depotMappingsDict.Count);
            }
            else
            {
                await _picsDataService.SaveToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen, depotOwners: depotOwnersDict, depotNames: depotNamesDict);
                _logger.LogInformation(
                    "Saved {DepotCount} unique depot mappings to JSON file (full); JSON metadata totals will list depot/app pairs when depots are shared",
                    _depotToAppMappings.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save depot mappings to JSON file");
            throw;
        }
    }

    /// <summary>
    /// Import JSON data to database after PICS crawl
    /// </summary>
    private async Task ImportJsonToDatabaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _picsDataService.ImportToDatabaseAsync(cancellationToken);
            _logger.LogInformation("Successfully imported PICS JSON data to database");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import PICS JSON data to database");
            throw;
        }
    }

    /// <summary>
    /// Merge JSON-backed depot mappings into the in-memory dictionaries.
    /// </summary>
    private (int mappingsMerged, bool changeNumberUpdated) MergeDepotMappingsFromJson(PicsJsonData? jsonData)
    {
        if (jsonData?.DepotMappings == null || jsonData.DepotMappings.Count == 0)
        {
            return (0, false);
        }

        int mappingsMerged = 0;

        foreach (var mappingEntry in jsonData.DepotMappings)
        {
            if (!uint.TryParse(mappingEntry.Key, out var depotId))
            {
                continue;
            }

            var mapping = mappingEntry.Value;
            if (mapping?.AppIds == null)
            {
                continue;
            }

            var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());

            foreach (var appId in mapping.AppIds)
            {
                if (set.Add(appId))
                {
                    mappingsMerged++;
                }
            }

            // Use explicit OwnerId if available, otherwise fallback to first app in array
            if (mapping.OwnerId.HasValue)
            {
                _depotOwners.TryAdd(depotId, mapping.OwnerId.Value);
            }
            else if (mapping.AppIds.Count > 0)
            {
                _depotOwners.TryAdd(depotId, mapping.AppIds[0]);
            }

            if (mapping.AppNames?.Any() == true && mapping.AppIds.Count == mapping.AppNames.Count)
            {
                for (int i = 0; i < mapping.AppIds.Count; i++)
                {
                    _appNames.TryAdd(mapping.AppIds[i], mapping.AppNames[i]);
                }
            }
        }

        var changeNumberUpdated = false;
        var committed = _stateService.GetState().LastPicsChangeNumber;
        var changeNumber = committed ?? jsonData.Metadata?.LastChangeNumber ?? 0;
        if (changeNumber != _lastChangeNumberSeen)
        {
            _lastChangeNumberSeen = changeNumber;
            changeNumberUpdated = true;
        }

        return (mappingsMerged, changeNumberUpdated);
    }

    /// <summary>
    /// Load existing depot mappings from database on startup
    /// </summary>
    private async Task LoadDepotMappingsAsync(bool replace = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            var existingMappings = await scopedDb.DbContext.SteamDepotMappings.AsNoTracking().ToListAsync(cancellationToken);
            var mappings = new Dictionary<uint, HashSet<uint>>();
            var owners = new Dictionary<uint, uint>();
            var names = new Dictionary<uint, string>();
            var depotNames = new Dictionary<uint, string>();

            foreach (var mapping in existingMappings)
            {
                var depotIdUint = (uint)mapping.DepotId;
                var appIdUint = (uint)mapping.AppId;

                if (!mappings.TryGetValue(depotIdUint, out var set))
                    mappings[depotIdUint] = set = new HashSet<uint>();
                set.Add(appIdUint);

                // Track owner apps from database
                if (mapping.IsOwner)
                {
                    owners.TryAdd(depotIdUint, appIdUint);
                }

                if (!string.IsNullOrEmpty(mapping.AppName) && mapping.AppName != $"App {mapping.AppId}")
                {
                    names[appIdUint] = mapping.AppName;
                }

                // Load depot names from database
                if (!string.IsNullOrEmpty(mapping.DepotName))
                {
                    depotNames.TryAdd(depotIdUint, mapping.DepotName);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (replace)
            {
                _depotToAppMappings.Clear();
                _depotOwners.Clear();
                _appNames.Clear();
                _depotNames.Clear();
            }
            foreach (var (depotId, apps) in mappings)
                _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>()).UnionWith(apps);
            foreach (var (depotId, appId) in owners) _depotOwners.TryAdd(depotId, appId);
            foreach (var (appId, name) in names) _appNames[appId] = name;
            foreach (var (depotId, name) in depotNames) _depotNames.TryAdd(depotId, name);

            _logger.LogInformation($"Loaded {existingMappings.Count} existing depot mappings from database. Total unique depots: {_depotToAppMappings.Count}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading existing depot mappings from database");
            if (replace) throw;
        }
    }

    /// <summary>
    /// Load PICS metadata (crawl time and change number) from state and JSON
    /// Crawl time is loaded from state.json (for accurate scheduling), change number from JSON (for PICS scans)
    /// </summary>
    private async Task LoadPicsMetadataAsync()
    {
        try
        {
            // Load crawl time from state.json FIRST (for scheduling accuracy)
            // This ensures we track "when did WE last refresh" not "when was the JSON file generated"
            var lastCrawl = _stateService.GetLastPicsCrawl();
            if (lastCrawl.HasValue)
            {
                _lastCrawlTime = lastCrawl.Value;
                _logger.LogInformation("Loaded last crawl time from state: {LastCrawl}", _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            // Load change number from JSON file (still needed for PICS scans)
            var picsData = await _picsDataService.LoadFromJsonAsync();
            var committed = _stateService.GetState().LastPicsChangeNumber;
            _lastChangeNumberSeen = committed ?? picsData?.Metadata?.LastChangeNumber ?? 0;
            if (picsData?.Metadata != null)
            {
                _logger.LogInformation("Loaded change number from JSON: {ChangeNumber}", _lastChangeNumberSeen);

                // Only use JSON timestamp if state.json doesn't have one (first-time setup)
                if (!lastCrawl.HasValue && committed == null)
                {
                    _lastCrawlTime = picsData.Metadata.LastUpdated;
                    _logger.LogInformation("No state crawl time found, using JSON metadata timestamp: {LastCrawl}",
                        _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }
            }
            else if (!lastCrawl.HasValue)
            {
                _logger.LogInformation("No previous PICS metadata found in state or JSON");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load PICS metadata, will use defaults");
        }
    }

}
