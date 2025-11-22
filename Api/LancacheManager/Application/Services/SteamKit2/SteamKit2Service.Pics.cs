using System.Text.Json;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SteamKit2;

namespace LancacheManager.Application.Services;

public partial class SteamKit2Service
{
    public bool TryStartRebuild(CancellationToken cancellationToken = default, bool incrementalOnly = false)
    {
        if (Interlocked.CompareExchange(ref _rebuildActive, 1, 0) != 0)
        {
            return false;
        }

        _logger.LogInformation("Starting Steam PICS depot crawl");
        _lastScanWasForced = false; // Reset flag at start of new scan
        _automaticScanSkipped = false; // Reset flag at start of new scan

        // Send start notification via SignalR (fire-and-forget)
        _ = Task.Run(async () =>
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("DepotMappingStarted", new
                {
                    scanMode = incrementalOnly ? "incremental" : "full",
                    message = incrementalOnly ? "Starting incremental depot mapping scan..." : "Starting full depot mapping scan...",
                    isLoggedOn = IsSteamAuthenticated,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send depot mapping start notification via SignalR");
            }
        });

        // Dispose previous cancellation token source if it exists
        _currentRebuildCts?.Dispose();

        try
        {
            _currentRebuildCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            // Main cancellation token source was disposed, use provided token or none
            _currentRebuildCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
        }

        async Task RunAsync()
        {
            try
            {
                await ConnectAndBuildIndexAsync(_currentRebuildCts.Token, incrementalOnly).ConfigureAwait(false);
                _logger.LogInformation("PICS crawl completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Steam PICS depot crawl cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Steam PICS depot crawl failed");
            }
            finally
            {
                // Clear rebuild flag BEFORE disconnecting to prevent reconnection attempts
                Interlocked.Exchange(ref _rebuildActive, 0);

                // Explicitly disconnect after crawl completion to prevent reconnection loops
                if (_steamClient?.IsConnected == true)
                {
                    _intentionalDisconnect = true;
                    await DisconnectFromSteamAsync();
                }

                // Dispose the cancellation token source
                _currentRebuildCts?.Dispose();
                _currentRebuildCts = null;
            }
        }

        _currentBuildTask = Task.Run(RunAsync, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Cancel the current PICS rebuild if one is running
    /// </summary>
    public async Task<bool> CancelRebuildAsync()
    {
        if (!IsRebuildRunning || _currentRebuildCts == null)
        {
            return false;
        }

        try
        {
            _logger.LogInformation("Cancelling active PICS rebuild");
            _currentRebuildCts.Cancel();

            // Wait briefly for cancellation to complete
            if (_currentBuildTask != null)
            {
                await Task.WhenAny(_currentBuildTask, Task.Delay(5000));
            }

            _logger.LogInformation("PICS rebuild cancelled successfully");

            // Send cancellation notification via SignalR
            try
            {
                await _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
                {
                    success = true,
                    cancelled = true,
                    message = "Depot mapping scan cancelled",
                    isLoggedOn = IsSteamAuthenticated,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception signalREx)
            {
                _logger.LogWarning(signalREx, "Failed to send depot mapping cancellation notification via SignalR");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling PICS rebuild");
            return false;
        }
    }

    private async Task ConnectAndBuildIndexAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        try
        {
            // Stop idle disconnect timer since we're actively using the connection
            StopIdleDisconnectTimer();

            await ConnectAndLoginAsync(ct);
            _lastConnectionActivity = SteamKit2Helpers.UpdateConnectionActivity();
            await BuildDepotIndexAsync(ct, incrementalOnly);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected - don't retry, just rethrow
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect and build depot index");
            throw;
        }
    }

    /// <summary>
    /// Build depot index by querying PICS for all apps
    /// </summary>
    private async Task BuildDepotIndexAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        try
        {
            _currentStatus = "Connecting and enumerating apps";
            _lastErrorMessage = null; // Clear previous errors when starting new scan
            _logger.LogInformation("Starting to build depot index via Steam PICS (incremental={Incremental}, Current mappings in memory: {Count})...",
                incrementalOnly, _depotToAppMappings.Count);

            // Check if we already have data loaded in memory (fast)
            // Use in-memory count if available, otherwise estimate from database (non-blocking for logging)
            int estimatedDatabaseDepotCount = _depotToAppMappings.Count;
            if (estimatedDatabaseDepotCount == 0)
            {
                // Only do database count if memory is empty - run in background for logging
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var dbCount = await context.SteamDepotMappings.CountAsync();
                        _logger.LogInformation("Database has {Count} depot mappings", dbCount);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to count database depot mappings");
                    }
                });
            }

            // Load existing data from JSON file if it exists
            var existingData = await _picsDataService.LoadPicsDataFromJsonAsync();
            bool hasExistingJsonData = existingData?.DepotMappings?.Any() == true;
            bool hasExistingDatabaseData = estimatedDatabaseDepotCount > 1000; // Require substantial database data (not just a few mappings)
            bool hasExistingData = hasExistingJsonData || hasExistingDatabaseData;

            _logger.LogInformation("Existing data check: JSON={HasJson} ({JsonCount} mappings), Database={HasDb} ({DbCount} mappings)",
                hasExistingJsonData, existingData?.DepotMappings?.Count ?? 0, hasExistingDatabaseData, estimatedDatabaseDepotCount);

            if (!incrementalOnly)
            {
                // Full rebuild - start fresh, don't load change number
                _logger.LogInformation("Starting full PICS generation (will use Web API for app enumeration)");
                _depotToAppMappings.Clear();
                _appNames.Clear();
                _scannedApps.Clear(); // Clear scanned apps list so all apps are rescanned
                _lastChangeNumberSeen = 0; // Reset to ensure Web API enumeration is used

                // Clear game data from Downloads table so all downloads get fresh mappings
                _logger.LogInformation("Full scan detected - clearing existing game data from downloads for fresh mapping");
                await ClearDownloadGameDataAsync();
            }
            else if (incrementalOnly)
            {
                // For incremental updates, load existing mappings and change number
                _logger.LogInformation("Incremental update mode - loading existing data");

                // Load existing mappings from database first
                await LoadExistingDepotMappings();
                _logger.LogInformation("Loaded {Count} mappings from database into memory", _depotToAppMappings.Count);

                // Load change number from JSON if available
                if (hasExistingJsonData && existingData?.Metadata != null)
                {
                    _lastChangeNumberSeen = existingData.Metadata.LastChangeNumber;
                    _logger.LogInformation("Loaded change number {ChangeNumber} from JSON metadata", _lastChangeNumberSeen);

                    // Merge existing depot mappings from JSON (only if not already loaded from database)
                    if (_depotToAppMappings.Count == 0)
                    {
                        var (jsonMappingsMerged, _) = MergeDepotMappingsFromJson(existingData);
                        if (jsonMappingsMerged > 0)
                        {
                            _logger.LogInformation("Merged {Count} existing mappings from JSON (database was empty)", jsonMappingsMerged);
                        }
                    }
                }

                _logger.LogInformation("Starting incremental update with {Count} existing depot mappings", _depotToAppMappings.Count);
            }

            // Enumerate every appid by crawling the PICS changelist
            List<uint> appIds = await EnumerateAllAppIdsViaPicsChangesAsync(ct, incrementalOnly);
            _logger.LogInformation("Retrieved {Count} app IDs from PICS", appIds.Count);

            var allBatches = appIds
                .Where(id => !_scannedApps.Contains(id))
                .Chunk(AppBatchSize)
                .ToList();

            _totalAppsToProcess = appIds.Count;
            _processedApps = 0;
            _totalBatches = allBatches.Count;
            _processedBatches = 0;
            _currentStatus = "Processing app data";

            // Reset session start count to track new mappings found
            _sessionStartDepotCount = _depotToAppMappings.Count;
            _logger.LogInformation("Session start depot count: {Count}", _sessionStartDepotCount);

            _logger.LogInformation("Processing {BatchCount} appinfo batches via PICS", allBatches.Count);

            foreach (var batch in allBatches)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    _logger.LogTrace("Processing batch {BatchNum}/{TotalBatches} with {AppCount} apps",
                        _processedBatches + 1, allBatches.Count, batch.Length);

                    // Acquire access tokens for protected appinfo entries when available
                    var tokensJob = _steamApps!.PICSGetAccessTokens(batch, Enumerable.Empty<uint>());
                    var tokens = await WaitForCallbackAsync(tokensJob, ct);

                    _logger.LogTrace("Received {GrantedTokens} granted tokens and {DeniedTokens} denied tokens for batch",
                        tokens.AppTokens.Count, tokens.AppTokensDenied.Count);

                    // Prepare the product info request with tokens attached when needed
                    var appRequests = new List<SteamApps.PICSRequest>(batch.Length);
                    foreach (var appId in batch)
                    {
                        var request = new SteamApps.PICSRequest(appId);
                        if (tokens.AppTokens.TryGetValue(appId, out var token))
                        {
                            request.AccessToken = token;
                            _logger.LogTrace("Using access token for app {AppId}", appId);
                        }

                        appRequests.Add(request);
                    }

                    var productJob = _steamApps.PICSGetProductInfo(appRequests, Enumerable.Empty<SteamApps.PICSRequest>());
                    var productCallbacks = await WaitForAllProductInfoAsync(productJob, ct);

                    int appsInThisBatch = 0, unknownInThisBatch = 0;
                    var dlcAppsToScan = new List<uint>();

                    foreach (var cb in productCallbacks)
                    {
                        appsInThisBatch += cb.Apps.Count;
                        unknownInThisBatch += cb.UnknownApps.Count;

                        foreach (var app in cb.Apps.Values)
                        {
                            var dlcList = ProcessAppDepots(app);
                            dlcAppsToScan.AddRange(dlcList);
                            _processedApps++;
                        }
                    }

                    // Process DLC apps found in this batch
                    if (dlcAppsToScan.Count > 0)
                    {
                        _logger.LogInformation("Found {Count} DLC apps to scan in this batch", dlcAppsToScan.Count);

                        // Process DLC apps in smaller sub-batches
                        var dlcBatches = dlcAppsToScan.Distinct().Chunk(50).ToList();
                        foreach (var dlcBatch in dlcBatches)
                        {
                            try
                            {
                                var dlcTokensJob = _steamApps.PICSGetAccessTokens(dlcBatch, Enumerable.Empty<uint>());
                                var dlcTokens = await WaitForCallbackAsync(dlcTokensJob, ct);

                                var dlcAppRequests = new List<SteamApps.PICSRequest>();
                                foreach (var dlcAppId in dlcBatch)
                                {
                                    var request = new SteamApps.PICSRequest(dlcAppId);
                                    if (dlcTokens.AppTokens.TryGetValue(dlcAppId, out var token))
                                    {
                                        request.AccessToken = token;
                                    }
                                    dlcAppRequests.Add(request);
                                }

                                var dlcProductJob = _steamApps.PICSGetProductInfo(dlcAppRequests, Enumerable.Empty<SteamApps.PICSRequest>());
                                var dlcProductCallbacks = await WaitForAllProductInfoAsync(dlcProductJob, ct);

                                foreach (var dlcCb in dlcProductCallbacks)
                                {
                                    foreach (var dlcApp in dlcCb.Apps.Values)
                                    {
                                        ProcessAppDepots(dlcApp);  // Don't need to scan DLC's DLCs recursively
                                        _processedApps++;
                                    }
                                }

                                await Task.Delay(100, ct);  // Small delay between DLC batches
                            }
                            catch (OperationCanceledException)
                            {
                                // Cancellation is expected, just rethrow
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to process DLC batch. Continuing...");
                            }
                        }
                    }

                    _logger.LogTrace(
                        "Received product info for {Apps} apps, {Unknown} unknown across {Parts} parts",
                        appsInThisBatch, unknownInThisBatch, productCallbacks.Count);

                    _processedBatches++;

                    // Send SignalR progress updates every 10 batches for smoother UI feedback
                    if (_processedBatches % 10 == 0)
                    {
                        double percentComplete = (_processedBatches * 100.0 / allBatches.Count);

                        // Send progress via SignalR
                        try
                        {
                            await _hubContext.Clients.All.SendAsync("DepotMappingProgress", new
                            {
                                status = "Scanning Steam PICS data",
                                percentComplete,
                                processedBatches = _processedBatches,
                                totalBatches = allBatches.Count,
                                depotMappingsFound = _depotToAppMappings.Count,
                                isLoggedOn = IsSteamAuthenticated,
                                message = $"Scanning Steam PICS: {_processedBatches}/{allBatches.Count} batches"
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send PICS scan progress via SignalR");
                        }
                    }

                    // Log progress and save every 50 batches to reduce I/O
                    if (_processedBatches % 50 == 0)
                    {
                        double percentComplete = (_processedBatches * 100.0 / allBatches.Count);
                        _logger.LogInformation(
                            "Processed {Processed}/{Total} batches ({Percent:F1}%); depot mappings found={Mappings}",
                            _processedBatches,
                            allBatches.Count,
                            percentComplete,
                            _depotToAppMappings.Count);

                        // Use merge for incremental, full save for complete rebuild
                        await SaveAllMappingsToJsonAsync(incrementalOnly);
                    }

                    // Adaptive delay based on current performance
                    var delayMs = _depotToAppMappings.Count > 1000 ? 100 : 150;
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is expected, just rethrow
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process batch {Batch}/{Total}. Continuing...",
                        _processedBatches + 1, allBatches.Count);
                }
            }

