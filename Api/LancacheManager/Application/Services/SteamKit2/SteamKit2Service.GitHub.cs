using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Application.Services;

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

        try
        {
            _logger.LogInformation("[GitHub Mode] Starting download of pre-created depot data from GitHub");

            // Send start notification via SignalR
            _ = Task.Run(async () =>
            {
                try
                {
                    await _hubContext.Clients.All.SendAsync("DepotMappingStarted", new
                    {
                        scanMode = "github",
                        message = "Downloading depot mappings from GitHub...",
                        isLoggedOn = IsSteamAuthenticated,
                        timestamp = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[GitHub Mode] Failed to send DepotMappingStarted notification via SignalR");
                }
            });

            const string githubUrl = "https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json";

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            _logger.LogInformation("[GitHub Mode] Downloading from: {Url}", githubUrl);

            var response = await httpClient.GetAsync(githubUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[GitHub Mode] Failed to download: HTTP {StatusCode}", response.StatusCode);
                return false;
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                _logger.LogWarning("[GitHub Mode] Downloaded file is empty");
                return false;
            }

            // Validate JSON structure and parse GitHub data
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

            // Save to local file (overwrites existing)
            var localPath = _picsDataService.GetPicsJsonFilePath();
            await System.IO.File.WriteAllTextAsync(localPath, jsonContent, cancellationToken);
            _logger.LogInformation("[GitHub Mode] Saved pre-created depot data to: {Path}", localPath);

            // Clear cache so next load reads the new file
            _picsDataService.ClearCache();

            // Send progress update for clearing phase
            await SendGitHubProgress("Clearing existing depot mappings...", 25);

            // Full replace: Clear existing depot mappings first, then import fresh data
            // This ensures the database always matches GitHub exactly (removes stale/deleted mappings)
            _logger.LogInformation("[GitHub Mode] Clearing existing depot mappings for full replace...");
            await _picsDataService.ClearDepotMappingsAsync(cancellationToken);

            // Send progress update for import phase
            await SendGitHubProgress("Importing depot mappings to database...", 50);

            // Import fresh data from GitHub
            _logger.LogInformation("[GitHub Mode] Importing {Count} depot mappings to database (full replace mode)",
                downloadedData.DepotMappings.Count);
            await _picsDataService.ImportJsonDataToDatabaseAsync(cancellationToken);

            // Send progress update for apply phase
            await SendGitHubProgress("Applying mappings to downloads...", 90);

            // Apply depot mappings to existing downloads
            // This only updates downloads that don't have game info yet (or missing image)
            _logger.LogInformation("[GitHub Mode] Applying depot mappings to downloads without game info");
            await ManuallyApplyDepotMappings();

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
            try
            {
                var totalMappings = _depotToAppMappings.Count;
                await _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
                {
                    success = true,
                    scanMode = "github",
                    message = "GitHub depot data imported successfully",
                    totalMappings,
                    isLoggedOn = IsSteamAuthenticated,
                    timestamp = DateTime.UtcNow
                });

                _logger.LogInformation("[GitHub Mode] DepotMappingComplete notification sent successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[GitHub Mode] Failed to send DepotMappingComplete notification via SignalR");
            }

            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[GitHub Mode] Network error while downloading pre-created depot data");
            await SendGitHubErrorNotification("Network error while downloading depot data");
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "[GitHub Mode] Timeout while downloading pre-created depot data");
            await SendGitHubErrorNotification("Timeout while downloading depot data");
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GitHub Mode] Download cancelled");
            await SendGitHubErrorNotification("Download cancelled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHub Mode] Error downloading pre-created depot data");
            await SendGitHubErrorNotification($"Error downloading depot data: {ex.Message}");
            return false;
        }
        finally
        {
            // Always release the lock when done
            Interlocked.Exchange(ref _rebuildActive, 0);
        }
    }

    private async Task SendGitHubErrorNotification(string errorMessage)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
            {
                success = false,
                scanMode = "github",
                message = $"[GitHub Mode] {errorMessage}",
                error = errorMessage,
                isLoggedOn = IsSteamAuthenticated,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GitHub Mode] Failed to send error notification via SignalR");
        }
    }

    private async Task SendGitHubProgress(string message, int percentComplete)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("DepotMappingProgress", new
            {
                status = message,
                percentComplete,
                scanMode = "github",
                message,
                isLoggedOn = IsSteamAuthenticated,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GitHub Mode] Failed to send progress notification via SignalR");
        }
    }
}
