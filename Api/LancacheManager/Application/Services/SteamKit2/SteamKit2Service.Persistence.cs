using LancacheManager.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

public partial class SteamKit2Service
{
    private async Task SaveAllMappingsToJsonAsync(bool incrementalOnly = false)
    {
        try
        {
            // Convert ConcurrentDictionary to Dictionary for the service call
            var (depotMappingsDict, appNamesDict, depotOwnersDict) = SteamKit2Helpers.ConvertMappingsDictionaries(
                _depotToAppMappings, _appNames, _depotOwners);

            if (incrementalOnly)
            {
                // Pass validateExisting=true to clean up corrupted entries during incremental updates
                await _picsDataService.MergePicsDataToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen, validateExisting: true, depotOwners: depotOwnersDict);
                _logger.LogInformation(
                    "Merged {DepotCount} unique depot mappings to JSON (incremental); JSON metadata totals will list depot/app pairs when depots are shared",
                    depotMappingsDict.Count);
            }
            else
            {
                await _picsDataService.SavePicsDataToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen, depotOwners: depotOwnersDict);
                _logger.LogInformation(
                    "Saved {DepotCount} unique depot mappings to JSON file (full); JSON metadata totals will list depot/app pairs when depots are shared",
                    _depotToAppMappings.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save depot mappings to JSON file");
        }
    }

    /// <summary>
    /// Import JSON data to database after PICS crawl
    /// </summary>
    private async Task ImportJsonToDatabase()
    {
        try
        {
            await _picsDataService.ImportJsonDataToDatabaseAsync();
            _logger.LogInformation("Successfully imported PICS JSON data to database");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import PICS JSON data to database");
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
        if (jsonData.Metadata?.LastChangeNumber > 0 && jsonData.Metadata.LastChangeNumber > _lastChangeNumberSeen)
        {
            _lastChangeNumberSeen = jsonData.Metadata.LastChangeNumber;
            changeNumberUpdated = true;
        }

        return (mappingsMerged, changeNumberUpdated);
    }

    /// <summary>
    /// Load existing depot mappings from database on startup
    /// </summary>
    private async Task LoadExistingDepotMappings()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingMappings = await context.SteamDepotMappings.AsNoTracking().ToListAsync();

            foreach (var mapping in existingMappings)
            {
                var set = _depotToAppMappings.GetOrAdd(mapping.DepotId, _ => new HashSet<uint>());
                set.Add(mapping.AppId);

                // Track owner apps from database
                if (mapping.IsOwner)
                {
                    _depotOwners.TryAdd(mapping.DepotId, mapping.AppId);
                }

                if (!string.IsNullOrEmpty(mapping.AppName) && mapping.AppName != $"App {mapping.AppId}")
                {
                    _appNames[mapping.AppId] = mapping.AppName;
                }
            }

            _logger.LogInformation($"Loaded {existingMappings.Count} existing depot mappings from database. Total unique depots: {_depotToAppMappings.Count}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading existing depot mappings from database");
        }
    }

    /// <summary>
    /// Load PICS metadata (crawl time and change number) from JSON or state
    /// </summary>
    private async Task LoadPicsMetadataAsync()
    {
        try
        {
            // Try to load from JSON file first (contains both crawl time and change number)
            var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
            if (picsData?.Metadata != null)
            {
                _lastCrawlTime = picsData.Metadata.LastUpdated;
                _lastChangeNumberSeen = picsData.Metadata.LastChangeNumber;
                _logger.LogInformation("Loaded PICS metadata from JSON: crawl time {LastCrawl}, change number {ChangeNumber}",
                    _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"), _lastChangeNumberSeen);
                return;
            }

            // Fallback to state service for crawl time only
            var lastCrawl = _stateService.GetLastPicsCrawl();
            if (lastCrawl.HasValue)
            {
                _lastCrawlTime = lastCrawl.Value;
                _logger.LogInformation("Loaded last PICS crawl time from state: {LastCrawl}", _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else
            {
                _logger.LogInformation("No previous PICS metadata found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load PICS metadata, will use defaults");
        }
    }

    /// <summary>
    /// Save the last PICS crawl time to state
    /// </summary>
    private void SaveLastCrawlTime()
    {
        try
        {
            _stateService.SetLastPicsCrawl(_lastCrawlTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save last PICS crawl time to state");
        }
    }
}
