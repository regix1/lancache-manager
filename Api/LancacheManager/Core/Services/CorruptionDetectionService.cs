using System.Data;
using System.Globalization;
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
    internal const int DefaultLookbackDays = 30;
    internal const int MinimumLookbackDays = 1;
    internal const int MaximumLookbackDays = 365;

    private const string CanonicalUtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const CorruptionDetectionMode PersistedDetectionMode = CorruptionDetectionMode.CacheAndLogs;

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

    /// <summary>Starts the single actionable corruption scan.</summary>
    public async Task<Guid> StartDetectionAsync(
        int threshold = 3,
        int lookbackDays = DefaultLookbackDays,
        CancellationToken cancellationToken = default)
    {
        ValidateScanInput(threshold, lookbackDays);

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
                Threshold = threshold,
                LookbackDays = lookbackDays
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

            var startedAtUtc = CaptureScanStartedUtc();
            var token = cts.Token;
            _ = Task.Run(
                () => RunDetectionAsync(
                    operationId,
                    threshold,
                    lookbackDays,
                    startedAtUtc,
                    token),
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
                    CorruptionCounts: counts,
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
        int lookbackDays,
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
            var scanStartedUtc = FormatScanStartedUtc(startedAtUtc);
            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, "Corruption manager");

            _logger.LogInformation(
                "[CorruptionDetection] Starting actionable detection for {Count} datasource(s)",
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
                    lookbackDays,
                    scanStartedUtc,
                    cancellationToken);
                datasourceReports.Add(new DatasourceCorruptionReport(datasource.Name, report));
            }

            var completedAtUtc = DateTime.UtcNow;
            var scanId = Guid.NewGuid();
            await PersistCompletedScanAsync(
                scanId,
                threshold,
                lookbackDays,
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
                metrics.Threshold = threshold;
                metrics.LookbackDays = lookbackDays;
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
        int lookbackDays,
        string scanStartedUtc,
        CancellationToken cancellationToken)
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var progressFile = Path.Combine(operationsDir, $"corruption_detection_{operationId}_{datasourceName}.json");

        try
        {
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"summary \"{logDir}\" \"{cacheDir}\" \"{progressFile}\" \"{timezone}\" {threshold} --lookback-days {lookbackDays} --scan-started-utc \"{scanStartedUtc}\"");

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
            ValidateAndAttachDatasource(
                report,
                datasourceName,
                threshold,
                lookbackDays,
                scanStartedUtc);
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
        int expectedThreshold,
        int expectedLookbackDays,
        string expectedScanStartedUtc)
    {
        if (report.ContractVersion != CorruptionReport.SupportedContractVersion)
        {
            throw new InvalidDataException(
                $"Unsupported corruption report contract version {report.ContractVersion}");
        }

        if (report.Threshold != expectedThreshold)
        {
            throw new InvalidDataException("Corruption report threshold did not match the requested scan");
        }

        if (report.LookbackDays != expectedLookbackDays
            || !string.Equals(report.ScanStartedUtc, expectedScanStartedUtc, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Corruption report lookback or scan start did not match the requested scan");
        }

        if (!TryParseCanonicalUtc(expectedScanStartedUtc, out var scanStartedUtc))
        {
            throw new InvalidDataException("Corruption scan start was not canonical UTC");
        }

        if (report.Candidates is null
            || report.ServiceCounts is null)
        {
            throw new InvalidDataException("Corruption report omitted required v3 evidence fields");
        }

        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var physicalIdentities = new HashSet<(
            string Service,
            string NormalizedUri,
            string CacheSliceKind,
            ulong? CacheSliceStart,
            ulong? CacheSliceEnd)>();
        foreach (var candidate in report.Candidates)
        {
            if (candidate is null
                || string.IsNullOrWhiteSpace(candidate.CandidateId)
                || string.IsNullOrWhiteSpace(candidate.Service))
            {
                throw new InvalidDataException("Corruption report contained a candidate without an identity or service");
            }

            if (candidate.ExactPaths is null
                || candidate.Observations is null
                || candidate.ObservedRange is null
                || candidate.CacheSlice is null
                || candidate.Observations.Any(observation => observation is null)
                || candidate.ExactPaths.Any(string.IsNullOrWhiteSpace)
                || candidate.ExactPaths.Distinct(StringComparer.Ordinal).Count() != candidate.ExactPaths.Count)
            {
                throw new InvalidDataException(
                    "Corruption candidate contained malformed exact-path or observation evidence");
            }

            candidate.ExactPaths = candidate.ExactPaths
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            ValidateCandidateShape(candidate, expectedThreshold, expectedLookbackDays, scanStartedUtc);

            candidate.Datasource = datasourceName;
            candidate.CandidateId = $"{datasourceName}:{candidate.CandidateId}";

            if (!candidateIds.Add(candidate.CandidateId))
            {
                throw new InvalidDataException($"Duplicate corruption candidate ID '{candidate.CandidateId}'");
            }

            var physicalIdentity = (
                candidate.Service,
                candidate.NormalizedUri,
                candidate.CacheSlice.Kind,
                candidate.CacheSlice.Start,
                candidate.CacheSlice.End);
            if (!physicalIdentities.Add(physicalIdentity))
            {
                throw new InvalidDataException(
                    "Corruption report contained duplicate immutable physical candidate identity");
            }
        }

        report.Candidates = report.Candidates
            .OrderBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        var projection = ProjectCounts(report.Candidates);
        if (!CountMapsEqual(report.ServiceCounts, projection)
            || report.Total != projection.Values.Sum())
        {
            throw new InvalidDataException(
                "Corruption report count projections did not match its candidate evidence");
        }

        report.ServiceCounts = projection;
        report.Total = projection.Values.Sum();
    }

    internal async Task PersistCompletedScanAsync(
        Guid scanId,
        int threshold,
        int lookbackDays,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        IReadOnlyList<DatasourceCorruptionReport> datasourceReports,
        CancellationToken cancellationToken = default)
    {
        ValidateScanInput(threshold, lookbackDays);
        if (startedAtUtc.Kind != DateTimeKind.Utc
            || startedAtUtc.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new InvalidDataException(
                "Corruption scan start must be a whole-second UTC timestamp");
        }

        await using var strategyContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                await dbContext.CachedCorruptionDetections.ExecuteDeleteAsync(cancellationToken);
                await dbContext.CachedCorruptionScans.ExecuteDeleteAsync(cancellationToken);

                var expectedScanStartedUtc = FormatScanStartedUtc(startedAtUtc);
                if (datasourceReports.Any(item =>
                        item.Report.ContractVersion != CorruptionReport.SupportedContractVersion
                        || item.Report.Threshold != threshold
                        || item.Report.LookbackDays != lookbackDays
                        || !string.Equals(
                            item.Report.ScanStartedUtc,
                            expectedScanStartedUtc,
                            StringComparison.Ordinal)))
                {
                    throw new InvalidDataException(
                        "Datasource corruption report metadata did not match the completed scan");
                }

                dbContext.CachedCorruptionScans.Add(new CachedCorruptionScan
                {
                    ScanId = scanId,
                    DetectionMode = PersistedDetectionMode,
                    Threshold = threshold,
                    LookbackDays = lookbackDays,
                    ContractVersion = CorruptionReport.SupportedContractVersion,
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
                            RemovalAllowed = true,
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

        if (!IsSupportedScan(scan))
        {
            _logger.LogInformation(
                "[CorruptionDetection] Ignoring cached scan {ScanId} with unsupported contract {ContractVersion} or mode {Mode}",
                scan.ScanId,
                scan.ContractVersion,
                scan.DetectionMode.ToWireString());
            return null;
        }

        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scan.ScanId)
            .ToListAsync(cancellationToken);
        var candidates = rows.SelectMany(DeserializeCandidates).ToList();
        return BuildCachedResult(scan, candidates);
    }

    private static CachedCorruptionResult BuildCachedResult(
        CachedCorruptionScan scan,
        IReadOnlyCollection<CorruptionCandidate> candidates)
    {
        var counts = ProjectCounts(candidates);
        return new CachedCorruptionResult
        {
            HasCachedResults = true,
            ScanId = scan.ScanId,
            Threshold = scan.Threshold,
            LookbackDays = scan.LookbackDays,
            ContractVersion = scan.ContractVersion,
            CorruptionCounts = counts,
            LastDetectionTime = scan.CompletedAtUtc,
            TotalServicesWithCorruption = counts.Count,
            TotalCorruptedChunks = counts.Values.Sum()
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
        EnsureActionableRows(rows);
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
        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scanId && row.ServiceName == service)
            .ToListAsync(cancellationToken);
        EnsureActionableRows(rows);
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
        }
        else
        {
            selected = allCandidates.ToList();
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
        CancellationToken cancellationToken = default) =>
        await ApplyCandidateCleanupSuccessAsync(
            scanId,
            candidateIds,
            "The stored corruption scan changed before removal completed",
            cancellationToken);

    private async Task ApplyCandidateCleanupSuccessAsync(
        Guid scanId,
        IReadOnlyCollection<string> candidateIds,
        string changedMessage,
        CancellationToken cancellationToken)
    {
        if (candidateIds.Count == 0)
        {
            throw new ValidationException("At least one stored corruption candidate is required");
        }

        var removedIds = new HashSet<string>(candidateIds, StringComparer.Ordinal);
        await using var strategyContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
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
                        row.RemovalAllowed = true;
                    }
                }

                if (!matchedIds.SetEquals(removedIds))
                {
                    throw new ConflictException(changedMessage);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new ConflictException("The corruption scan changed. Reload results and try again");
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

    private static void EnsureActionableRows(IEnumerable<CachedCorruptionDetection> rows)
    {
        if (rows.Any(row => !row.RemovalAllowed))
        {
            throw new ConflictException(
                "The stored corruption scan contains unsupported evidence. Run a new scan and try again");
        }
    }

    private static Dictionary<string, long> ProjectCounts(
        IEnumerable<CorruptionCandidate> candidates)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            IncrementCount(counts, candidate.Service);
        }

        return SortCounts(counts);
    }

    private static void IncrementCount(Dictionary<string, long> counts, string service) =>
        counts[service] = counts.GetValueOrDefault(service) + 1;

    private static Dictionary<string, long> SortCounts(Dictionary<string, long> counts) =>
        counts
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

    private static bool CountMapsEqual(
        Dictionary<string, long> actual,
        Dictionary<string, long> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        var normalized = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (service, count) in actual)
        {
            if (string.IsNullOrWhiteSpace(service)
                || count <= 0
                || !normalized.TryAdd(service, count))
            {
                return false;
            }
        }

        return expected.All(pair =>
            normalized.TryGetValue(pair.Key, out var count) && count == pair.Value);
    }

    private static void ValidateCandidateShape(
        CorruptionCandidate candidate,
        int threshold,
        int lookbackDays,
        DateTime scanStartedUtc)
    {
        if (candidate.ExactPaths.Count == 0
            || candidate.EvidenceCount < threshold
            || candidate.EvidenceCount != candidate.Observations.Count)
        {
            throw new InvalidDataException(
                "Corruption candidate did not contain threshold-qualified exact-path evidence");
        }

        if (!TryParseCanonicalUtc(candidate.FirstSeen, out var firstSeenUtc)
            || !TryParseCanonicalUtc(candidate.LastSeen, out var lastSeenUtc)
            || firstSeenUtc > lastSeenUtc
            || lastSeenUtc - firstSeenUtc > TimeSpan.FromSeconds(60))
        {
            throw new InvalidDataException(
                "Corruption candidate did not contain an inclusive 60-second evidence window");
        }

        var cutoffUtc = scanStartedUtc.AddDays(-lookbackDays);
        if (firstSeenUtc < cutoffUtc || lastSeenUtc > scanStartedUtc)
        {
            throw new InvalidDataException(
                "Corruption candidate evidence was outside the closed lookback window");
        }

        foreach (var observation in candidate.Observations)
        {
            if (!string.Equals(observation.Method, "GET", StringComparison.Ordinal)
                || observation.HttpStatus is not (200 or 206)
                || !string.Equals(observation.CacheStatus, "MISS", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(observation.RawUrl)
                || observation.BytesServed < 0
                || !TryParseCanonicalUtc(observation.Timestamp, out var observedAtUtc)
                || observedAtUtc < firstSeenUtc
                || observedAtUtc > lastSeenUtc)
            {
                throw new InvalidDataException(
                    "Corruption candidate contained an ineligible repeated-MISS observation");
            }
        }
    }

    private static DateTime CaptureScanStartedUtc()
    {
        var now = DateTime.UtcNow;
        return new DateTime(
            now.Ticks - now.Ticks % TimeSpan.TicksPerSecond,
            DateTimeKind.Utc);
    }

    private static string FormatScanStartedUtc(DateTime scanStartedUtc) =>
        scanStartedUtc.ToUniversalTime().ToString(CanonicalUtcFormat, CultureInfo.InvariantCulture);

    private static bool TryParseCanonicalUtc(string value, out DateTime timestampUtc) =>
        DateTime.TryParseExact(
            value,
            CanonicalUtcFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestampUtc);

    internal static void ValidateScanInput(
        int threshold,
        int lookbackDays = DefaultLookbackDays)
    {
        if (!_allowedThresholds.Contains(threshold))
        {
            throw new ValidationException("Corruption threshold must be 3, 5, or 10");
        }

        if (lookbackDays is < MinimumLookbackDays or > MaximumLookbackDays)
        {
            throw new ValidationException(
                $"Evidence lookback must be between {MinimumLookbackDays} and {MaximumLookbackDays} days");
        }

    }

    private static bool IsSupportedScan(CachedCorruptionScan scan) =>
        scan.ContractVersion == CorruptionReport.SupportedContractVersion
        && scan.DetectionMode == PersistedDetectionMode;

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

        if (!IsSupportedScan(current))
        {
            throw new ConflictException(
                "The stored corruption scan uses an older format. Run a new scan and try again");
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
    public int Threshold { get; set; }
    public int LookbackDays { get; set; }
    public int ContractVersion { get; set; }
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastDetectionTime { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
}
