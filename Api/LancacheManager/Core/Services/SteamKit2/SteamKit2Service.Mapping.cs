using LancacheManager.Extensions;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
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
                    .SetProperty(d => d.GameAppId, (long?)null));

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
        if (Interlocked.CompareExchange(ref _rebuildActive, 1, 0) != 0)
        {
            throw new ConflictException("A depot mapping operation is already running.");
        }

        _depotRunShowNotification = EffectiveNotificationMode.AllowsTrigger(RunTrigger.Manual);
        _activeDepotScanMode = DepotScanMode.Incremental;
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        _currentRebuildCts = runCts;
        await using var reporter = CreateDepotMappingReporter(
            runCts.Token,
            () =>
            {
                if (ReferenceEquals(_currentRebuildCts, runCts))
                {
                    _currentRebuildCts = null;
                    _currentMappingReporter = null;
                    _currentPicsOperationId = null;
                }

                Interlocked.Exchange(ref _rebuildActive, 0);
                RaiseExecutionStateChanged();
            });
        _currentMappingReporter = reporter;

        try
        {
            await reporter.StartAsync(new Dictionary<string, object?>
            {
                ["scanMode"] = _activeDepotScanMode,
                ["message"] = "Applying depot mappings to downloads...",
            }, "signalr.depotMapping.applyingToDownloads");
            _currentPicsOperationId = reporter.OperationId;
            RaiseExecutionStateChanged();

            await ManuallyApplyDepotMappingsCoreAsync(reporter, reporter.Token);
            await reporter.CompleteAsync(
                success: true,
                stageKey: "signalr.depotMapping.finalized",
                context: CreateDepotContext(
                    message: "Depot mappings applied to downloads",
                    depotMappingsFound: _depotToAppMappings.Count));
        }
        catch (OperationCanceledException)
        {
            await reporter.CompleteAsync(
                success: false,
                cancelled: true,
                stageKey: "signalr.depotMapping.cancelled");
            throw;
        }
        catch (Exception ex)
        {
            await reporter.CompleteAsync(
                success: false,
                error: ex.Message,
                stageKey: "signalr.depotMapping.failed",
                context: CreateDepotContext(errorDetail: ex.Message));
            throw;
        }
        finally
        {
            runCts.Dispose();
            if (ReferenceEquals(_currentRebuildCts, runCts))
            {
                _currentRebuildCts = null;
                _currentMappingReporter = null;
                _currentPicsOperationId = null;
                Interlocked.Exchange(ref _rebuildActive, 0);
                RaiseExecutionStateChanged();
            }
        }
    }

    private async Task ManuallyApplyDepotMappingsCoreAsync(
        MappingOperationReporter reporter,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manually applying depot mappings to downloads");

        // Wait a moment to ensure database operations have completed
        _logger.LogInformation("Waiting 2 seconds to ensure database is fully synced...");
        await Task.Delay(2000, cancellationToken);

        // Reload depot mappings from database to ensure we have latest data
        _logger.LogInformation("Reloading depot mappings from database...");
        await LoadDepotMappingsAsync();

        await reporter.ReportAsync(
            90,
            "signalr.depotMapping.applyingToDownloads",
            CreateDepotContext(
                status: "Applying mappings to downloads",
                message: "Applying mappings to downloads"));
        await ApplyDepotMappingsAsync(reporter, cancellationToken, progressStart: 90, progressEnd: 99);

        using var scopedDb = _scopeFactory.CreateScopedDbContext();
        var unmappedCount = await scopedDb.DbContext.Downloads
            .Where(d => d.DepotId.HasValue && d.GameAppId == null)
            .Select(d => d.DepotId!.Value)
            .Distinct()
            .CountAsync(cancellationToken);
        if (unmappedCount > 0)
        {
            _logger.LogInformation("{Count} depot(s) remain unmapped. Running a full PICS scan may resolve these (delisted/removed games).", unmappedCount);
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
            // Deliberately separate from UnknownGameResolutionService: this is PICS-time depot ownership recovery, not runtime cache-detection resolution.
            if (_steamApps == null || !_isLoggedOn)
            {
                _logger.LogDebug("Skipping orphan depot resolution - not connected to Steam");
                return new List<uint>();
            }

            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            // Find depot IDs that have no GameAppId, are not in _depotOwners, and have no DB mapping with IsOwner=true
            var unmappedDepotIdsLong = await scopedDb.DbContext.Downloads
                .Where(d => d.DepotId.HasValue && d.GameAppId == null)
                .Select(d => d.DepotId!.Value)
                .Distinct()
                .ToListAsync(ct);

            if (unmappedDepotIdsLong.Count == 0)
            {
                _logger.LogDebug("No orphan depots to resolve");
                return new List<uint>();
            }

            // Filter out depots that are already mapped in memory or database
            var dbMappedDepotsList = await scopedDb.DbContext.SteamDepotMappings
                .Where(m => unmappedDepotIdsLong.Contains(m.DepotId) && m.IsOwner)
                .Select(m => m.DepotId)
                .ToListAsync(ct);
            var dbMappedDepots = new HashSet<uint>(dbMappedDepotsList.Select(id => (uint)id));

            var unmappedDepotIds = unmappedDepotIdsLong.Select(id => (uint)id).ToList();
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

            var (resolvedDepotIds, _) = await QueryPicsDepotsAsync(orphanDepotIds, candidates, ct);

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
    private async Task<(List<uint> resolvedDepotIds, List<SteamDepotMapping> newMappings)> QueryPicsDepotsAsync(
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
            catch (Exception ex) when (!IsSessionFatal(ex))
            {
                // Session-fatal failures abort the resolution pass - the session could not be
                // re-established, so every remaining candidate batch would fail the same way.
                _logger.LogWarning(ex, "Failed to process candidate batch for depot resolution. Continuing...");
            }
        }

        return (resolvedDepotIds, newMappings);
    }

    /// <summary>
    /// Update downloads that have depot IDs but no game information
    /// </summary>
    private async Task<(int updated, int notFound)> ApplyDepotMappingsAsync(
        MappingOperationReporter? reporter,
        CancellationToken cancellationToken,
        double progressStart,
        double progressEnd)
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
                .ToListAsync(cancellationToken);

            _logger.LogInformation($"Found {downloadsNeedingGameInfo.Count} downloads needing game info after PICS completion");

            // Batch load all depot mappings upfront to avoid N+1 query pattern
            var depotIds = downloadsNeedingGameInfo
                .Where(d => d.DepotId.HasValue && !d.GameAppId.HasValue)
                .Select(d => d.DepotId!.Value)
                .Distinct()
                .ToList();

            var depotMappingsFromDb = await scopedDb.DbContext.SteamDepotMappings
                .Where(m => depotIds.Contains(m.DepotId) && m.IsOwner)
                .ToDictionaryAsync(m => m.DepotId, m => new { m.AppId, m.AppName }, cancellationToken);

            _logger.LogDebug($"Pre-loaded {depotMappingsFromDb.Count} depot mappings from database for batch processing");

            int updated = 0;
            int notFound = 0;
            int processed = 0;
            int totalDownloads = downloadsNeedingGameInfo.Count;
            int lastLoggedBucket = -1;

            foreach (var download in downloadsNeedingGameInfo)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    uint? appId = download.GameAppId.HasValue ? (uint)download.GameAppId.Value : null; // Use existing appId if available

                    // If no AppId yet, use owner ID from PICS data
                    if (!appId.HasValue && download.DepotId.HasValue)
                    {
                        var depotIdUint = (uint)download.DepotId.Value;
                        // First, check in-memory owner mapping from PICS scan
                        if (_depotOwners.TryGetValue(depotIdUint, out var ownerId))
                        {
                            appId = ownerId;
                            _logger.LogTrace($"Using PICS owner app {appId} for depot {download.DepotId}");
                        }
                        else
                        {
                            // Fallback to pre-loaded database owner lookup (batch loaded above)
                            if (depotMappingsFromDb.TryGetValue(download.DepotId.Value, out var dbMapping) && dbMapping.AppId != 0)
                            {
                                appId = (uint)dbMapping.AppId;
                                _logger.LogTrace($"Using database owner app {appId} for depot {download.DepotId}");
                            }
                            else
                            {
                                // Last resort fallback: Try common depot->app ID patterns
                                var potentialAppId = depotIdUint;

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
                            _depotNames.TryGetValue((uint)download.DepotId.Value, out depotName);
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
                            download.GameImageUrl = gameInfo?.HeaderImage;
                            updated++;
                        }
                        else if (!string.IsNullOrEmpty(depotName))
                        {
                            // Use depot name as fallback for shared redistributables
                            // e.g., "Ubisoft Connect PC Client Content", "RGL/SC Content"
                            download.GameName = depotName;
                            download.GameImageUrl = null;
                            _logger.LogInformation("Using depot name for depot {DepotId}: {DepotName}", download.DepotId, depotName);
                            updated++;
                        }
                        else
                        {
                            download.GameName = $"Steam App {appId}";
                            download.GameImageUrl = null;
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

                // Send progress updates using 5%-bucket throttling (max ~20 messages total)
                // This prevents flooding SignalR with hundreds of broadcasts during large operations
                processed++;
                if (totalDownloads > 0)
                {
                    var currentBucket = (int)(processed * 100.0 / totalDownloads) / 5;
                    if (currentBucket > lastLoggedBucket || processed == totalDownloads)
                    {
                        lastLoggedBucket = currentBucket;
                        double percentComplete = (double)processed / totalDownloads * 100;
                        if (reporter is not null)
                        {
                            var overallPercent = progressStart
                                + (progressEnd - progressStart) * percentComplete / 100;
                            await reporter.ReportAsync(
                                overallPercent,
                                "signalr.depotMapping.applyingToDownloads",
                                CreateDepotContext(
                                    status: "Applying mappings to downloads",
                                    message: "Applying mappings to downloads",
                                    totalMappings: totalDownloads,
                                    processedMappings: processed,
                                    mappingsApplied: updated));
                        }
                    }
                }
            }

            if (updated > 0)
            {
                await scopedDb.DbContext.SaveChangesAsync(cancellationToken);
                scopedDb.DbContext.ChangeTracker.Clear();
                _logger.LogInformation($"Updated {updated} downloads with game information, {notFound} not found");
            }
            else
            {
                _logger.LogInformation($"No downloads updated, {notFound} depots without mappings");
            }

            if (totalDownloads > 0 && reporter is not null)
            {
                await reporter.ReportAsync(
                    progressEnd,
                    "signalr.depotMapping.finalized",
                    CreateDepotContext(
                        status: "Finalizing depot mappings",
                        message: "Finalizing depot mappings",
                        totalMappings: totalDownloads,
                        processedMappings: totalDownloads,
                        mappingsApplied: updated));
            }

            return (updated, notFound);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating downloads with depot mappings");
            return (0, 0);
        }
    }
}
