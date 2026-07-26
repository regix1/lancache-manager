using System.Text.Json;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Downloads the published depot map and replaces database mappings while preserving locally
    /// resolved orphan mappings.
    /// </summary>
    public async Task<bool> ImportFromGitHubAsync(
        CancellationToken cancellationToken = default,
        RunTrigger trigger = RunTrigger.Manual)
    {
        if (Interlocked.CompareExchange(ref _rebuildActive, 1, 0) != 0)
        {
            _logger.LogInformation("[GitHub Mode] Download already in progress, skipping duplicate request");
            return true;
        }

        _depotRunShowNotification = EffectiveNotificationMode.AllowsTrigger(trigger);
        _activeDepotScanMode = DepotScanMode.Github;
        _emitTotalMappings = 0;
        _emitDownloadsUpdated = 0;

        CancellationTokenSource runCts;
        try
        {
            runCts = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellationTokenSource.Token,
                cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

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

        async Task<bool> FailAsync(string message)
        {
            await reporter.CompleteAsync(
                success: false,
                error: message,
                stageKey: "signalr.depotMapping.github.failed",
                context: CreateDepotContext(
                    message: message,
                    errorDetail: message));
            return false;
        }

        try
        {
            await reporter.StartAsync(
                new Dictionary<string, object?>
                {
                    ["scanMode"] = DepotScanMode.Github,
                    ["message"] = "Downloading pre-created depot mappings...",
                },
                "signalr.depotMapping.github.downloading");
            _currentPicsOperationId = reporter.OperationId;
            RaiseExecutionStateChanged();

            _logger.LogInformation(
                "[GitHub Mode] Starting depot data download (operationId: {OperationId})",
                reporter.OperationId);
            await NotifyGitHubProgressAsync(reporter, "Connecting to GitHub...", 2);

            const string githubUrl =
                "https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json";
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            await NotifyGitHubProgressAsync(reporter, "Downloading depot data...", 5);
            using var response = await httpClient.GetAsync(githubUrl, reporter.Token);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"Depot mapping download failed with HTTP {(int)response.StatusCode}";
                _logger.LogWarning("[GitHub Mode] {Message}", message);
                return await FailAsync(message);
            }

            await NotifyGitHubProgressAsync(reporter, "Reading response data...", 8);
            var jsonContent = await response.Content.ReadAsStringAsync(reporter.Token);
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                return await FailAsync("Downloaded depot mapping file was empty");
            }

            await NotifyGitHubProgressAsync(reporter, "Validating JSON structure...", 10);
            PicsJsonData? downloadedData;
            try
            {
                downloadedData = JsonSerializer.Deserialize<PicsJsonData>(
                    jsonContent,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[GitHub Mode] Downloaded file is not valid JSON");
                return await FailAsync("Downloaded depot mapping file was not valid JSON");
            }

            if (downloadedData?.DepotMappings is not { Count: > 0 })
            {
                return await FailAsync("Downloaded file did not contain depot mappings");
            }

            await NotifyGitHubProgressAsync(reporter, "Saving to local file...", 15);
            var localPath = _picsDataService.GetPicsJsonFilePath();
            await File.WriteAllTextAsync(localPath, jsonContent, reporter.Token);
            _picsDataService.ClearCache();

            await NotifyGitHubProgressAsync(reporter, "Clearing existing depot mappings...", 18);
            await _picsDataService.ClearDepotMappingsAsync(
                reporter.Token,
                preserveOrphanResolved: true);
            await NotifyGitHubProgressAsync(reporter, "Depot mappings cleared", 22);

            async Task ImportProgressCallback(string message, int importPercent)
            {
                var overallPercent = 22 + (int)(0.68 * importPercent);
                await NotifyGitHubProgressAsync(reporter, message, overallPercent);
            }

            await _picsDataService.ImportToDatabaseAsync(reporter.Token, ImportProgressCallback);
            await NotifyGitHubProgressAsync(reporter, "Applying mappings to downloads...", 90);
            await ManuallyApplyDepotMappingsCoreAsync(reporter, reporter.Token);
            await NotifyGitHubProgressAsync(reporter, "Finalizing import...", 98);

            ClearViabilityCache();
            _emitTotalMappings = _depotToAppMappings.Count;
            await reporter.CompleteAsync(
                success: true,
                stageKey: "signalr.depotMapping.github.complete",
                context: CreateDepotContext(
                    message: $"Depot mapping completed - {_emitTotalMappings} mappings",
                    depotMappingsFound: _emitTotalMappings,
                    totalMappings: _emitTotalMappings,
                    mappingsApplied: _emitDownloadsUpdated));
            return true;
        }
        catch (TaskCanceledException ex)
            when (ex.InnerException is TimeoutException && !runCts.IsCancellationRequested)
        {
            _logger.LogError(ex, "[GitHub Mode] Depot mapping download timed out");
            return await FailAsync("Depot mapping download timed out");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[GitHub Mode] Network error downloading depot mappings");
            return await FailAsync($"Network error downloading depot mappings: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GitHub Mode] Depot mapping download cancelled");
            await reporter.CompleteAsync(
                success: false,
                error: "Cancelled by user",
                cancelled: true,
                stageKey: "signalr.depotMapping.cancelled",
                context: CreateDepotContext(message: "Depot mapping download cancelled"));
            UpdateLastCrawlTime();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GitHub Mode] Error downloading depot mappings");
            return await FailAsync($"Error downloading depot mappings: {ex.Message}");
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

    private Task NotifyGitHubProgressAsync(
        MappingOperationReporter reporter,
        string message,
        int percentComplete) =>
        reporter.ReportAsync(
            percentComplete,
            "signalr.depotMapping.github.downloading",
            CreateDepotContext(status: message, message: message));
}
