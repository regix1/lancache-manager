using System.Text.Json;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

/// <summary>
/// Background service that automatically downloads depot data from GitHub on first run
/// when the database is empty
/// </summary>
public class DepotDataInitializationService : IHostedService
{
    private readonly ILogger<DepotDataInitializationService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PicsDataService _picsDataService;

    public DepotDataInitializationService(
        ILogger<DepotDataInitializationService> logger,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        PicsDataService picsDataService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _picsDataService = picsDataService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Check if database has depot mappings
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var depotCount = await context.SteamDepotMappings.CountAsync(cancellationToken);

            if (depotCount > 0)
            {
                _logger.LogInformation("Depot mappings already exist ({Count} mappings). Skipping auto-download.", depotCount);
                return;
            }

            _logger.LogInformation("No depot mappings found. Auto-downloading from GitHub...");

            // Download from GitHub releases
            const string githubUrl = "https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json";

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            _logger.LogInformation("Downloading depot data from: {Url}", githubUrl);

            var response = await httpClient.GetAsync(githubUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to auto-download depot data: HTTP {StatusCode}. Users will need to manually download.", response.StatusCode);
                return;
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                _logger.LogWarning("Downloaded file is empty. Auto-download failed.");
                return;
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
                    _logger.LogWarning("Downloaded file does not contain valid depot mappings");
                    return;
                }

                _logger.LogInformation("Successfully downloaded {Count} depot mappings", testData.Metadata?.TotalMappings ?? 0);

                // Save to local file
                var localPath = _picsDataService.GetPicsJsonFilePath();
                await File.WriteAllTextAsync(localPath, jsonContent, cancellationToken);

                _logger.LogInformation("Saved depot data to: {Path}", localPath);

                // Import to database
                await _picsDataService.ImportJsonDataToDatabaseAsync(cancellationToken);

                _logger.LogInformation("Auto-download and import completed successfully");
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Downloaded file is not valid JSON format");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error during auto-download. Users will need to manually download depot data.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Auto-download timed out. Users will need to manually download depot data.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during auto-download initialization");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
