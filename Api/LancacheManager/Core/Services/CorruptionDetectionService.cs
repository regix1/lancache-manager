using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Runs corruption scans and owns the one authoritative persisted scan snapshot.
/// Summary, details, and removal scopes are projections of that immutable evidence.
/// </summary>
public class CorruptionDetectionService
{
    private static readonly HashSet<int> _allowedThresholds = [3, 5, 10];
    private static readonly JsonSerializerOptions _candidateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<CorruptionDetectionService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly OperationStateService _operationStateService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private static readonly string _operationStateKey = OperationType.CorruptionDetection.ToWireString();

    public CorruptionDetectionService(
        ILogger<CorruptionDetectionService> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        ISignalRNotificationService notifications,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        OperationStateService operationStateService,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _notifications = notifications;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;
        _operationStateService = operationStateService;
        _operationTracker = operationTracker;
    }

    /// <summary>Starts a canonical three-mode corruption scan.</summary>
    public async Task<Guid> StartDetectionAsync(
        int threshold = 3,
        string detectionMode = "cache_and_logs",
        CancellationToken cancellationToken = default)
    {
        var mode = ValidateScanInput(detectionMode, threshold);

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            var activeOp = _operationTracker.GetActiveOperations(OperationType.CorruptionDetection).FirstOrDefault();
            if (activeOp != null)
            {
                _logger.LogWarning("[CorruptionDetection] Detection already in progress: {OperationId}", activeOp.Id);
                return activeOp.Id;
            }

            var cts = new CancellationTokenSource();
            var metadata = new CorruptionDetectionMetrics
            {
                DetectionMode = mode,
                Threshold = threshold
            };
            Guid operationId = Guid.Empty;
            operationId = _operationTracker.RegisterOperation(
                OperationType.CorruptionDetection,
                "Corruption Detection",
                cts,
                metadata,
                onTerminalEmit: info => EmitTerminalAsync(info, operationId, metadata));

            _operationStateService.SaveState($"{_operationStateKey}_{operationId}", new OperationState
            {
                Key = $"{_operationStateKey}_{operationId}",
                Type = OperationType.CorruptionDetection.ToWireString(),
                Status = OperationStatus.Running.ToWireString(),
                Message = "Starting corruption detection..."
            });

            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionStarted, new
            {
                OperationId = operationId,
                StageKey = "signalr.corruptionDetect.starting"
            });

            var startedAtUtc = DateTime.UtcNow;
            var token = cts.Token;
            _ = Task.Run(
                () => RunDetectionAsync(operationId, threshold, mode, startedAtUtc, token),
                token);

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private Task EmitTerminalAsync(
        OperationTerminalInfo info,
        Guid operationId,
        CorruptionDetectionMetrics metadata)
    {
        var counts = metadata.CorruptionCounts;
        var totalServicesWithCorruption = counts?.Count ?? 0;
        var totalCorruptedChunks = counts != null ? (int)Math.Min(counts.Values.Sum(), int.MaxValue) : 0;

        if (info.Cancelled)
        {
            return _notifications.NotifyAllAsync(
                SignalREvents.CorruptionDetectionComplete,
                new SignalRNotifications.CorruptionDetectionComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.corruptionDetect.cancelled",
                    Status: OperationStatus.Cancelled,
                    Cancelled: true));
        }

        if (info.Success)
        {
            return _notifications.NotifyAllAsync(
                SignalREvents.CorruptionDetectionComplete,
                new SignalRNotifications.CorruptionDetectionComplete(
                    Success: true,
                    OperationId: operationId,
                    StageKey: "signalr.corruptionDetect.complete",
                    Status: OperationStatus.Completed,
                    Cancelled: false,
                    TotalServicesWithCorruption: totalServicesWithCorruption,
                    TotalCorruptedChunks: totalCorruptedChunks,
                    Context: new Dictionary<string, object?>
                    {
                        ["count"] = totalServicesWithCorruption,
                        ["scanId"] = metadata.ScanId
                    }));
        }

