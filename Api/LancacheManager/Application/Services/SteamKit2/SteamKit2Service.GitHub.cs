using System.Text.Json;

namespace LancacheManager.Application.Services;

public partial class SteamKit2Service
{
    /// <summary>
    /// Download pre-created depot mappings from GitHub and import them
    /// This is used for the "GitHub mode" in periodic scans
    /// </summary>
    public async Task<bool> DownloadAndImportGitHubDataAsync(CancellationToken cancellationToken = default)
    {
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

            // Validate JSON structure
            try
            {
                var testData = JsonSerializer.Deserialize<PicsJsonData>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (testData?.DepotMappings == null || !testData.DepotMappings.Any())
                {
                    _logger.LogWarning("[GitHub Mode] Downloaded file does not contain valid depot mappings");
                    return false;
                }

                _logger.LogInformation("[GitHub Mode] Downloaded {Count} depot mappings", testData.Metadata?.TotalMappings ?? 0);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[GitHub Mode] Downloaded file is not valid JSON");
                return false;
            }

            // Save to local file
            var localPath = _picsDataService.GetPicsJsonFilePath();
            await System.IO.File.WriteAllTextAsync(localPath, jsonContent, cancellationToken);

            _logger.LogInformation("[GitHub Mode] Saved pre-created depot data to: {Path}", localPath);

            // Clear existing depot mappings before importing (GitHub download is a full replacement)
            _logger.LogInformation("[GitHub Mode] Clearing existing depot mappings for full replacement");
            await _picsDataService.ClearDepotMappingsAsync(cancellationToken);

            // Import to database
            await _picsDataService.ImportJsonDataToDatabaseAsync(cancellationToken);

            // Apply depot mappings to existing downloads
            _logger.LogInformation("[GitHub Mode] Applying depot mappings to existing downloads");
            await ManuallyApplyDepotMappings();

            _logger.LogInformation("[GitHub Mode] Pre-created depot data downloaded and imported successfully");

            // Send completion notification via SignalR
            try
            {
                var totalMappings = _depotToAppMappings.Count;
                await _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
                {
                    success = true,
                    message = "GitHub depot data downloaded and imported successfully",
                    totalMappings,
                    isLoggedOn = IsSteamAuthenticated,
                    timestamp = DateTime.UtcNow
                });
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
    }

    private async Task SendGitHubErrorNotification(string errorMessage)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
            {
                success = false,
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
}