            _currentStatus = "Saving PICS data to JSON";
            _logger.LogInformation("Depot index built. Total depot mappings: {Count}", _depotToAppMappings.Count);

            // DISABLED: Using OwnerId from PICS data instead of log-based fallback
            // Add log-based fallback mappings for depots that appear in actual downloads
            // Only adds mappings where depotId = appId and no existing mapping exists
            // await AddLogBasedFallbackDepotMappingsAsync();

            // Final save - use merge for incremental, full save for complete rebuild
            await SaveAllMappingsToJsonAsync(incrementalOnly);

            _currentStatus = "Importing to database";
            // Import to database BEFORE updating downloads so mappings are available
            try
            {
                await ImportJsonToDatabase();
                _logger.LogInformation("Database import completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database import failed");
            }

            // Auto-apply depot mappings to downloads after PICS data is ready
            _currentStatus = "Applying depot mappings";
            _logger.LogInformation("Automatically applying depot mappings after PICS completion");

            var (downloadsUpdated, downloadsNotFound) = await UpdateDownloadsWithDepotMappings();

            _currentStatus = "Complete";
            _lastCrawlTime = DateTime.UtcNow;
            SaveLastCrawlTime();

            // Clear cached viability check since we just completed a scan
            // Next check will get fresh data from Steam
            var state = _stateService.GetState();
            state.RequiresFullScan = false;
            state.LastViabilityCheck = null;
            state.LastViabilityCheckChangeNumber = 0;
            state.ViabilityChangeGap = 0;
            _stateService.SaveState(state);
            _logger.LogInformation("Cleared cached viability check - next check will query Steam for fresh data");