        return _notifications.NotifyAllAsync(
            SignalREvents.CorruptionDetectionComplete,
            new SignalRNotifications.CorruptionDetectionComplete(
                Success: false,
                OperationId: operationId,
                StageKey: "signalr.corruptionDetect.failed",
                Status: OperationStatus.Failed,
                Cancelled: false,
                Error: info.Error,
                Context: new Dictionary<string, object?> { ["errorDetail"] = info.Error }));
    }

    private async Task RunDetectionAsync(
        Guid operationId,
        int threshold,
        CorruptionDetectionMode mode,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_operationTracker.GetOperation(operationId) == null)
        {
            _logger.LogWarning("[CorruptionDetection] Operation not found: {OperationId}", operationId);
            return;
        }

        try
        {
            var datasourceReports = new List<DatasourceCorruptionReport>();
            var datasources = _datasourceService.GetDatasources();
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();
            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, "Corruption manager");

            _logger.LogInformation(
                "[CorruptionDetection] Starting {Mode} detection for {Count} datasource(s)",
                mode.ToWireString(),
                datasources.Count);

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var datasource in datasources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var report = await GetReportForDatasourceAsync(
                    datasource.LogPath,
                    datasource.CachePath,
                    timezone,
                    rustBinaryPath,
                    operationId,
                    datasource.Name,
                    threshold,
                    mode,
                    cancellationToken);
                datasourceReports.Add(new DatasourceCorruptionReport(datasource.Name, report));
            }

            var completedAtUtc = DateTime.UtcNow;
            var scanId = Guid.NewGuid();
            await PersistCompletedScanAsync(
                scanId,
                mode,
                threshold,
                startedAtUtc,
                completedAtUtc,
                datasourceReports,
                cancellationToken);

            var counts = ProjectCounts(datasourceReports.SelectMany(report => report.Report.Candidates));
            _operationTracker.UpdateProgress(operationId, 100, "signalr.corruptionDetect.complete");
            _operationTracker.UpdateMetadata(operationId, metadata =>
            {
                var metrics = (CorruptionDetectionMetrics)metadata;
                metrics.ScanId = scanId;
                metrics.DetectionMode = mode;
                metrics.Threshold = threshold;
                metrics.CorruptionCounts = counts;
                metrics.LastDetectionTime = completedAtUtc;
            });

            _operationStateService.RemoveState($"{_operationStateKey}_{operationId}");
            _operationTracker.CompleteOperation(operationId, success: true);
            _logger.LogInformation(
                "[CorruptionDetection] Scan {ScanId} complete: {Services}",
                scanId,
                string.Join(", ", counts.Select(pair => $"{pair.Key}={pair.Value}")));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[CorruptionDetection] Operation {OperationId} was cancelled", operationId);
            _operationStateService.RemoveState($"{_operationStateKey}_{operationId}");
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CorruptionDetection] Detection failed for operation {OperationId}", operationId);
            _operationStateService.RemoveState($"{_operationStateKey}_{operationId}");
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
    }

    private async Task<CorruptionReport> GetReportForDatasourceAsync(
        string logDir,
        string cacheDir,
        string timezone,
        string rustBinaryPath,
        Guid operationId,
        string datasourceName,
        int threshold,
        CorruptionDetectionMode mode,
        CancellationToken cancellationToken)
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var progressFile = Path.Combine(operationsDir, $"corruption_detection_{operationId}_{datasourceName}.json");

        try
        {
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"summary \"{logDir}\" \"{cacheDir}\" \"{progressFile}\" \"{timezone}\" {threshold} --mode {mode.ToWireString()}");

            var lastMessage = string.Empty;
            var lastPercent = 0.0;
            const double percentThreshold = 5.0;
            var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<CorruptionDetectionProgressData>(
                startInfo,
                operationId,
                cancellationToken,
                progressFile,
                async progressData =>
                {
                    var keyChanged = progressData.StageKey != lastMessage;
                    var percentChanged = Math.Abs(progressData.PercentComplete - lastPercent) >= percentThreshold;
                    if (!keyChanged && !percentChanged)
                    {
                        return;
                    }

                    lastMessage = progressData.StageKey ?? string.Empty;
                    lastPercent = progressData.PercentComplete;
                    await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionProgress, new
                    {
                        OperationId = operationId,
                        PercentComplete = progressData.PercentComplete,
                        Status = OperationStatus.Running,
                        StageKey = progressData.StageKey,
                        Context = progressData.Context,
                        filesProcessed = progressData.FilesProcessed,
                        totalFiles = progressData.TotalFiles,
                        currentFile = progressData.CurrentFile,
                        datasourceName
                    });
                },
                "corruption_manager");

            if (!string.IsNullOrEmpty(result.Error))
            {
                _logger.LogDebug(
                    "[CorruptionDetection] corruption_manager stderr for {Datasource}: {Error}",
                    datasourceName,
                    result.Error);
            }

            result.EnsureSuccess("corruption_manager", datasourceName);
            var report = JsonSerializer.Deserialize<CorruptionReport>(result.Output, _candidateJsonOptions)
                ?? throw new InvalidDataException(
                    $"corruption_manager returned an empty report for datasource '{datasourceName}'");
            ValidateAndAttachDatasource(report, datasourceName, mode, threshold);
            return report;
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(progressFile);
        }
    }

    internal static void ValidateAndAttachDatasource(
        CorruptionReport report,
        string datasourceName,
        CorruptionDetectionMode expectedMode,
        int expectedThreshold)
    {
        if (report.ContractVersion != CorruptionReport.SupportedContractVersion)
        {
            throw new InvalidDataException(
                $"Unsupported corruption report contract version {report.ContractVersion}");
        }

        if (report.Mode != expectedMode || report.Threshold != expectedThreshold)
        {
            throw new InvalidDataException("Corruption report mode or threshold did not match the requested scan");
        }

        report.Candidates ??= [];
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in report.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.CandidateId) || string.IsNullOrWhiteSpace(candidate.Service))
            {
                throw new InvalidDataException("Corruption report contained a candidate without an identity or service");
            }

            if (candidate.Mode != expectedMode || candidate.Threshold != expectedThreshold)
            {
                throw new InvalidDataException(
                    "Corruption candidate mode or threshold did not match its report");
            }

            candidate.Datasource = datasourceName;
            candidate.CandidateId = $"{datasourceName}:{candidate.CandidateId}";
            candidate.ExactPaths ??= [];
            candidate.Observations ??= [];
            candidate.ExactPaths = candidate.ExactPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            var hasExactPath = candidate.ExactPaths.Count > 0
                && candidate.ValidationState.Equals("exact_path_present", StringComparison.OrdinalIgnoreCase);
            candidate.RemovalAllowed = expectedMode != CorruptionDetectionMode.LogsOnly
                && candidate.RemovalAllowed
                && hasExactPath;

            if (expectedMode == CorruptionDetectionMode.CacheAndLogs && !candidate.RemovalAllowed)
            {
                throw new InvalidDataException(
                    "Cache + Logs report contained a candidate without exact-path validation");
            }

            if (!candidateIds.Add(candidate.CandidateId))
            {
                throw new InvalidDataException($"Duplicate corruption candidate ID '{candidate.CandidateId}'");
            }
        }

        report.Candidates = report.Candidates
            .OrderBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        report.ServiceCounts = ProjectCounts(report.Candidates);
        report.Total = report.Candidates.Count;
    }

    internal async Task PersistCompletedScanAsync(
        Guid scanId,
        CorruptionDetectionMode mode,
        int threshold,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        IReadOnlyList<DatasourceCorruptionReport> datasourceReports,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await dbContext.CachedCorruptionDetections.ExecuteDeleteAsync(cancellationToken);
                await dbContext.CachedCorruptionScans.ExecuteDeleteAsync(cancellationToken);

                var contractVersions = datasourceReports
                    .Select(report => report.Report.ContractVersion)
                    .Distinct()
                    .ToList();
                if (contractVersions.Count > 1)
                {
                    throw new InvalidDataException("Datasources returned different corruption contract versions");
                }

                var contractVersion = contractVersions.SingleOrDefault(CorruptionReport.SupportedContractVersion);
                dbContext.CachedCorruptionScans.Add(new CachedCorruptionScan
                {
                    ScanId = scanId,
                    DetectionMode = mode,
                    Threshold = threshold,
                    ContractVersion = contractVersion,
                    Status = OperationStatus.Completed.ToWireString(),
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    CreatedAtUtc = completedAtUtc
                });

                foreach (var datasourceReport in datasourceReports)
                {
                    foreach (var serviceGroup in datasourceReport.Report.Candidates
                                 .GroupBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase))
                    {
                        var candidates = serviceGroup.OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToList();
                        dbContext.CachedCorruptionDetections.Add(new CachedCorruptionDetection
                        {
                            ScanId = scanId,
                            ServiceName = serviceGroup.Key,
                            DatasourceName = datasourceReport.DatasourceName,
                            CorruptedChunkCount = candidates.Count,
                            CandidatesJson = SerializeCandidates(candidates),
                            RemovalAllowed = candidates.Any(candidate => candidate.RemovalAllowed),
                            LastDetectedUtc = completedAtUtc,
                            CreatedAtUtc = completedAtUtc
                        });
                    }
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "[CorruptionDetection] Failed to persist scan {ScanId}; rolling back", scanId);
                throw;
            }
        });
    }

    /// <summary>Returns the authoritative completed scan, including an empty scan.</summary>
    public async Task<CachedCorruptionResult?> GetDetectionAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await dbContext.CachedCorruptionScans
            .AsNoTracking()
            .OrderByDescending(item => item.CompletedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (scan == null)
        {
            return null;
        }

        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scan.ScanId)
            .ToListAsync(cancellationToken);
        var candidates = rows.SelectMany(DeserializeCandidates).ToList();
        var counts = ProjectCounts(candidates);
        var serviceRemovalAllowed = candidates
            .GroupBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Any(candidate => candidate.RemovalAllowed),
                StringComparer.OrdinalIgnoreCase);

        return new CachedCorruptionResult
        {
            HasCachedResults = true,
            ScanId = scan.ScanId,
            DetectionMode = scan.DetectionMode,
            Threshold = scan.Threshold,
            ContractVersion = scan.ContractVersion,
            CorruptionCounts = counts,
            LastDetectionTime = scan.CompletedAtUtc,
            TotalServicesWithCorruption = counts.Count,
            TotalCorruptedChunks = counts.Values.Sum(),
            RemovalAllowed = candidates.Any(candidate => candidate.RemovalAllowed),
            ServiceRemovalAllowed = serviceRemovalAllowed
        };
    }

    /// <summary>Loads stored candidates for one service without rerunning detection.</summary>
    public async Task<IReadOnlyList<CorruptionCandidate>> GetDetailsAsync(
        Guid scanId,
        string service,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await RequireCurrentScanAsync(dbContext, scanId, cancellationToken);
        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scanId && row.ServiceName == service)
            .OrderBy(row => row.DatasourceName)
            .ToListAsync(cancellationToken);
        return rows
            .SelectMany(DeserializeCandidates)
            .OrderBy(candidate => candidate.Datasource, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Resolves an exact, narrowing-only removal scope from server-stored evidence.
    /// Client-provided IDs may only select a subset of the stored service candidates.
    /// </summary>
    public async Task<CorruptionRemovalSelection> GetRemovalSelectionAsync(
        Guid scanId,
        string service,
        IReadOnlyCollection<string>? candidateIds = null,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await RequireCurrentScanAsync(dbContext, scanId, cancellationToken);
        if (scan.DetectionMode == CorruptionDetectionMode.LogsOnly)
        {
            throw new ForbiddenException("Logs-only corruption findings are review-only and cannot be removed");
        }

        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scanId && row.ServiceName == service)
            .ToListAsync(cancellationToken);
        var allCandidates = rows.SelectMany(DeserializeCandidates).ToList();
        if (allCandidates.Count == 0)
        {
            throw new NotFoundException("Corruption candidates");
        }

        List<CorruptionCandidate> selected;
        if (candidateIds is { Count: > 0 })
        {
            var requestedIds = new HashSet<string>(candidateIds, StringComparer.Ordinal);
            var storedIds = new HashSet<string>(
                allCandidates.Select(candidate => candidate.CandidateId),
                StringComparer.Ordinal);
            var unknownIds = requestedIds.Where(id => !storedIds.Contains(id)).ToList();
            if (unknownIds.Count > 0)
            {
                throw new ValidationException("One or more corruption candidate IDs are not part of the stored scan");
            }

            selected = allCandidates.Where(candidate => requestedIds.Contains(candidate.CandidateId)).ToList();
            if (selected.Any(candidate => !candidate.RemovalAllowed))
            {
                throw new ForbiddenException("One or more selected corruption candidates are review-only");
            }
        }
        else
        {
            selected = allCandidates.Where(candidate => candidate.RemovalAllowed).ToList();
        }

        if (selected.Count == 0)
        {
            throw new ForbiddenException("This stored corruption scope has no removable candidates");
        }

        var candidatesByDatasource = selected
            .GroupBy(candidate => candidate.Datasource, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CorruptionCandidate>)group
                    .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        return new CorruptionRemovalSelection
        {
            ScanId = scan.ScanId,
            Mode = scan.DetectionMode,
            Threshold = scan.Threshold,
            ContractVersion = scan.ContractVersion,
            Service = service,
            CandidatesByDatasource = candidatesByDatasource
        };
    }

    /// <summary>
    /// Prunes only candidates that Rust successfully processed. Failed/cancelled
    /// operations never call this method, preserving authoritative evidence.
    /// </summary>
    public async Task ApplyRemovalSuccessAsync(
        Guid scanId,
        IReadOnlyCollection<string> candidateIds,
        CancellationToken cancellationToken = default)
    {
        if (candidateIds.Count == 0)
        {
            throw new ValidationException("At least one stored corruption candidate is required");
        }

        var removedIds = new HashSet<string>(candidateIds, StringComparer.Ordinal);
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await RequireCurrentScanAsync(dbContext, scanId, cancellationToken);
                var rows = await dbContext.CachedCorruptionDetections
                    .Where(row => row.ScanId == scanId)
                    .ToListAsync(cancellationToken);
                var matchedIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var remaining = new List<CorruptionCandidate>();
                    foreach (var candidate in DeserializeCandidates(row))
                    {
                        if (removedIds.Contains(candidate.CandidateId))
                        {
                            matchedIds.Add(candidate.CandidateId);
                        }
                        else
                        {
                            remaining.Add(candidate);
                        }
                    }

                    if (remaining.Count == 0)
                    {
                        dbContext.CachedCorruptionDetections.Remove(row);
                    }
                    else
                    {
                        row.CandidatesJson = SerializeCandidates(remaining);
                        row.CorruptedChunkCount = remaining.Count;
                        row.RemovalAllowed = remaining.Any(candidate => candidate.RemovalAllowed);
                    }
                }

                if (!matchedIds.SetEquals(removedIds))
                {
                    throw new ConflictException("The stored corruption scan changed before removal completed");
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        });
    }

    public OperationInfo? GetOperationStatus(Guid operationId) => _operationTracker.GetOperation(operationId);

    public OperationInfo? GetActiveOperation() =>
        _operationTracker.GetActiveOperations(OperationType.CorruptionDetection).FirstOrDefault();

    public bool CancelDetection()
    {
        var activeOp = GetActiveOperation();
        if (activeOp == null)
        {
            return false;
        }

        if (activeOp.Status is OperationStatus.Cancelled or OperationStatus.Cancelling)
        {
            return true;
        }

        _logger.LogInformation("[CorruptionDetection] Cancelling detection operation {OperationId}", activeOp.Id);
        _operationTracker.CancelOperation(activeOp.Id);
        return true;
    }

    public async Task InvalidateCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.CachedCorruptionDetections.ExecuteDeleteAsync(cancellationToken);
            await dbContext.CachedCorruptionScans.ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
        _logger.LogInformation("[CorruptionDetection] Persisted scan invalidated");
    }

    internal static string SerializeCandidates(IReadOnlyList<CorruptionCandidate> candidates) =>
        JsonSerializer.Serialize(candidates, _candidateJsonOptions);

    internal static List<CorruptionCandidate> DeserializeCandidates(CachedCorruptionDetection row)
    {
        var candidates = JsonSerializer.Deserialize<List<CorruptionCandidate>>(
                row.CandidatesJson,
                _candidateJsonOptions)
            ?? throw new InvalidDataException($"Stored corruption evidence row {row.Id} was null");
        foreach (var candidate in candidates)
        {
            candidate.Datasource = row.DatasourceName;
        }

        return candidates;
    }

    private static Dictionary<string, long> ProjectCounts(IEnumerable<CorruptionCandidate> candidates) =>
        candidates
            .GroupBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.LongCount(),
                StringComparer.OrdinalIgnoreCase);

    internal static CorruptionDetectionMode ValidateScanInput(string detectionMode, int threshold)
    {
        var mode = CorruptionDetectionModeExtensions.Parse(detectionMode);
        if (mode == CorruptionDetectionMode.Unknown)
        {
            throw new ValidationException(
                "Detection mode must be logs_only, cache_and_logs, or redownload");
        }

        if (!_allowedThresholds.Contains(threshold))
        {
            throw new ValidationException("Corruption threshold must be 3, 5, or 10");
        }

        return mode;
    }

    private static async Task<CachedCorruptionScan> RequireCurrentScanAsync(
        AppDbContext dbContext,
        Guid scanId,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.CachedCorruptionScans
            .OrderByDescending(scan => scan.CompletedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (current == null)
        {
            throw new NotFoundException("Corruption scan");
        }

        if (current.ScanId != scanId)
        {
            throw new ConflictException("The corruption scan is stale. Reload results and try again");
        }

        return current;
    }
}

internal sealed record DatasourceCorruptionReport(string DatasourceName, CorruptionReport Report);

/// <summary>JSON model for Rust corruption detection progress.</summary>
public class CorruptionDetectionProgressData
{
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }

    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public double PercentComplete { get; set; }
    public string? CurrentFile { get; set; }
    public string? Timestamp { get; set; }
}

/// <summary>JSON model for Rust corruption removal progress.</summary>
internal class CorruptionRemovalProgressData
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }

    [JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("filesProcessed")]
    public int FilesProcessed { get; set; }

    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

/// <summary>Projection of the authoritative cached corruption scan.</summary>
public class CachedCorruptionResult
{
    public bool HasCachedResults { get; set; }
    public Guid ScanId { get; set; }
    public CorruptionDetectionMode DetectionMode { get; set; }
    public int Threshold { get; set; }
    public int ContractVersion { get; set; }
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastDetectionTime { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
    public bool RemovalAllowed { get; set; }
    public Dictionary<string, bool> ServiceRemovalAllowed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
