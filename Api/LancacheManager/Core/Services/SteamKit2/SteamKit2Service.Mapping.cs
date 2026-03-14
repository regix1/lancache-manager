using LancacheManager.Extensions;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Clear game information from all downloads (used before full scans/GitHub imports)
    /// </summary>
    private async Task ClearDownloadGameDataAsync()
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            _logger.LogInformation("Clearing game information from Downloads table (GameName, GameImageUrl, GameAppId)");

            await scopedDb.DbContext.Downloads
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.GameName, (string?)null)
                    .SetProperty(d => d.GameImageUrl, (string?)null)
                    .SetProperty(d => d.GameAppId, (uint?)null));

            _logger.LogInformation("Cleared game information from all downloads - ready for fresh mapping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear download game data");
            throw;
        }
    }

    /// <summary>
    /// Manually apply depot mappings to existing downloads (called from UI)
    /// </summary>
    public async Task ManuallyApplyDepotMappingsAsync()
    {
        _logger.LogInformation("Manually applying depot mappings to downloads");

        // Wait a moment to ensure database operations have completed
        _logger.LogInformation("Waiting 2 seconds to ensure database is fully synced...");
        await Task.Delay(2000);

        // Reload depot mappings from database to ensure we have latest data
        _logger.LogInformation("Reloading depot mappings from database...");
        await LoadExistingDepotMappingsAsync();

        await UpdateDownloadsWithDepotMappingsAsync();

        using var scopedDb = _scopeFactory.CreateScopedDbContext();
        var unmappedCount = await scopedDb.DbContext.Downloads
            .Where(d => d.DepotId.HasValue && d.GameAppId == null)
            .Select(d => d.DepotId!.Value)
            .Distinct()
            .CountAsync();
        if (unmappedCount > 0)
        {
            _logger.LogInformation("{Count} depot(s) remain unmapped. Running a full PICS scan may resolve these (delisted/removed games).", unmappedCount);
        }
    }

    /// <summary>
    /// Attempt to resolve specific unknown depot IDs by querying PICS for their parent apps.
    /// Called from GameCacheDetectionService when unknown games remain after checking existing mappings.
    /// Saves any discovered mappings directly to SteamDepotMappings so they are available
    /// on the next call to ResolveUnknownGamesInCacheAsync.
    /// Returns gracefully (empty list) if Steam is not connected.
    /// </summary>
    public async Task<List<uint>> TryResolveSpecificDepotsAsync(IReadOnlyList<uint> depotIds, CancellationToken ct)
    {
        if (depotIds.Count == 0)
            return new List<uint>();

        try
        {
            if (_steamApps == null || !_isLoggedOn)
            {
                _logger.LogDebug("[GameDetection] Skipping specific depot resolution for {Count} depot(s) - not connected to Steam", depotIds.Count);
                return new List<uint>();
            }

            _logger.LogInformation("[GameDetection] Attempting PICS resolution for {Count} specific unknown depot(s): {DepotIds}",
                depotIds.Count, string.Join(", ", depotIds.Take(10)));

            var candidates = GenerateCandidateAppIds(depotIds);
            if (candidates.Count == 0)
                return new List<uint>();

            _logger.LogInformation("[GameDetection] Querying PICS for {Count} candidate app(s) to resolve unknown depots", candidates.Count);

            var (resolvedDepotIds, newMappings) = await QueryPicsForDepotMappingsAsync(depotIds, candidates, ct);

            // Persist new mappings directly to SteamDepotMappings so they are available immediately
            if (newMappings.Count > 0)
                await PersistNewDepotMappingsAsync(newMappings, ct);

            _logger.LogInformation("[GameDetection] Specific depot resolution complete: {Resolved}/{Total} depot(s) resolved",
                resolvedDepotIds.Count, depotIds.Count);

            return resolvedDepotIds;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameDetection] Specific depot resolution failed (non-fatal)");
            return new List<uint>();
        }
    }

    /// <summary>
    /// Resolve orphan depots by querying PICS for candidate parent app IDs.
    /// Orphan depots are those present in the Downloads table with a DepotId but no GameAppId,
    /// and not already mapped via _depotOwners or the database. This handles delisted/removed
    /// games whose depots never appear in Steam's GetAppList API.
    /// </summary>
    private async Task<List<uint>> ResolveOrphanDepotsAsync(CancellationToken ct)
    {
        try
        {
            if (_steamApps == null || !_isLoggedOn)
            {
                _logger.LogDebug("Skipping orphan depot resolution - not connected to Steam");
                return new List<uint>();
            }

            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            // Find depot IDs that have no GameAppId, are not in _depotOwners, and have no DB mapping with IsOwner=true
            var unmappedDepotIds = await scopedDb.DbContext.Downloads
                .Where(d => d.DepotId.HasValue && d.GameAppId == null)
                .Select(d => d.DepotId!.Value)
                .Distinct()
                .ToListAsync(ct);

            if (unmappedDepotIds.Count == 0)
            {
                _logger.LogDebug("No orphan depots to resolve");
                return new List<uint>();
            }

            // Filter out depots that are already mapped in memory or database
            var dbMappedDepotsList = await scopedDb.DbContext.SteamDepotMappings
                .Where(m => unmappedDepotIds.Contains(m.DepotId) && m.IsOwner)
                .Select(m => m.DepotId)
                .ToListAsync(ct);
            var dbMappedDepots = new HashSet<uint>(dbMappedDepotsList);

            var orphanDepotIds = unmappedDepotIds
                .Where(depotId => !_depotOwners.ContainsKey(depotId) && !dbMappedDepots.Contains(depotId))
                .ToList();

            if (orphanDepotIds.Count == 0)
            {
                _logger.LogDebug("All unmapped depots already have owner mappings");
                return new List<uint>();
            }

            _logger.LogInformation("Attempting orphan depot resolution for {Count} unmapped depot(s)", orphanDepotIds.Count);

            var candidates = GenerateCandidateAppIds(orphanDepotIds);

            // Filter out candidates already scanned during the main PICS pass
            candidates.ExceptWith(_scannedApps);

            if (candidates.Count == 0)
            {
                _logger.LogDebug("All candidate parent apps were already scanned");
                return new List<uint>();
            }

            _logger.LogInformation("Querying PICS for {Count} candidate parent app(s) for orphan depots", candidates.Count);

            var (resolvedDepotIds, _) = await QueryPicsForDepotMappingsAsync(orphanDepotIds, candidates, ct);

            _logger.LogInformation("Orphan depot resolution complete: {Resolved} new depot mapping(s) discovered from {Candidates} candidate(s)",
                resolvedDepotIds.Count, candidates.Count);

            return resolvedDepotIds;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orphan depot resolution failed (non-fatal) - some delisted game depots may remain unmapped");
            return new List<uint>();
        }
    }

    /// <summary>
    /// Generates candidate parent app IDs for a list of depot IDs using common Steam heuristics.
    /// Pattern 1: depotId - 1 (most common convention: app=X, depot=X+1)
    /// Pattern 2: depotId itself (some apps use depot ID = app ID)
    /// Pattern 3: depotId - 2 (less common, but some games use this)
    /// </summary>
    private static HashSet<uint> GenerateCandidateAppIds(IReadOnlyList<uint> depotIds)
    {
        var candidates = new HashSet<uint>();
        foreach (var depotId in depotIds)
        {
            if (depotId > 0)
                candidates.Add(depotId - 1);
            candidates.Add(depotId);
            if (depotId > 1)
                candidates.Add(depotId - 2);
        }
        return candidates;
    }

    /// <summary>
    /// Queries PICS for candidate app IDs and discovers depot-to-app mappings.
    /// Returns the resolved depot IDs (filtered to those in targetDepotIds) and
    /// corresponding SteamDepotMapping records ready for DB persistence.
    /// </summary>
    private async Task<(List<uint> resolvedDepotIds, List<SteamDepotMapping> newMappings)> QueryPicsForDepotMappingsAsync(
        IReadOnlyList<uint> targetDepotIds,
        HashSet<uint> candidateAppIds,
        CancellationToken ct)
    {
        var resolvedDepotIds = new List<uint>();
        var newMappings = new List<SteamDepotMapping>();
        var targetSet = new HashSet<uint>(targetDepotIds);
        var candidateBatches = candidateAppIds.Chunk(50).ToList();

        foreach (var batch in candidateBatches)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var productCallbacks = await FetchProductInfoBatchAsync(batch, ct);

                foreach (var cb in productCallbacks)
                {
                    foreach (var app in cb.Apps.Values)
                    {
                        var beforeKeys = new HashSet<uint>(_depotOwners.Keys);
                        ProcessAppDepots(app);

                        foreach (var kvp in _depotOwners)
                        {
                            if (!beforeKeys.Contains(kvp.Key) && targetSet.Contains(kvp.Key))
                            {
                                resolvedDepotIds.Add(kvp.Key);

                                var appName = _appNames.TryGetValue(kvp.Value, out var name) ? name : null;
                                _depotNames.TryGetValue(kvp.Key, out var depotName);

                                newMappings.Add(new SteamDepotMapping
                                {
                                    DepotId = kvp.Key,
                                    DepotName = depotName,
                                    AppId = kvp.Value,
                                    AppName = appName,
                                    IsOwner = true,
                                    Source = "orphan-resolved",
                                    DiscoveredAt = DateTime.UtcNow
                                });
                            }
                        }
                    }
                }

                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process candidate batch for depot resolution. Continuing...");
            }
        }

        return (resolvedDepotIds, newMappings);
    }

    /// <summary>
    /// Persists newly discovered depot mappings to SteamDepotMappings table,
    /// avoiding duplicates with existing records.
    /// </summary>
    private async Task PersistNewDepotMappingsAsync(List<SteamDepotMapping> newMappings, CancellationToken ct)
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            var depotIdSet = newMappings.Select(m => m.DepotId).Distinct().ToList();
            var existingDepotIds = await scopedDb.DbContext.SteamDepotMappings
                .Where(m => depotIdSet.Contains(m.DepotId) && m.IsOwner)
                .Select(m => m.DepotId)
                .ToHashSetAsync(ct);

            var toInsert = newMappings
                .Where(m => !existingDepotIds.Contains(m.DepotId))
                .ToList();

            if (toInsert.Count > 0)
            {
                await scopedDb.DbContext.SteamDepotMappings.AddRangeAsync(toInsert, ct);
                await scopedDb.DbContext.SaveChangesAsync(ct);
                _logger.LogInformation("Saved {Count} new orphan depot mapping(s) to database", toInsert.Count);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist resolved depot mappings to database");
        }
    }

    /// <summary>
    /// Update downloads that have depot IDs but no game information
    /// </summary>
    private async Task<(int updated, int notFound)> UpdateDownloadsWithDepotMappingsAsync()
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            // Get downloads that have depot IDs but no game info, missing image, or placeholder names
            // The placeholder name check (e.g. "Steam App 12345") ensures downloads get re-resolved
            // when the Steam API later returns the real name
            var downloadsNeedingGameInfo = await scopedDb.DbContext.Downloads
                .Where(d => d.DepotId.HasValue && (
                    d.GameAppId == null ||
                    string.IsNullOrEmpty(d.GameImageUrl) ||
                    d.GameName == null ||
                    EF.Functions.Like(d.GameName, "Steam App %")))
                .ToListAsync();

            _logger.LogInformation($"Found {downloadsNeedingGameInfo.Count} downloads needing game info after PICS completion");

            // Batch load all depot mappings upfront to avoid N+1 query pattern
            var depotIds = downloadsNeedingGameInfo
                .Where(d => d.DepotId.HasValue && !d.GameAppId.HasValue)
                .Select(d => d.DepotId!.Value)
                .Distinct()
                .ToList();

            var depotMappingsFromDb = await scopedDb.DbContext.SteamDepotMappings
                .Where(m => depotIds.Contains(m.DepotId) && m.IsOwner)
                .ToDictionaryAsync(m => m.DepotId, m => new { m.AppId, m.AppName });

            _logger.LogDebug($"Pre-loaded {depotMappingsFromDb.Count} depot mappings from database for batch processing");

            int updated = 0;
            int notFound = 0;
            int processed = 0;
            int totalDownloads = downloadsNeedingGameInfo.Count;

            foreach (var download in downloadsNeedingGameInfo)
            {
                try
                {
                    uint? appId = download.GameAppId; // Use existing appId if available

                    // If no AppId yet, use owner ID from PICS data
                    if (!appId.HasValue && download.DepotId.HasValue)
                    {
                        // First, check in-memory owner mapping from PICS scan
                        if (_depotOwners.TryGetValue(download.DepotId.Value, out var ownerId))
                        {
                            appId = ownerId;
                            _logger.LogTrace($"Using PICS owner app {appId} for depot {download.DepotId}");
                        }
                        else
                        {
                            // Fallback to pre-loaded database owner lookup (batch loaded above)
                            if (depotMappingsFromDb.TryGetValue(download.DepotId.Value, out var dbMapping) && dbMapping.AppId != 0)
                            {
                                appId = dbMapping.AppId;
                                _logger.LogTrace($"Using database owner app {appId} for depot {download.DepotId}");
                            }
                            else
                            {
                                // Last resort fallback: Try common depot->app ID patterns
                                var potentialAppId = download.DepotId.Value;

                                // Pattern 1: depot ID = app ID (some apps have this)
                                if (_appNames.ContainsKey(potentialAppId))
                                {
                                    appId = potentialAppId;
                                }
                                // Pattern 2: depot ID = app ID + 1 (most common Steam convention)
                                else if (potentialAppId > 0 && _appNames.ContainsKey(potentialAppId - 1))
                                {
                                    appId = potentialAppId - 1;
                                }
                            }
                        }
                    }

                    if (appId.HasValue)
                    {
                        download.GameAppId = appId.Value;

                        // First check if we have a PICS name (works for all apps including redistributables/launchers)
                        var picsName = _appNames.TryGetValue(appId.Value, out var name) ? name : null;

                        // Check for depot name (for redistributables like "Ubisoft Connect PC Client Content")
                        string? depotName = null;
                        if (download.DepotId.HasValue)
                        {
                            _depotNames.TryGetValue(download.DepotId.Value, out depotName);
                        }

                        // Get game info from Steam API
                        var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                        if (gameInfo != null && !gameInfo.Name.StartsWith("Steam App ") && !gameInfo.Name.StartsWith("App "))
                        {
                            download.GameName = gameInfo.Name;
                            download.GameImageUrl = gameInfo.HeaderImage;
                            updated++;
                        }
                        else if (!string.IsNullOrEmpty(picsName) && !picsName.StartsWith("App "))
                        {
                            // Use PICS name if Steam Store API failed (e.g., redistributables/launchers)
                            download.GameName = picsName;
                            download.GameImageUrl = gameInfo?.HeaderImage ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                            updated++;
                        }
                        else if (!string.IsNullOrEmpty(depotName))
                        {
                            // Use depot name as fallback for shared redistributables
                            // e.g., "Ubisoft Connect PC Client Content", "RGL/SC Content"
                            download.GameName = depotName;
                            download.GameImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                            _logger.LogInformation("Using depot name for depot {DepotId}: {DepotName}", download.DepotId, depotName);
                            updated++;
                        }
                        else
                        {
                            download.GameName = $"Steam App {appId}";
                            download.GameImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                            updated++;
                        }
                    }
                    else
                    {
                        notFound++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get game info for depot {download.DepotId}");
                    notFound++;
                }

                // Send progress updates EVERY download to keep connection alive
                // This prevents SignalR/HTTP connection timeouts during long-running mapping operations
                processed++;
                if (totalDownloads > 0)
                {
                    double percentComplete = (double)processed / totalDownloads * 100;
                    try
                    {
                        await _notifications.NotifyAllAsync(SignalREvents.DepotMappingProgress, new
                        {
                            operationId = _currentPicsOperationId,
                            status = "Applying mappings to downloads",
                            percentComplete,
                            processedMappings = processed,
                            totalMappings = totalDownloads,
                            mappingsApplied = updated,
                            isLoggedOn = IsSteamAuthenticated,
                            message = $"Applying depot mappings to downloads... {processed}/{totalDownloads}"
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send depot mapping progress via SignalR");
                    }
                }
            }

            if (updated > 0)
            {
                await scopedDb.DbContext.SaveChangesAsync();
                _logger.LogInformation($"Updated {updated} downloads with game information, {notFound} not found");
            }
            else
            {
                _logger.LogInformation($"No downloads updated, {notFound} depots without mappings");
            }

            // Send final 100% progress update
            if (totalDownloads > 0)
            {
                try
                {
                    await _notifications.NotifyAllAsync(SignalREvents.DepotMappingProgress, new
                    {
                        operationId = _currentPicsOperationId,
                        status = "Finalizing depot mappings",
                        percentComplete = 100.0,
                        processedMappings = totalDownloads,
                        totalMappings = totalDownloads,
                        mappingsApplied = updated,
                        isLoggedOn = IsSteamAuthenticated,
                        message = $"Depot mapping complete - {updated} downloads updated"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send final depot mapping progress via SignalR");
                }
            }

            return (updated, notFound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating downloads with depot mappings");
            return (0, 0);
        }
    }
}