            // Send completion notification
            try
            {
                var totalMappings = _depotToAppMappings.Count;
                await _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
                {
                    success = true,
                    message = "Depot mapping completed successfully",
                    totalMappings,
                    downloadsUpdated,
                    scanMode = incrementalOnly ? "incremental" : "full",
                    isLoggedOn = IsSteamAuthenticated,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send depot mapping completion via SignalR");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected - don't log as error
            _currentStatus = "Cancelled";
            _logger.LogDebug("Depot mapping scan was cancelled");
            // Don't send error notification for cancellation - it's handled elsewhere
            throw;
        }
        catch (Exception ex)
        {
            _currentStatus = "Error occurred";
            _lastErrorMessage = ex.Message;
            _logger.LogError(ex, "Error building depot index");

            // Send error notification
            try
            {
                await _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
                {
                    success = false,
                    message = $"Depot mapping failed: {ex.Message}",
                    error = ex.Message,
                    isLoggedOn = IsSteamAuthenticated,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception signalREx)
            {
                _logger.LogWarning(signalREx, "Failed to send depot mapping error via SignalR");
            }

            // Re-throw the exception so calling code knows the operation failed
            throw;
        }
    }

    private List<uint> ProcessAppDepots(SteamApps.PICSProductInfoCallback.PICSProductInfo app)
    {
        var dlcAppIdsToScan = new List<uint>();

        try
        {
            var appId = app.ID;
            var kv = app.KeyValues;

            var appinfo = kv["appinfo"];
            var common = appinfo != KeyValue.Invalid ? appinfo["common"] : kv["common"];
            var depots = appinfo != KeyValue.Invalid ? appinfo["depots"] : kv["depots"];

            var appName = common?["name"]?.AsString() ?? $"App {appId}";
            var appType = common?["type"]?.AsString()?.ToLower() ?? "unknown";
            _appNames[appId] = appName;

            // Extract DLC list for DLC depot discovery
            var listofdlc = common?["listofdlc"];
            if (listofdlc != KeyValue.Invalid && listofdlc?.Children != null)
            {
                foreach (var dlcChild in listofdlc.Children)
                {
                    if (uint.TryParse(dlcChild.AsString(), out var dlcAppId))
                    {
                        // Add DLC to scan list if not already processed
                        if (!_appNames.ContainsKey(dlcAppId))
                        {
                            dlcAppIdsToScan.Add(dlcAppId);
                        }
                    }
                }
            }

            if (depots == KeyValue.Invalid)
            {
                return dlcAppIdsToScan;
            }

            foreach (var child in depots.Children)
            {
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                var ownerFromPics = AsUInt(child["depotfromapp"]);
                var ownerAppId = ownerFromPics ?? appId;

                // Self-referencing depots (depot ID = app ID) are valid owner depots
                // This includes both base games and DLCs
                var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
                set.Add(ownerAppId);

                // Store the owner app for this depot
                _depotOwners.TryAdd(depotId, ownerAppId);

                // Store owner app name
                if (ownerFromPics.HasValue && !_appNames.ContainsKey(ownerAppId))
                {
                    _appNames[ownerAppId] = $"App {ownerAppId}";
                }
            }

            _scannedApps.Add(appId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Warning: Error processing app {AppId}", app.ID);
        }

        return dlcAppIdsToScan;
    }

    /// <summary>
    /// Enumerate app IDs via PICS changes or Web API fallback.
    ///
    /// SECONDARY VIABILITY CHECK (during execution):
    /// - If incrementalOnly=true (automatic scans): Throws exception if Steam requires full scan
    /// - If incrementalOnly=false (manual scans): Silently falls back to Web API if needed
    ///
    /// This is separate from the pre-flight check in the controller because:
    /// 1. Automatic scheduled scans don't go through the controller viability check
    /// 2. Provides a safety net if Steam conditions change during scan startup
    /// 3. For manual full scans, user has already been informed - just proceed with Web API
    /// </summary>
    private async Task<List<uint>> EnumerateAllAppIdsViaPicsChangesAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        if (_steamApps is null)
        {
            throw new InvalidOperationException("SteamApps handler not ready.");
        }

        var allApps = new HashSet<uint>();

        // Check if we have existing data
        bool hasExistingMappings = _depotToAppMappings.Count > 0;

        if (incrementalOnly && hasExistingMappings && _lastChangeNumberSeen == 0)
        {
            _logger.LogInformation("Incremental mode with existing data but no change number - will use PICS to check for updates");
        }

        // For full scans, use Web API to enumerate all apps first, then PICS for depot info
        if (!incrementalOnly)
        {
            _logger.LogInformation("Full scan requested - enumerating all apps via Web API V2/V1");
            try
            {
                var webApiAppIds = await EnumerateAllAppIdsViaWebApiAsync(ct);

                // Success! Add all app IDs to our collection
                foreach (var appId in webApiAppIds)
                {
                    allApps.Add(appId);
                }

                _logger.LogInformation("Successfully enumerated {Count} apps via Web API - will now query PICS for depot information", webApiAppIds.Count);

                // IMPORTANT: Get and save current change number for future incremental updates
                // Even though we're using Web API for app enumeration, we still need the PICS change number
                var changeNumberJob = _steamApps.PICSGetChangesSince(0, false, false);
                var changeNumberResult = await WaitForCallbackAsync(changeNumberJob, ct);
                var latestChangeNumber = changeNumberResult.CurrentChangeNumber;

                if (latestChangeNumber > _lastChangeNumberSeen)
                {
                    _lastChangeNumberSeen = latestChangeNumber;
                    _logger.LogInformation("Initialized change number to {ChangeNumber} for future incremental updates", _lastChangeNumberSeen);
                }

                // Skip the PICS changes loop and go directly to processing the apps we got from Web API
                var appList = allApps.OrderBy(id => id).ToList();
                return appList;
            }
            catch (Exception webApiEx)
            {
                _logger.LogError(webApiEx, "Failed to enumerate via Web API for full scan");

                // Provide helpful error message
                var errorMessage = webApiEx.Message;
                if (string.IsNullOrEmpty(errorMessage))
                {
                    errorMessage = "Full scan requires Steam Web API, but it is unavailable. Please download the latest depot mappings from GitHub using the 'Download Pre-created Data' button, or configure a Steam Web API key for V1 fallback.";
                }

                throw new InvalidOperationException(errorMessage, webApiEx);
            }
        }

        // For incremental updates, use PICS changes
        uint since = 0;

        // Get current change number
        var initialJob = _steamApps.PICSGetChangesSince(0, false, false);
        var initialChanges = await WaitForCallbackAsync(initialJob, ct);
        var currentChangeNumber = initialChanges.CurrentChangeNumber;

        // Use saved change number for incremental, or start from recent point if we have existing data
        if (incrementalOnly && _lastChangeNumberSeen > 0)
        {
            since = _lastChangeNumberSeen;
            _logger.LogInformation("Incremental update from saved change #{FromChange} to #{CurrentChange}", since, currentChangeNumber);
        }
        else if (incrementalOnly && hasExistingMappings && _lastChangeNumberSeen == 0)
        {
            // Have data but no change number - be more aggressive and scan more changes
            since = Math.Max(0, currentChangeNumber - 50000); // Last ~50k changes (about 2-3 months) - more aggressive
            _logger.LogInformation("Incremental update with existing data but no change number - starting from recent change #{FromChange} to #{CurrentChange} (aggressive scan)", since, currentChangeNumber);
        }
        else
        {
            // Full mode or no existing data - start from recent point for partial updates
            since = Math.Max(0, currentChangeNumber - 50000);
            _logger.LogInformation("Enumerating from change #{FromChange} to #{CurrentChange}", since, currentChangeNumber);
        }

        // Update change number NOW so it gets saved during batch processing
        if (currentChangeNumber > _lastChangeNumberSeen)
        {
            _lastChangeNumberSeen = currentChangeNumber;
            _logger.LogInformation("Updated change number to {ChangeNumber} for this crawl", _lastChangeNumberSeen);
        }

        int consecutiveFullUpdates = 0;
        const int maxFullUpdates = 3;

        while (since < currentChangeNumber && consecutiveFullUpdates < maxFullUpdates)
        {
            ct.ThrowIfCancellationRequested();

            var job = _steamApps.PICSGetChangesSince(since, true, true);
            var changes = await WaitForCallbackAsync(job, ct);

            if (changes.RequiresFullUpdate || changes.RequiresFullAppUpdate)
            {
                consecutiveFullUpdates++;

                // Steam is requesting a full update - this means the change gap is too large
                // Try to use the Steam Web API (V2/V1) to get all app IDs
                _logger.LogWarning("PICS requesting full update - change gap is too large for incremental scan. Attempting to use Steam Web API for full enumeration.");
                _lastScanWasForced = true; // Mark that scan was forced to be full

                if (incrementalOnly)
                {
                    throw new InvalidOperationException("Steam requires a full scan - change gap is too large for incremental update. Please download the latest depot mappings from GitHub using the 'Download Pre-created Data' button in the Management page, or run a full scan instead.");
                }

                try
                {
                    // Try to enumerate via Web API (V2 or V1 with API key)
                    _logger.LogInformation("Attempting full scan via Steam Web API (V2/V1 fallback)");
                    var webApiAppIds = await EnumerateAllAppIdsViaWebApiAsync(ct);

                    // Success! Add all app IDs to our collection
                    foreach (var appId in webApiAppIds)
                    {
                        allApps.Add(appId);
                    }

                    _logger.LogInformation("Successfully enumerated {Count} apps via Steam Web API - continuing with PICS scan", webApiAppIds.Count);

                    // Break out of PICS loop since we got all apps via Web API
                    break;
                }
                catch (Exception webApiEx)
                {
                    _logger.LogError(webApiEx, "Failed to enumerate via Steam Web API - full scan not possible");

                    // Provide helpful error message based on the exception
                    var errorMessage = webApiEx.Message;
                    if (string.IsNullOrEmpty(errorMessage))
                    {
                        errorMessage = "Steam requires a full scan, but the Steam Web API is unavailable. Please download the latest depot mappings from GitHub using the 'Download Pre-created Data' button in the Management page.";
                    }

                    throw new InvalidOperationException(errorMessage, webApiEx);
                }
            }

            consecutiveFullUpdates = 0;

            foreach (var change in changes.AppChanges)
            {
                allApps.Add(change.Key);
            }

            var last = changes.LastChangeNumber;
            if (last <= since)
            {
                if (changes.AppChanges.Count == 0)
                {
                    since += 500;
                    await Task.Delay(100, ct);
                    continue;
                }
                last = (uint)Math.Min((long)currentChangeNumber, (long)since + Math.Max(1, changes.AppChanges.Count));
            }

            since = last;

            if (allApps.Count >= 500000)
                break;

            await Task.Delay(100, ct);
        }

        var list = allApps.ToList();
        list.Sort();

        _logger.LogInformation("PICS enumeration complete. Found {Count} apps", list.Count);
        return list;
    }

    /// <summary>
    /// Enumerate all app IDs via Steam Web API V2/V1 (used when PICS requires full update)
    /// Falls back from V2 (no auth) to V1 (with API key)
    /// </summary>
    private async Task<List<uint>> EnumerateAllAppIdsViaWebApiAsync(CancellationToken ct)
    {
        _logger.LogInformation("Attempting to enumerate app IDs via Steam Web API (V2/V1 fallback)");

        try
        {
            // Check if Web API is operational (force refresh to ensure latest configuration)
            var status = await _steamWebApiService.GetApiStatusAsync(forceRefresh: true);

            if (!status.IsFullyOperational)
            {
                _logger.LogWarning("Steam Web API is not operational: {Message}", status.Message);

                // If V1 needs an API key but none is configured, provide helpful guidance
                if (status.Version == SteamWebApiService.SteamApiVersion.V1NoKey)
                {
                    throw new InvalidOperationException(
                        "Cannot perform full PICS scan: Steam Web API V2 is currently unavailable and no V1 API key is configured. " +
                        "To resolve this issue, either: " +
                        "(1) Configure a Steam Web API key in the Management tab under 'Steam Web API Status', OR " +
                        "(2) Download pre-created depot mappings from GitHub by clicking the 'Download Pre-created Data' button in the Depot Mapping section.");
                }
                else
                {
                    throw new InvalidOperationException(
                        "Cannot perform full PICS scan: Steam Web API is currently unavailable. " +
                        "To resolve this issue, download pre-created depot mappings from GitHub by clicking the 'Download Pre-created Data' button in the Depot Mapping section.");
                }
            }

            // Get app list from Web API (V2 or V1 with key)
            var apps = await _steamWebApiService.GetAppListAsync();

            if (apps == null || apps.Count == 0)
            {
                _logger.LogError("Steam Web API returned no apps");
                throw new InvalidOperationException("Steam Web API returned no apps - service may be temporarily unavailable");
            }

            _logger.LogInformation("Steam Web API enumeration complete using {Version}. Found {Count} apps",
                status.Version, apps.Count);

            // Convert to app IDs only
            var appIds = apps.Select(a => a.AppId).OrderBy(id => id).ToList();
            return appIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate app IDs via Steam Web API");
            throw;
        }
    }

    private async Task<T> WaitForCallbackAsync<T>(AsyncJob<T> job, CancellationToken ct, TimeSpan? timeout = null) where T : CallbackMsg
    {
        if (_manager is null)
        {
            throw new InvalidOperationException("Callback manager not initialized.");
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = job.JobID;

        Action<T>? handler = null;
        handler = callback =>
        {
            if (callback.JobID == jobId)
            {
                tcs.TrySetResult(callback);
            }
        };

        using var subscription = _manager.Subscribe(handler!);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Use longer timeout for PICS operations which can be slow with unreliable connections
        linkedCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

        while (!tcs.Task.IsCompleted)
        {
            // Check if we're still connected
            if (!_isLoggedOn || _steamClient?.IsConnected != true)
            {
                // Give it a moment for auto-reconnection to kick in
                await Task.Delay(TimeSpan.FromSeconds(1), linkedCts.Token);

                // If still not connected after waiting, throw a more descriptive exception
                if (!_isLoggedOn || _steamClient?.IsConnected != true)
                {
                    throw new InvalidOperationException("Lost connection to Steam during PICS operation");
                }
            }

            _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            linkedCts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        return await tcs.Task;
    }

    private async Task<IReadOnlyList<SteamApps.PICSProductInfoCallback>> WaitForAllProductInfoAsync(
        AsyncJobMultiple<SteamApps.PICSProductInfoCallback> job,
        CancellationToken ct)
    {
        if (_manager is null)
        {
            throw new InvalidOperationException("Callback manager not initialized.");
        }

        var callbacks = new List<SteamApps.PICSProductInfoCallback>();
        var jobId = job.JobID;
        var isCompleted = false;

        Action<SteamApps.PICSProductInfoCallback>? handler = null;
        handler = callback =>
        {
            if (callback.JobID == jobId)
            {
                callbacks.Add(callback);
                if (!callback.ResponsePending) // This is the final callback
                {
                    isCompleted = true;
                }
            }
        };

        using var subscription = _manager.Subscribe(handler!);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromMinutes(10)); // batches can be slow, especially with reconnections

        while (!isCompleted && !linkedCts.Token.IsCancellationRequested)
        {
            _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            await Task.Yield();
        }

        linkedCts.Token.ThrowIfCancellationRequested();
        // Return a new list to avoid collection modification exceptions if late callbacks arrive
        return callbacks.ToList().AsReadOnly();
    }

    private static uint? AsUInt(KeyValue kv)
    {
        if (kv == KeyValue.Invalid || kv.Value == null) return null;
        if (uint.TryParse(kv.AsString() ?? string.Empty, out var v)) return v;
        return null;
    }

    private async Task<int> CheckUnmappedDownloadsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Count downloads with depot IDs but no game info
            return await context.Downloads
                .Where(d => d.Service.ToLower() == "steam"
                       && d.DepotId.HasValue
                       && !d.GameAppId.HasValue)
                .CountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check unmapped downloads");
            return 0;
        }
    }
}
