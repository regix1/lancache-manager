using System.Globalization;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Middleware;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Downloads the published depot map and adds it to the database mappings, keeping every mapping
    /// another source found.
    /// </summary>
    public async Task<bool> ImportFromGitHubAsync(
        CancellationToken cancellationToken = default,
        RunTrigger trigger = RunTrigger.Manual,
        RunNotice? notice = null,
        TaskCompletionSource<Guid?>? started = null)
    {
        if (Interlocked.CompareExchange(ref _rebuildActive, 1, 0) != 0)
        {
            started?.TrySetResult(null);
            throw new ConflictException("A depot mapping operation is already running.")
            {
                StageKey = "errors.depot.mappingInProgress"
            };
        }

        lock (_baselineLock) _baselineCommitted = false;

        var runNotice = notice ?? new RunNotice(EffectiveNotificationMode, trigger);
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
        await using var reporter = CreateTrackedRebuildReporter(runCts, runNotice);
        var committed = false;
        var runToken = reporter.Token;

        // The stage key rides as the error too, so the run row, another browser and Schedule History keep
        // the reason in the reader's language; the values its sentence names ride in the context.
        async Task<bool> FailAsync(string stageKey, string? errorDetail = null)
        {
            await reporter.CompleteAsync(
                success: false,
                error: stageKey,
                stageKey: stageKey,
                context: CreateDepotContext(errorDetail: errorDetail));
            return false;
        }

        try
        {
            await reporter.StartAsync(
                new Dictionary<string, object?>
                {
                    ["scanMode"] = DepotScanMode.Github,
                },
                "signalr.depotMapping.github.downloading");
            _currentPicsOperationId = reporter.OperationId;
            // Lets a caller hold the queue lock until this run is registered, and no longer.
            started?.TrySetResult(reporter.OperationId);
            RaiseExecutionStateChanged();

            _logger.LogInformation(
                "[GitHub Mode] Starting depot data download (operationId: {OperationId})",
                reporter.OperationId);
            await NotifyGitHubProgressAsync(reporter, 2);

            const string githubUrl =
                "https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json";
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            await NotifyGitHubProgressAsync(reporter, 5);
            using var response = await httpClient.GetAsync(githubUrl, reporter.Token);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"Depot mapping download failed with HTTP {(int)response.StatusCode}";
                _logger.LogWarning("[GitHub Mode] {Message}", message);
                return await FailAsync(
                    "signalr.depotMapping.github.httpFailed",
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            await NotifyGitHubProgressAsync(reporter, 8);
            var jsonContent = await response.Content.ReadAsStringAsync(reporter.Token);
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                return await FailAsync("signalr.depotMapping.github.emptyFile");
            }

            await NotifyGitHubProgressAsync(reporter, 10);
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
                return await FailAsync("signalr.depotMapping.github.invalidJson");
            }

            if (downloadedData?.DepotMappings is not { Count: > 0 })
            {
                return await FailAsync("signalr.depotMapping.github.noMappings");
            }

            PicsDataService.ValidateSnapshot(downloadedData, reporter.Token);
            var baseline = await GetDepotBaselineAsync();
            if (downloadedData.Metadata!.LastChangeNumber < baseline.LastChangeNumber)
                return await FailAsync("signalr.depotMapping.github.olderSnapshot");
            reporter.Token.ThrowIfCancellationRequested();
            await PinBaselineAsync();

            async Task ImportProgressCallback(int importPercent)
            {
                var overallPercent = 22 + (int)(0.68 * importPercent);
                await NotifyGitHubProgressAsync(reporter, overallPercent);
            }

            // The restore runs inside the import's transaction, so no detection sees the import
            // without the prefill-only depots. Assigned per attempt, since a retried attempt reruns it.
            var restoreFailed = false;
            await _picsDataService.ImportToDatabaseAsync(downloadedData, reporter.Token, ImportProgressCallback,
                async (AppDbContext db) => restoreFailed = await _prefillCache.RestoreDepotMappingsAsync(db) > 0);
            if (restoreFailed)
            {
                // The PICS rows are committed; names for prefill-only depots return with the next GitHub import.
                _logger.LogWarning("[GitHub Mode] Could not restore prefill depot mappings after the import");
            }
            await reporter.ReportAsync(90, "signalr.depotMapping.applyingToDownloads", CreateDepotContext());
            await ManuallyApplyDepotMappingsCoreAsync(reporter, reporter.Token, replace: true);
            await NotifyGitHubProgressAsync(reporter, 98);

            _picsDataService.WritePicsJsonFile(jsonContent);
            _emitTotalMappings = _depotToAppMappings.Count;
            if (restoreFailed)
            {
                // The import committed, so the checkpoint stands and the next crawl does not redo it.
                runToken.ThrowIfCancellationRequested();
                UpdateLastCrawlTime(downloadedData.Metadata.LastChangeNumber);
                committed = true;
                const string message = "Prefill depot names could not be restored after the import";
                await reporter.CompleteAsync(
                    success: false,
                    error: "signalr.depotMapping.github.prefillNamesNotRestored",
                    stageKey: "signalr.depotMapping.github.prefillNamesNotRestored",
                    context: CreateDepotContext(errorDetail: message));
                return false;
            }
            await reporter.CompleteAsync(
                success: true,
                stageKey: "signalr.depotMapping.github.complete",
                context: CreateDepotContext(
                    depotMappingsFound: _emitTotalMappings,
                    totalMappings: _emitTotalMappings,
                    mappingsApplied: _emitDownloadsUpdated),
                commit: () =>
                {
                    runToken.ThrowIfCancellationRequested();
                    UpdateLastCrawlTime(downloadedData.Metadata.LastChangeNumber);
                    committed = true;
                });
            return true;
        }
        catch (TaskCanceledException ex)
            when (ex.InnerException is TimeoutException && !runCts.IsCancellationRequested)
        {
            _logger.LogError(ex, "[GitHub Mode] Depot mapping download timed out");
            return await FailAsync("signalr.depotMapping.github.timedOut");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[GitHub Mode] Network error downloading depot mappings");
            return await FailAsync(
                "signalr.depotMapping.github.networkError",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GitHub Mode] Depot mapping download canceled");
            await reporter.CompleteAsync(
                success: false,
                canceled: true,
                stageKey: "signalr.depotMapping.canceled",
                context: CreateDepotContext());
            throw;
        }
        catch (Exception ex)
        {
            if (committed)
            {
                _logger.LogWarning(ex, "Depot mappings committed but completion notification failed");
                return true;
            }
            _logger.LogError(ex, "[GitHub Mode] Error downloading depot mappings");
            return await FailAsync(
                "signalr.depotMapping.github.failed",
                ex.Message);
        }
        finally
        {
            started?.TrySetResult(null);
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

    /// <summary>
    /// Imports the depot map already on disk as a tracked depot run, so it takes the single-run flag,
    /// draws a card and is refused while another depot run is active, like the GitHub import. It does
    /// not replace the committed baseline or stamp a crawl: the file carries no change number this
    /// run can vouch for.
    /// </summary>
    public async Task ImportFromLocalFileAsync(
        CancellationToken cancellationToken,
        RunNotice notice,
        TaskCompletionSource<Guid?> started)
    {
        if (Interlocked.CompareExchange(ref _rebuildActive, 1, 0) != 0)
        {
            started.TrySetResult(null);
            throw new ConflictException("A depot mapping operation is already running.")
            {
                StageKey = "errors.depot.mappingInProgress"
            };
        }

        // An earlier crawl's committed baseline would refuse this run's cancel.
        lock (_baselineLock) _baselineCommitted = false;
        // Not a GitHub download; labeled like the other run that only writes mappings (apply to downloads).
        _activeDepotScanMode = DepotScanMode.Incremental;

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
        await using var reporter = CreateTrackedRebuildReporter(runCts, notice);

        try
        {
            await reporter.StartAsync(stageKey: "signalr.depotMapping.importing");
            _currentPicsOperationId = reporter.OperationId;
            // Lets a caller hold the queue lock until this run is registered, and no longer.
            started.TrySetResult(reporter.OperationId);
            RaiseExecutionStateChanged();

            await _picsDataService.ImportToDatabaseAsync(reporter.Token);
            await reporter.CompleteAsync(
                success: true,
                stageKey: "signalr.depotMapping.completed",
                context: CreateDepotContext());
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Local depot mapping import canceled");
            await reporter.CompleteAsync(
                success: false,
                canceled: true,
                stageKey: "signalr.depotMapping.canceled",
                context: CreateDepotContext());
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing local depot mappings");
            await reporter.CompleteAsync(
                success: false,
                error: "signalr.depotMapping.github.failed",
                stageKey: "signalr.depotMapping.github.failed",
                context: CreateDepotContext(errorDetail: ex.Message));
            throw;
        }
        finally
        {
            started.TrySetResult(null);
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
        int percentComplete) =>
        reporter.ReportAsync(
            percentComplete,
            "signalr.depotMapping.github.downloading",
            CreateDepotContext());
}
