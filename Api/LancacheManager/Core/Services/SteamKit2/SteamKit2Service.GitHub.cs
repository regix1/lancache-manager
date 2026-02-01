using System.Text.Json;
using LancacheManager.Core.Models;
using LancacheManager.Hubs;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Download pre-created depot mappings from GitHub and perform a full replace in the database.
    /// This ensures the database always matches GitHub exactly (no stale mappings from previous imports).
    /// This is used for the "GitHub mode" in periodic scans.
    /// </summary>
    public async Task<bool> DownloadAndImportGitHubDataAsync(CancellationToken cancellationToken = default)
    {
        // Prevent concurrent downloads - if already running, log and return immediately
        if (Interlocked.CompareExchange(ref _rebuildActive, 1, 0) != 0)
        {
            _logger.LogInformation("[GitHub Mode] Download already in progress, skipping duplicate request");
            return true; // Return true to indicate "no error, just already running"
        }

        // Register with unified operation tracker for cancellation support
        var cts = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(
            OperationType.DepotMapping,
            "Depot Mapping (GitHub)",
            cts);

        // Link the provided cancellation token
        var linkedCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token)
            : cts;
        var token = linkedCts.Token;

        try
        {
            _logger.LogInformation("[GitHub Mode] Starting download of pre-created depot data from GitHub (operationId: {OperationId})", operationId);

            // Send start notification via SignalR
            _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingStarted, new
            {
                operationId,
                scanMode = "github",
                message = "Downloading depot mappings from GitHub...",
                isLoggedOn = IsSteamAuthenticated,
                timestamp = DateTime.UtcNow
            });

            // Phase 1: Connect and download (0-10%)
            await SendGitHubProgress("Connecting to GitHub...", 2, operationId);

            const string githubUrl = "https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json";

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            _logger.LogInformation("[GitHub Mode] Downloading from: {Url}", githubUrl);

            await SendGitHubProgress("Downloading depot data...", 5, operationId);
            var response = await httpClient.GetAsync(githubUrl, token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[GitHub Mode] Failed to download: HTTP {StatusCode}", response.StatusCode);
                return false;
            }

            await SendGitHubProgress("Reading response data...", 8, operationId);
            var jsonContent = await response.Content.ReadAsStringAsync(token);

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                _logger.LogWarning("[GitHub Mode] Downloaded file is empty");
                return false;
            }

            // Phase 2: Validate JSON (10-15%)
            await SendGitHubProgress("Validating JSON structure...", 10, operationId);

            PicsJsonData? downloadedData;
            try
            {
                downloadedData = JsonSerializer.Deserialize<PicsJsonData>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (downloadedData?.DepotMappings == null || !downloadedData.DepotMappings.Any())
                {
                    _logger.LogWarning("[GitHub Mode] Downloaded file does not contain valid depot mappings");
                    return false;
                }

                _logger.LogInformation("[GitHub Mode] Downloaded {Count} depot mappings (change number: {ChangeNumber})",
                    downloadedData.Metadata?.TotalMappings ?? 0,
                    downloadedData.Metadata?.LastChangeNumber ?? 0);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[GitHub Mode] Downloaded file is not valid JSON");
                return false;
            }

            // Phase 3: Save to local file (15-18%)
            await SendGitHubProgress("Saving to local file...", 15, operationId);
            var localPath = _picsDataService.GetPicsJsonFilePath();
            await System.IO.File.WriteAllTextAsync(localPath, jsonContent, token);
            _logger.LogInformation("[GitHub Mode] Saved pre-created depot data to: {Path}", localPath);

            // Clear cache so next load reads the new file
            _picsDataService.ClearCache();

            // Phase 4: Clear existing mappings (18-22%)
            await SendGitHubProgress("Clearing existing depot mappings...", 18, operationId);

            // Full replace: Clear existing depot mappings first, then import fresh data
            // This ensures the database always matches GitHub exactly (removes stale/deleted mappings)
            _logger.LogInformation("[GitHub Mode] Clearing existing depot mappings for full replace...");
            await _picsDataService.ClearDepotMappingsAsync(token);

            await SendGitHubProgress("Depot mappings cleared", 22, operationId);

            // Phase 5: Import to database (22-90%) - uses progress callback for granular updates
            _logger.LogInformation("[GitHub Mode] Importing {Count} depot mappings to database (full replace mode)",
                downloadedData.DepotMappings.Count);

            // Progress callback that maps import progress (0-100%) to our range (22-90%)
            async Task ImportProgressCallback(string message, int importPercent)
            {
                // Map 0-100% import progress to 22-90% overall progress
                var overallPercent = 22 + (int)(0.68 * importPercent);
                await SendGitHubProgress(message, overallPercent, operationId);
            }

            await _picsDataService.ImportJsonDataToDatabaseAsync(token, ImportProgressCallback);

            // Phase 6: Apply mappings to downloads (90-98%)
            await SendGitHubProgress("Applying mappings to downloads...", 90, operationId);

            // Apply depot mappings to existing downloads
            // This only updates downloads that don't have game info yet (or missing image)
            _logger.LogInformation("[GitHub Mode] Applying depot mappings to downloads without game info");
            await ManuallyApplyDepotMappings();

            // Phase 7: Finalize (98-100%)
            await SendGitHubProgress("Finalizing import...", 98, operationId);

            _logger.LogInformation("[GitHub Mode] Pre-created depot data downloaded and imported successfully");

            // Clear cached viability check since we just imported fresh data from GitHub
            var state = _stateService.GetState();
            state.RequiresFullScan = false;
            state.LastViabilityCheck = null;
            state.LastViabilityCheckChangeNumber = 0;
            state.ViabilityChangeGap = 0;
            _stateService.SaveState(state);
            _logger.LogInformation("[GitHub Mode] Cleared cached viability check - system is now up to date with GitHub data");

            // Send completion notification via SignalR
            var totalMappings = _depotToAppMappings.Count;
            await _notifications.NotifyAllAsync(SignalREvents.DepotMappingComplete, new
            {
                operationId,
                success = true,
                scanMode = "github",
                message = "GitHub depot data imported successfully",
                totalMappings,
                isLoggedOn = IsSteamAuthenticated,
                timestamp = DateTime.UtcNow
            });

            _logger.LogInformation("[GitHub Mode] DepotMappingComplete notification sent successfully");
            _operationTracker.CompleteOperation(operationId, true);

            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[GitHub Mode] Network error while downloading pre-created depot data");
            _operationTracker.CompleteOperation(operationId, false, "Network error");
            await SendGitHubErrorNotification("Network error while downloading depot data", operationId);
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "[GitHub Mode] Timeout while downloading pre-created depot data");
            _operationTracker.CompleteOperation(operationId, false, "Timeout");
            await SendGitHubErrorNotification("Timeout while downloading depot data", operationId);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GitHub Mode] Download cancelled");
            _operationTracker.CompleteOperation(operationId, false, "Cancelled");

            // Reset the schedule timer so next run is at full interval
            UpdateLastCrawlTime();
            _logger.LogInformation("[GitHub Mode] Reset depot mapping schedule timer - next run in {Interval}", _crawlInterval);

            // Send cancellation notification via SignalR
            await _notifications.NotifyAllAsync(SignalREvents.DepotMappingComplete, new
            {
                operationId,
                success = false,
                cancelled = true,
                scanMode = "github",
                message = "Depot mapping scan cancelled",
                isLoggedOn = IsSteamAuthenticated,
                timestamp = DateTime.UtcNow
            });

            // Re-throw so the controller/middleware handles cancellation properly
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHub Mode] Error downloading pre-created depot data");
            _operationTracker.CompleteOperation(operationId, false, ex.Message);
            await SendGitHubErrorNotification($"Error downloading depot data: {ex.Message}", operationId);
            return false;
        }
        finally
        {
            // Always release the lock when done
            Interlocked.Exchange(ref _rebuildActive, 0);
            linkedCts.Dispose();
        }
    }

    private async Task SendGitHubErrorNotification(string errorMessage, string? operationId = null)
    {
        await _notifications.NotifyAllAsync(SignalREvents.DepotMappingComplete, new
        {
            operationId,
            success = false,
            scanMode = "github",
            message = $"[GitHub Mode] {errorMessage}",
            error = errorMessage,
            isLoggedOn = IsSteamAuthenticated,
            timestamp = DateTime.UtcNow
        });
    }

    private async Task SendGitHubProgress(string message, int percentComplete, string? operationId = null)
    {
        await _notifications.NotifyAllAsync(SignalREvents.DepotMappingProgress, new
        {
            operationId,
            status = message,
            percentComplete,
            scanMode = "github",
            message,
            isLoggedOn = IsSteamAuthenticated,
            timestamp = DateTime.UtcNow
        });
    }
}
