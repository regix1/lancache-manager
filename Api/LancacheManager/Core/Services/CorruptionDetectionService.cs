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
/// Runs corruption scans and owns retained per-method persisted scan snapshots.
/// Current identity is explicit; summary, details, and removal scopes are
/// projections of the saved evidence.
/// </summary>
public class CorruptionDetectionService
{
    internal const int DefaultLookbackDays = 30;
    internal const int MinimumLookbackDays = 1;
    internal const int MaximumLookbackDays = 365;
    internal const ulong StructuralMinimumStableAgeSeconds = 600;
    internal const ulong StructuralMaximumPrefixBytes = 65_535;

    private const string CanonicalUtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string ProjectionServiceName = "__scan_projection__";
    private const string ProjectionDatasourceName = "__aggregate__";
    private const int MaxRetainedScansPerMethod = 3;
    private static readonly HashSet<int> _allowedThresholds = [3, 5, 10];
    private static readonly JsonSerializerOptions _candidateJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<CorruptionDetectionService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly OperationStateService _operationStateService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly SemaphoreSlim _startLock = new(1, 1);

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
        CorruptionDetectionMethod detectionMethod = CorruptionDetectionMethod.RepeatedMiss,
        StructuralScanMode? scanMode = null,
        CancellationToken cancellationToken = default)
    {
        scanMode = NormalizeStructuralScanMode(detectionMethod, scanMode);
        ValidateScanInput(threshold, lookbackDays, detectionMethod, scanMode);

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            var activeOp = _operationTracker.GetActiveOperations(OperationType.CorruptionDetection).FirstOrDefault();
            if (activeOp != null)
            {
                if (activeOp.Metadata is not CorruptionDetectionMetrics activeMetrics
                    || activeMetrics.DetectionMethod != detectionMethod
                    || activeMetrics.ScanMode != scanMode
                    || activeMetrics.Threshold != threshold
                    || activeMetrics.LookbackDays != lookbackDays)
                {
                    throw new ConflictException("A different corruption detection scan is already in progress");
                }
                _logger.LogWarning("[CorruptionDetection] Detection already in progress: {OperationId}", activeOp.Id);
                return activeOp.Id;
            }

            var cts = new CancellationTokenSource();
            var metadata = new CorruptionDetectionMetrics
            {
                Threshold = threshold,
                LookbackDays = lookbackDays,
                DetectionMethod = detectionMethod,
                ScanMode = scanMode
            };
            var startingStageKey = detectionMethod == CorruptionDetectionMethod.Structural
                ? "signalr.corruptionDetect.startingStructural"
                : "signalr.corruptionDetect.startingRepeatedMiss";
            var startingContext = BuildStructuralContext(detectionMethod, scanMode);
            metadata.CaptureProgress(
                startingStageKey,
                0,
                context: null,
                authoritativeContext: startingContext);
            Guid operationId = Guid.Empty;
            operationId = _operationTracker.RegisterOperation(
                OperationType.CorruptionDetection,
                DetectionOperationName(detectionMethod, scanMode),
                cts,
                metadata,
                onTerminalCleanup: metadata.ClearProgress,
                onTerminalEmit: info => EmitTerminalAsync(info, operationId, metadata));
            _operationTracker.UpdateProgress(operationId, 0, startingStageKey);

            var operationStateKey = operationId.ToString();
            _operationStateService.SaveState(operationStateKey, new OperationState
            {
                Key = operationStateKey,
                Type = OperationType.CorruptionDetection.ToWireString(),
                Status = OperationStatus.Running.ToWireString(),
                Message = "Starting corruption detection...",
                Data = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["detectionMethod"] = detectionMethod.ToWireString(),
                    ["scanMode"] = scanMode?.ToWireString()
                })
            });

            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionStarted, new
            {
                OperationId = operationId,
                StageKey = startingStageKey,
                DetectionMethod = detectionMethod.ToWireString(),
                ScanMode = scanMode?.ToWireString(),
                Context = startingContext
            });

            var startedAtUtc = CaptureScanStartedUtc();
            var token = cts.Token;
            _ = StartDetectionWorker(
                workerToken => RunDetectionAsync(
                    operationId,
                    threshold,
                    lookbackDays,
                    detectionMethod,
                    scanMode,
                    metadata,
                    startedAtUtc,
                    workerToken),
                token);

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    // Do not pass the operation token to Task.Run itself. If cancellation wins during the
    // asynchronous start notification, Task.Run(token) suppresses the delegate entirely and the
    // registered operation never reaches its cancellation cleanup. The worker receives the same
    // token and owns the terminal transition.
    internal static Task StartDetectionWorker(
        Func<CancellationToken, Task> worker,
        CancellationToken cancellationToken) =>
        Task.Run(() => worker(cancellationToken));

    internal static IReadOnlyList<string> BuildStructuralProcessArguments(
        string cacheDir,
        string progressFile,
        string scanStartedUtc,
        StructuralScanMode scanMode,
        string stateDatabasePath,
        string stateScope) =>
        [
            "structural-summary",
            cacheDir,
            progressFile,
            "--scan-started-utc",
            scanStartedUtc,
            "--scan-mode",
            scanMode.ToWireString(),
            "--state-db",
            stateDatabasePath,
            "--state-scope",
            stateScope
        ];

    internal static string DetectionOperationName(
        CorruptionDetectionMethod detectionMethod,
        StructuralScanMode? scanMode) =>
        detectionMethod == CorruptionDetectionMethod.Structural
            ? $"Structural Corruption Detection ({(scanMode ?? StructuralScanMode.Full).ToWireString()})"
            : "Corruption Detection";

    private Task EmitTerminalAsync(
        OperationTerminalInfo info,
        Guid operationId,
        CorruptionDetectionMetrics metadata)
    {
        var counts = metadata.CorruptionCounts;
        var totalServicesWithCorruption = counts?.Count ?? 0;
        var totalCorruptedChunks = counts != null ? (int)Math.Min(counts.Values.Sum(), int.MaxValue) : 0;
        var detectionMethod = metadata.DetectionMethod.ToWireString();
        var scanMode = metadata.ScanMode?.ToWireString();
        var effectiveScanMode = metadata.EffectiveScanMode?.ToWireString();
        var baselineStatus = metadata.BaselineStatus?.ToWireString();
        var structural = metadata.ScanMode.HasValue;
        var structuralContext = BuildStructuralContext(metadata.DetectionMethod, metadata.ScanMode);
        AddStructuralMetrics(structuralContext, metadata);

        if (info.Cancelled)
        {
            return _notifications.NotifyAllAsync(
                SignalREvents.CorruptionDetectionComplete,
                new SignalRNotifications.CorruptionDetectionComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.corruptionDetect.cancelled",
                    Status: OperationStatus.Cancelled,
                    Cancelled: true,
                    DetectionMethod: detectionMethod,
                    DetectionCounts: metadata.DetectionCounts,
                    Coverage: CorruptionScanCoverageResponse.From(metadata.Coverage),
                    Context: structuralContext,
                    ScanMode: scanMode,
                    EffectiveScanMode: effectiveScanMode,
                    BaselineStatus: baselineStatus,
                    StateCommitted: structural ? metadata.StateCommitted : null,
                    Resumed: structural ? metadata.Resumed : null,
                    FilesDiscovered: structural ? metadata.FilesDiscovered : null,
                    FilesProcessed: structural ? metadata.FilesProcessed : null,
                    FilesInspected: structural ? metadata.FilesInspected : null,
                    FilesReused: structural ? metadata.FilesReused : null,
                    FilesRevalidated: structural ? metadata.FilesRevalidated : null,
                    InvalidFiles: structural ? metadata.InvalidFiles : null,
                    FilesPruned: structural ? metadata.FilesPruned : null,
                    FilesPendingRetry: structural ? metadata.FilesPendingRetry : null,
                    StateEntries: structural ? metadata.StateEntries : null,
                    ScanSummary: SnapshotStructuralSummary(metadata)));
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
                    DetectionMethod: detectionMethod,
                    DetectionCounts: metadata.DetectionCounts,
                    Coverage: CorruptionScanCoverageResponse.From(metadata.Coverage),
                    Context: new Dictionary<string, object?>(structuralContext)
                    {
                        ["count"] = totalServicesWithCorruption,
                        ["scanId"] = metadata.ScanId
                    },
                    ScanMode: scanMode,
                    EffectiveScanMode: effectiveScanMode,
                    BaselineStatus: baselineStatus,
                    StateCommitted: structural ? metadata.StateCommitted : null,
                    Resumed: structural ? metadata.Resumed : null,
                    FilesDiscovered: structural ? metadata.FilesDiscovered : null,
                    FilesProcessed: structural ? metadata.FilesProcessed : null,
                    FilesInspected: structural ? metadata.FilesInspected : null,
                    FilesReused: structural ? metadata.FilesReused : null,
                    FilesRevalidated: structural ? metadata.FilesRevalidated : null,
                    InvalidFiles: structural ? metadata.InvalidFiles : null,
                    FilesPruned: structural ? metadata.FilesPruned : null,
                    FilesPendingRetry: structural ? metadata.FilesPendingRetry : null,
                    StateEntries: structural ? metadata.StateEntries : null,
                    ScanSummary: SnapshotStructuralSummary(metadata)));
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
                DetectionMethod: detectionMethod,
                DetectionCounts: metadata.DetectionCounts,
                Coverage: CorruptionScanCoverageResponse.From(metadata.Coverage),
                Context: new Dictionary<string, object?>(structuralContext)
                {
                    ["errorDetail"] = info.Error
                },
                ScanMode: scanMode,
                EffectiveScanMode: effectiveScanMode,
                BaselineStatus: baselineStatus,
                StateCommitted: structural ? metadata.StateCommitted : null,
                Resumed: structural ? metadata.Resumed : null,
                FilesDiscovered: structural ? metadata.FilesDiscovered : null,
                FilesProcessed: structural ? metadata.FilesProcessed : null,
                FilesInspected: structural ? metadata.FilesInspected : null,
                FilesReused: structural ? metadata.FilesReused : null,
                FilesRevalidated: structural ? metadata.FilesRevalidated : null,
                InvalidFiles: structural ? metadata.InvalidFiles : null,
                FilesPruned: structural ? metadata.FilesPruned : null,
                FilesPendingRetry: structural ? metadata.FilesPendingRetry : null,
                StateEntries: structural ? metadata.StateEntries : null,
                ScanSummary: SnapshotStructuralSummary(metadata)));
    }

    private async Task RunDetectionAsync(
        Guid operationId,
        int threshold,
        int lookbackDays,
        CorruptionDetectionMethod detectionMethod,
        StructuralScanMode? scanMode,
        CorruptionDetectionMetrics metadata,
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
            for (var datasourceIndex = 0; datasourceIndex < datasources.Count; datasourceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var datasource = datasources[datasourceIndex];
                var datasourceReport = await GetReportForDatasourceAsync(
                    datasource.LogPath,
                    datasource.CachePath,
                    timezone,
                    rustBinaryPath,
                    operationId,
                    datasource.Name,
                    threshold,
                    lookbackDays,
                    detectionMethod,
                    scanMode,
                    metadata,
                    scanStartedUtc,
                    datasourceIndex,
                    datasources.Count,
                    cancellationToken);
                datasourceReports.Add(datasourceReport);
            }

            var structuralSummary = AggregateStructuralSummary(datasourceReports, scanMode);
            var completedAtUtc = DateTime.UtcNow;
            var scanId = Guid.NewGuid();
            await PersistCompletedScanAsync(
                scanId,
                threshold,
                lookbackDays,
                detectionMethod,
                startedAtUtc,
                completedAtUtc,
                datasourceReports,
                scanMode,
                cancellationToken);

            var counts = ProjectCounts(datasourceReports.SelectMany(report => report.Report.Candidates));
            var detectionCounts = ProjectDetectionCounts(
                datasourceReports.SelectMany(report => report.Report.Candidates),
                detectionMethod);
            var coverage = AggregateCoverage(datasourceReports.Select(report => report.Report.Coverage));
            _operationTracker.UpdateProgress(operationId, 100, "signalr.corruptionDetect.complete");
            _operationTracker.UpdateMetadata(operationId, metadata =>
            {
                var metrics = (CorruptionDetectionMetrics)metadata;
                metrics.ScanId = scanId;
                metrics.Threshold = threshold;
                metrics.LookbackDays = lookbackDays;
                metrics.DetectionMethod = detectionMethod;
                ApplyStructuralSummary(metrics, structuralSummary);
                metrics.CorruptionCounts = counts;
                metrics.DetectionCounts = detectionCounts;
                metrics.Coverage = coverage;
                metrics.LastDetectionTime = completedAtUtc;
            });

            _operationStateService.RemoveState(operationId.ToString());
            _operationTracker.CompleteOperation(operationId, success: true);
            _logger.LogInformation(
                "[CorruptionDetection] Scan {ScanId} complete: {Services}",
                scanId,
                string.Join(", ", counts.Select(pair => $"{pair.Key}={pair.Value}")));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[CorruptionDetection] Operation {OperationId} was cancelled", operationId);
            _operationStateService.RemoveState(operationId.ToString());
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
        }
        catch (Exception ex)
        {
            // RustProcessException keeps stderr out of Message so it never reaches the client, which
            // also means it never reached the log: a failing scan reported nothing but an exit code.
            // Log it here, where it is server-side only and is the only thing that says why.
            if (ex is RustProcessException rustFailure && !string.IsNullOrWhiteSpace(rustFailure.Stderr))
            {
                _logger.LogError(
                    "[CorruptionDetection] {Tool} exited {ExitCode} for operation {OperationId}: {Stderr}",
                    rustFailure.Tool,
                    rustFailure.ExitCode,
                    operationId,
                    rustFailure.Stderr!.Trim());
            }
            _logger.LogError(ex, "[CorruptionDetection] Detection failed for operation {OperationId}", operationId);
            _operationStateService.RemoveState(operationId.ToString());
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
    }

    private async Task<DatasourceCorruptionReport> GetReportForDatasourceAsync(
        string logDir,
        string cacheDir,
        string timezone,
        string rustBinaryPath,
        Guid operationId,
        string datasourceName,
        int threshold,
        int lookbackDays,
        CorruptionDetectionMethod detectionMethod,
        StructuralScanMode? scanMode,
        CorruptionDetectionMetrics metadata,
        string scanStartedUtc,
        int datasourceIndex,
        int datasourceCount,
        CancellationToken cancellationToken)
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var stateScope = _pathResolver.GetStructuralCorruptionStateScope(datasourceName, cacheDir);
        var progressFile = Path.Combine(operationsDir, $"corruption_detection_{operationId}_{stateScope}.json");

        try
        {
            if (detectionMethod == CorruptionDetectionMethod.Structural)
            {
                ResetStructuralProgressMetrics(metadata);
            }

            var startInfo = detectionMethod switch
            {
                CorruptionDetectionMethod.RepeatedMiss =>
                    _rustProcessHelper.CreateProcessStartInfo(
                        rustBinaryPath,
                        [
                            "summary",
                            logDir,
                            cacheDir,
                            progressFile,
                            timezone,
                            threshold.ToString(CultureInfo.InvariantCulture),
                            "--lookback-days",
                            lookbackDays.ToString(CultureInfo.InvariantCulture),
                            "--scan-started-utc",
                            scanStartedUtc
                        ]),
                CorruptionDetectionMethod.Structural =>
                    _rustProcessHelper.CreateProcessStartInfo(
                        rustBinaryPath,
                        BuildStructuralProcessArguments(
                            cacheDir,
                            progressFile,
                            scanStartedUtc,
                            scanMode ?? throw new ValidationException("Structural scan mode is required"),
                            _pathResolver.GetStructuralCorruptionStateDatabasePath(datasourceName, cacheDir),
                            stateScope)),
                _ => throw new ValidationException("Unsupported corruption detection method")
            };

            var relay = new CorruptionProgressRelay(
                metadata,
                detectionMethod,
                datasourceName,
                datasourceIndex,
                datasourceCount,
                scanMode);
            var startingStageKey = detectionMethod == CorruptionDetectionMethod.Structural
                ? "signalr.corruptionDetect.startingStructural"
                : "signalr.corruptionDetect.startingRepeatedMiss";
            await RelayProgressAsync(relay.Capture(startingStageKey, 0, null, 0, 0));

            StructuralStderrObserver? stderrObserver = detectionMethod == CorruptionDetectionMethod.Structural
                ? new StructuralStderrObserver(_logger, datasourceName)
                : null;
            RepeatedMissStderrObserver? repeatedMissStderrObserver =
                detectionMethod == CorruptionDetectionMethod.RepeatedMiss
                    ? new RepeatedMissStderrObserver(_logger, datasourceName)
                    : null;
            Action<string>? stderrLineObserver = stderrObserver is not null
                ? stderrObserver.Observe
                : repeatedMissStderrObserver is not null
                    ? repeatedMissStderrObserver.Observe
                    : null;
            var rustCancellationReported = 0;
            ProcessExecutionResult result;
            try
            {
                result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<CorruptionDetectionProgressData>(
                    startInfo,
                    operationId,
                    cancellationToken,
                    progressFile,
                    async progressData =>
                    {
                        if (IsCancelledProgress(progressData))
                        {
                            Interlocked.Exchange(ref rustCancellationReported, 1);
                        }
                        var stageKey = string.IsNullOrWhiteSpace(progressData.StageKey)
                            ? "signalr.corruptionDetect.scanning"
                            : progressData.StageKey;
                        UpdateStructuralProgressMetrics(metadata, progressData.Context);
                        await RelayProgressAsync(relay.Capture(
                            stageKey,
                            progressData.PercentComplete,
                            progressData.Context,
                            progressData.FilesProcessed,
                            progressData.TotalFiles));
                    },
                    "corruption_manager",
                    onStderrLine: stderrLineObserver,
                    maxRetainedStderrChars: stderrLineObserver == null ? null : 256 * 1024);
            }
            finally
            {
                stderrObserver?.Complete();
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                _logger.LogDebug(
                    "[CorruptionDetection] corruption_manager stderr for {Datasource}: {Error}",
                    datasourceName,
                    result.Error);
            }

            result.EnsureSuccess("corruption_manager", datasourceName);
            if (Volatile.Read(ref rustCancellationReported) != 0)
            {
                // Preserve cancellation classification even if the redundant stdout report is
                // missing or malformed. This signal can only fail closed; it never permits
                // validation or persistence.
                _operationTracker.CancelOperation(operationId);
                throw new OperationCanceledException(
                    "corruption_manager reported cancellation",
                    cancellationToken);
            }
            var report = JsonSerializer.Deserialize<CorruptionReport>(result.Output, _candidateJsonOptions)
                ?? throw new InvalidDataException(
                    $"corruption_manager returned an empty report for datasource '{datasourceName}'");
            if (report.Cancelled)
            {
                // The report is the authoritative terminal result. A missed or unreadable final
                // progress-file update can no longer turn a partial report into a completed scan.
                _operationTracker.CancelOperation(operationId);
                throw new OperationCanceledException(
                    "corruption_manager reported cancellation",
                    cancellationToken);
            }
            var scanSummary = SnapshotStructuralSummary(metadata);
            ValidateAndAttachDatasource(
                report,
                datasourceName,
                threshold,
                lookbackDays,
                detectionMethod,
                scanMode,
                scanStartedUtc,
                scanSummary);
            return new DatasourceCorruptionReport(
                datasourceName,
                report,
                scanSummary);

            async Task RelayProgressAsync(CorruptionRelayDecision decision)
            {
                if (decision.IsNew)
                {
                    _operationTracker.UpdateProgress(
                        operationId,
                        decision.Snapshot.PercentComplete,
                        decision.Snapshot.StageKey);
                }

                if (!decision.ShouldEmit)
                {
                    return;
                }

                await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionProgress, new
                {
                    OperationId = operationId,
                    decision.Snapshot.PercentComplete,
                    Status = OperationStatus.Running,
                    decision.Snapshot.StageKey,
                    DetectionMethod = detectionMethod.ToWireString(),
                    ScanMode = scanMode?.ToWireString(),
                    EffectiveScanMode = GetContextValue(decision.Snapshot.Context, "effectiveScanMode"),
                    BaselineStatus = GetContextValue(decision.Snapshot.Context, "baselineStatus"),
                    Resumed = GetContextValue(decision.Snapshot.Context, "resumed"),
                    FilesDiscovered = GetContextValue(decision.Snapshot.Context, "filesDiscovered"),
                    decision.Snapshot.Context,
                    FilesProcessed = decision.Snapshot.Context["filesProcessed"],
                    FilesReused = GetContextValue(decision.Snapshot.Context, "filesReused"),
                    FilesInspected = GetContextValue(decision.Snapshot.Context, "filesInspected"),
                    FilesRevalidated = GetContextValue(decision.Snapshot.Context, "filesRevalidated"),
                    InvalidFiles = GetContextValue(decision.Snapshot.Context, "invalidFiles"),
                    FilesPendingRetry = GetContextValue(decision.Snapshot.Context, "filesPendingRetry"),
                    FilesPruned = GetContextValue(decision.Snapshot.Context, "filesPruned"),
                    StateEntries = GetContextValue(decision.Snapshot.Context, "stateEntries"),
                    StateCommitted = GetContextValue(decision.Snapshot.Context, "stateCommitted"),
                    TotalFiles = decision.Snapshot.Context["totalFiles"],
                    DatasourceName = datasourceName
                });
            }
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(progressFile);
        }
    }

    internal static bool IsCancelledProgress(CorruptionDetectionProgressData progressData) =>
        string.Equals(progressData.Status, "cancelled", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateAndAttachDatasource(
        CorruptionReport report,
        string datasourceName,
        int expectedThreshold,
        int expectedLookbackDays,
        CorruptionDetectionMethod expectedDetectionMethod,
        string expectedScanStartedUtc) =>
        ValidateAndAttachDatasource(
            report,
            datasourceName,
            expectedThreshold,
            expectedLookbackDays,
            expectedDetectionMethod,
            expectedDetectionMethod == CorruptionDetectionMethod.Structural
                ? StructuralScanMode.Full
                : null,
            expectedScanStartedUtc);

    internal static void ValidateAndAttachDatasource(
        CorruptionReport report,
        string datasourceName,
        int expectedThreshold,
        int expectedLookbackDays,
        CorruptionDetectionMethod expectedDetectionMethod,
        StructuralScanMode? expectedScanMode,
        string expectedScanStartedUtc,
        StructuralScanStatusResponse? scanSummary = null)
    {
        if (report.ContractVersion != CorruptionReport.SupportedContractVersion)
        {
            throw new InvalidDataException(
                $"Unsupported corruption report contract version {report.ContractVersion}");
        }

        if (report.Cancelled)
        {
            throw new InvalidDataException(
                "Cancelled corruption report cannot be validated as a completed scan");
        }

        if (report.DetectionMethod != expectedDetectionMethod)
        {
            throw new InvalidDataException("Corruption report method did not match the requested scan");
        }

        if ((expectedDetectionMethod == CorruptionDetectionMethod.Structural) != expectedScanMode.HasValue)
        {
            throw new InvalidDataException("Corruption report validation omitted or forged structural scan mode");
        }

        if (report.Candidates is null
            || report.ServiceCounts is null
            || report.DetectionCounts is null
            || report.Settings is null)
        {
            throw new InvalidDataException("Corruption report omitted required fields");
        }

        if (expectedDetectionMethod == CorruptionDetectionMethod.RepeatedMiss
            && (report.Settings.Threshold != expectedThreshold
                || report.Settings.LookbackDays != expectedLookbackDays))
        {
            throw new InvalidDataException("Corruption report settings did not match the requested repeated-MISS scan");
        }

        if (expectedDetectionMethod == CorruptionDetectionMethod.Structural
            && (report.Settings.Threshold is not null
                || report.Settings.LookbackDays is not null
                || report.Settings.MinimumStableAgeSeconds != StructuralMinimumStableAgeSeconds
                || report.Settings.MaximumPrefixBytes != StructuralMaximumPrefixBytes))
        {
            throw new InvalidDataException("Corruption report structural settings were malformed");
        }

        if (!string.Equals(report.ScanStartedUtc, expectedScanStartedUtc, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Corruption report scan start did not match the requested scan");
        }

        if (!TryParseCanonicalUtc(expectedScanStartedUtc, out var scanStartedUtc))
        {
            throw new InvalidDataException("Corruption scan start was not canonical UTC");
        }

        if (expectedDetectionMethod == CorruptionDetectionMethod.RepeatedMiss && report.Coverage is not null)
        {
            throw new InvalidDataException("Repeated-MISS report unexpectedly contained structural coverage");
        }

        if (expectedDetectionMethod == CorruptionDetectionMethod.Structural)
        {
            var coverage = report.Coverage
                ?? throw new InvalidDataException("Structural report omitted scan coverage");
            if (scanSummary != null)
            {
                ValidateStructuralProgressSummary(scanSummary, expectedScanMode!.Value, coverage, report.Candidates.Count);
            }
            ValidateCoverage(coverage, report.Candidates.Count, scanSummary?.FilesReused ?? 0);
        }

        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var physicalPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in report.Candidates)
        {
            if (candidate is null
                || string.IsNullOrWhiteSpace(candidate.CandidateId)
                || string.IsNullOrWhiteSpace(candidate.Service)
                || candidate.Datasource is not null)
            {
                throw new InvalidDataException(
                    "Corruption report contained an invalid identity, service, or producer-owned datasource");
            }

            if (candidate.ExactPaths is null
                || candidate.Evidence is null
                || candidate.ExactPaths.Count != 1
                || candidate.ExactPaths.Any(string.IsNullOrWhiteSpace)
                || candidate.ExactPaths.Distinct(StringComparer.Ordinal).Count() != candidate.ExactPaths.Count)
            {
                throw new InvalidDataException(
                    "Corruption candidate must identify exactly one physical path and one evidence branch");
            }

            ValidateCandidateShape(
                candidate,
                expectedDetectionMethod,
                expectedThreshold,
                expectedLookbackDays,
                scanStartedUtc);

            candidate.Datasource = datasourceName;
            candidate.CandidateId = $"{datasourceName}:{candidate.CandidateId}";

            if (!candidateIds.Add(candidate.CandidateId))
            {
                throw new InvalidDataException($"Duplicate corruption candidate ID '{candidate.CandidateId}'");
            }

            if (!physicalPaths.Add(candidate.ExactPaths[0]))
            {
                throw new InvalidDataException(
                    "Corruption report contained a duplicate physical cache path");
            }
        }

        report.Candidates = report.Candidates
            .OrderBy(candidate => candidate.Service, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        var projection = ProjectCounts(report.Candidates);
        var detectionProjection = ProjectDetectionCounts(report.Candidates, expectedDetectionMethod);
        if (!CountMapsEqual(report.ServiceCounts, projection)
            || !CountMapsEqual(report.DetectionCounts, detectionProjection, allowZero: true)
            || report.Total != projection.Values.Sum())
        {
            throw new InvalidDataException(
                "Corruption report count projections did not match its candidate evidence");
        }

        report.ServiceCounts = projection;
        report.DetectionCounts = detectionProjection;
        report.Total = projection.Values.Sum();
    }

    internal async Task PersistCompletedScanAsync(
        Guid scanId,
        int threshold,
        int lookbackDays,
        CorruptionDetectionMethod detectionMethod,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        IReadOnlyList<DatasourceCorruptionReport> datasourceReports,
        StructuralScanMode? scanMode = null,
        CancellationToken cancellationToken = default)
    {
        scanMode = NormalizeStructuralScanMode(detectionMethod, scanMode);
        ValidateScanInput(threshold, lookbackDays, detectionMethod, scanMode);
        if (startedAtUtc.Kind != DateTimeKind.Utc
            || startedAtUtc.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new InvalidDataException(
                "Corruption scan start must be a whole-second UTC timestamp");
        }

        if (datasourceReports.Any(item => item.Report.Cancelled))
        {
            throw new InvalidDataException(
                "Cancelled corruption report cannot be persisted as a completed scan");
        }

        ValidateCrossDatasourceIdentity(datasourceReports);
        var expectedScanStartedUtc = FormatScanStartedUtc(startedAtUtc);
        if (datasourceReports.Any(item =>
                item.Report.ContractVersion != CorruptionReport.SupportedContractVersion
                || item.Report.DetectionMethod != detectionMethod
                || (detectionMethod == CorruptionDetectionMethod.RepeatedMiss
                    && (item.Report.Settings.Threshold != threshold
                        || item.Report.Settings.LookbackDays != lookbackDays))
                || !string.Equals(
                    item.Report.ScanStartedUtc,
                    expectedScanStartedUtc,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Datasource corruption report metadata did not match the completed scan");
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
                var persistenceMode = detectionMethod.ToPersistenceMode();
                await dbContext.CachedCorruptionScans
                    .Where(scan => scan.DetectionMode == persistenceMode && scan.IsCurrent)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(scan => scan.IsCurrent, false),
                        cancellationToken);

                dbContext.CachedCorruptionScans.Add(new CachedCorruptionScan
                {
                    ScanId = scanId,
                    DetectionMode = persistenceMode,
                    ScanMode = scanMode,
                    IsCurrent = true,
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

                var allCandidates = datasourceReports
                    .SelectMany(report => report.Report.Candidates)
                    .ToList();
                var reportSettings = datasourceReports.Count > 0
                    ? datasourceReports[0].Report.Settings
                    : new CorruptionScanSettings
                    {
                        Threshold = detectionMethod == CorruptionDetectionMethod.RepeatedMiss ? threshold : null,
                        LookbackDays = detectionMethod == CorruptionDetectionMethod.RepeatedMiss ? lookbackDays : null
                    };
                dbContext.CachedCorruptionDetections.Add(new CachedCorruptionDetection
                {
                    ScanId = scanId,
                    ServiceName = ProjectionServiceName,
                    DatasourceName = ProjectionDatasourceName,
                    CorruptedChunkCount = 0,
                    CandidatesJson = JsonSerializer.Serialize(
                        new CachedCorruptionProjection
                        {
                            Settings = reportSettings,
                            DetectionCounts = ProjectDetectionCounts(allCandidates, detectionMethod),
                            Coverage = AggregateCoverage(datasourceReports.Select(report => report.Report.Coverage))
                        },
                        _candidateJsonOptions),
                    RemovalAllowed = false,
                    LastDetectedUtc = completedAtUtc,
                    CreatedAtUtc = completedAtUtc
                });

                await dbContext.SaveChangesAsync(cancellationToken);

                var completedStatus = OperationStatus.Completed.ToWireString();
                var retainedScanCandidates = await dbContext.CachedCorruptionScans
                    .AsNoTracking()
                    .Where(scan =>
                        scan.DetectionMode == persistenceMode
                        && scan.ContractVersion == CorruptionReport.SupportedContractVersion
                        && scan.Status == completedStatus)
                    .Select(scan => new { scan.ScanId, scan.IsCurrent, scan.CompletedAtUtc })
                    .ToListAsync(cancellationToken);
                var staleScanIds = retainedScanCandidates
                    .OrderByDescending(scan => scan.IsCurrent)
                    .ThenByDescending(scan => scan.CompletedAtUtc)
                    .ThenByDescending(
                        scan => scan.ScanId.ToString("D"),
                        StringComparer.Ordinal)
                    .Skip(MaxRetainedScansPerMethod)
                    .Select(scan => scan.ScanId)
                    .ToList();
                if (staleScanIds.Count > 0)
                {
                    await dbContext.CachedCorruptionDetections
                        .Where(row => staleScanIds.Contains(row.ScanId))
                        .ExecuteDeleteAsync(cancellationToken);
                    await dbContext.CachedCorruptionScans
                        .Where(scan => staleScanIds.Contains(scan.ScanId))
                        .ExecuteDeleteAsync(cancellationToken);
                }

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

    /// <summary>Returns the explicit current scan for one method, including an empty scan.</summary>
    public async Task<CachedCorruptionResult?> GetDetectionAsync(
        CorruptionDetectionMethod detectionMethod,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(detectionMethod))
        {
            throw new ValidationException("Unsupported corruption detection method");
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var persistenceMode = detectionMethod.ToPersistenceMode();
        var scan = await dbContext.CachedCorruptionScans
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.DetectionMode == persistenceMode && item.IsCurrent,
                cancellationToken);
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

        return await LoadCachedResultAsync(dbContext, scan, cancellationToken);
    }

    /// <summary>
    /// Loads an exact explicitly-current scan. This is the scan-bound lookup for
    /// all-services removal preflight and never elects a row by ordering.
    /// </summary>
    public async Task<CachedCorruptionResult> GetCurrentDetectionByScanIdAsync(
        Guid scanId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await RequireCurrentScanAsync(dbContext, scanId, cancellationToken);
        return await LoadCachedResultAsync(dbContext, scan, cancellationToken);
    }

    private static async Task<CachedCorruptionResult> LoadCachedResultAsync(
        AppDbContext dbContext,
        CachedCorruptionScan scan,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scan.ScanId)
            .ToListAsync(cancellationToken);
        var projectionRow = rows.SingleOrDefault(row => row.ServiceName == ProjectionServiceName);
        if (projectionRow == null)
        {
            throw new InvalidDataException("Stored v4 corruption scan omitted its projection row");
        }

        var projection = DeserializeProjection(projectionRow);
        var candidates = rows
            .Where(row => row.ServiceName != ProjectionServiceName)
            .SelectMany(DeserializeCandidates)
            .ToList();
        ValidateStoredCandidates(scan, candidates);
        return BuildCachedResult(scan, candidates, projection);
    }

    private static CachedCorruptionProjection DeserializeProjection(
        CachedCorruptionDetection projectionRow) =>
        JsonSerializer.Deserialize<CachedCorruptionProjection>(
            projectionRow.CandidatesJson,
            _candidateJsonOptions)
        ?? throw new InvalidDataException("Stored v4 corruption scan projection was null");

    private static CachedCorruptionResult BuildCachedResult(
        CachedCorruptionScan scan,
        IReadOnlyCollection<CorruptionCandidate> candidates,
        CachedCorruptionProjection projection)
    {
        ValidateStoredProjection(scan, projection);
        var counts = ProjectCounts(candidates);
        var detectionCounts = ProjectDetectionCounts(candidates, scan.DetectionMode.ToDetectionMethod());
        if (!CountMapsEqual(projection.DetectionCounts, detectionCounts, allowZero: true))
        {
            throw new InvalidDataException("Stored corruption source counts did not match candidate evidence");
        }

        return new CachedCorruptionResult
        {
            HasCachedResults = true,
            ScanId = scan.ScanId,
            Threshold = scan.Threshold,
            LookbackDays = scan.LookbackDays,
            ContractVersion = scan.ContractVersion,
            DetectionMethod = scan.DetectionMode.ToDetectionMethod(),
            ScanMode = scan.ScanMode,
            Settings = projection.Settings,
            CorruptionCounts = counts,
            DetectionCounts = detectionCounts,
            Coverage = projection.Coverage,
            LastDetectionTime = scan.CompletedAtUtc,
            TotalServicesWithCorruption = counts.Count,
            TotalCorruptedChunks = counts.Values.Sum()
        };
    }

    /// <summary>
    /// Returns retained supported snapshots, newest first, with at most three
    /// entries per method. Currentness is reported from explicit database state.
    /// </summary>
    public async Task<IReadOnlyList<CorruptionScanHistorySummary>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var completedStatus = OperationStatus.Completed.ToWireString();
        var scans = await dbContext.CachedCorruptionScans
            .AsNoTracking()
            .Where(scan =>
                scan.ContractVersion == CorruptionReport.SupportedContractVersion
                && scan.Status == completedStatus
                && (scan.DetectionMode == CorruptionDetectionMode.RepeatedMiss
                    || scan.DetectionMode == CorruptionDetectionMode.Structural))
            .ToListAsync(cancellationToken);
        var retainedScans = scans
            .Where(IsSupportedScan)
            .GroupBy(scan => scan.DetectionMode)
            .SelectMany(group => group
                .OrderByDescending(scan => scan.CompletedAtUtc)
                .ThenByDescending(
                    scan => scan.ScanId.ToString("D"),
                    StringComparer.Ordinal)
                .Take(MaxRetainedScansPerMethod))
            .OrderByDescending(scan => scan.CompletedAtUtc)
            .ThenByDescending(
                scan => scan.ScanId.ToString("D"),
                StringComparer.Ordinal)
            .ToList();
        if (retainedScans.Count == 0)
        {
            return [];
        }

        var scanIds = retainedScans.Select(scan => scan.ScanId).ToList();
        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => scanIds.Contains(row.ScanId))
            .ToListAsync(cancellationToken);
        var rowsByScan = rows.ToLookup(row => row.ScanId);
        var summaries = new List<CorruptionScanHistorySummary>(retainedScans.Count);
        foreach (var scan in retainedScans)
        {
            var scanRows = rowsByScan[scan.ScanId].ToList();
            var projectionRow = scanRows.SingleOrDefault(row => row.ServiceName == ProjectionServiceName)
                ?? throw new InvalidDataException("Stored v4 corruption scan omitted its projection row");
            var projection = DeserializeProjection(projectionRow);
            ValidateStoredProjection(scan, projection);

            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in scanRows.Where(row => row.ServiceName != ProjectionServiceName))
            {
                if (string.IsNullOrWhiteSpace(row.ServiceName) || row.CorruptedChunkCount < 0)
                {
                    throw new InvalidDataException("Stored corruption history counts were malformed");
                }

                counts[row.ServiceName] = checked(
                    counts.GetValueOrDefault(row.ServiceName) + row.CorruptedChunkCount);
            }

            counts = SortCounts(counts);
            var totalCorruptedChunks = counts.Values.Sum();
            var expectedDetectionCounts = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [scan.DetectionMode.ToDetectionMethod().ToWireString()] = totalCorruptedChunks
            };
            if (!CountMapsEqual(
                    projection.DetectionCounts,
                    expectedDetectionCounts,
                    allowZero: true))
            {
                throw new InvalidDataException(
                    "Stored corruption history counts did not match its projection");
            }

            summaries.Add(new CorruptionScanHistorySummary
            {
                ScanId = scan.ScanId,
                ContractVersion = scan.ContractVersion,
                DetectionMethod = scan.DetectionMode.ToDetectionMethod(),
                ScanMode = scan.ScanMode,
                IsCurrent = scan.IsCurrent,
                StartedAtUtc = scan.StartedAtUtc,
                CompletedAtUtc = scan.CompletedAtUtc,
                Settings = projection.Settings,
                CorruptionCounts = counts,
                DetectionCounts = expectedDetectionCounts,
                Coverage = projection.Coverage,
                TotalServicesWithCorruption = counts.Count,
                TotalCorruptedChunks = totalCorruptedChunks
            });
        }

        return summaries;
    }

    /// <summary>
    /// Loads validated evidence from an exact retained snapshot. This read-only
    /// path deliberately does not call the actionable current-scan gate.
    /// </summary>
    public async Task<IReadOnlyList<CorruptionCandidate>> GetSnapshotDetailsAsync(
        Guid scanId,
        string service,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await RequireSupportedSnapshotAsync(dbContext, scanId, cancellationToken);
        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row =>
                row.ScanId == scanId
                && row.ServiceName != ProjectionServiceName
                && row.ServiceName == service)
            .OrderBy(row => row.DatasourceName)
            .ToListAsync(cancellationToken);
        var candidates = rows
            .SelectMany(DeserializeCandidates)
            .OrderBy(candidate => candidate.Datasource, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        ValidateStoredCandidates(scan, candidates, service);
        return candidates;
    }

    /// <summary>
    /// Deletes one exact supported snapshot and its evidence transactionally.
    /// Deleting a current row never promotes an older historical row.
    /// </summary>
    public async Task DeleteSnapshotAsync(
        Guid scanId,
        CancellationToken cancellationToken = default)
    {
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
                var scan = await dbContext.CachedCorruptionScans
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item => item.ScanId == scanId, cancellationToken);
                if (scan == null || !IsSupportedScan(scan))
                {
                    throw new NotFoundException("Corruption scan");
                }

                await dbContext.CachedCorruptionDetections
                    .Where(row => row.ScanId == scanId)
                    .ExecuteDeleteAsync(cancellationToken);
                var deletedHeaders = await dbContext.CachedCorruptionScans
                    .Where(item => item.ScanId == scanId)
                    .ExecuteDeleteAsync(cancellationToken);
                if (deletedHeaders != 1)
                {
                    throw new ConflictException(
                        "The corruption scan changed before it could be deleted");
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                if (ex is not NotFoundException)
                {
                    _logger.LogError(
                        ex,
                        "[CorruptionDetection] Failed to delete saved scan {ScanId}; rolling back",
                        scanId);
                }
                throw;
            }
        });
    }

    /// <summary>Loads stored candidates for one service without rerunning detection.</summary>
    public async Task<IReadOnlyList<CorruptionCandidate>> GetDetailsAsync(
        Guid scanId,
        string service,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await RequireCurrentScanAsync(dbContext, scanId, cancellationToken);
        var rows = await dbContext.CachedCorruptionDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scanId && row.ServiceName == service)
            .OrderBy(row => row.DatasourceName)
            .ToListAsync(cancellationToken);
        EnsureActionableRows(rows);
        var candidates = rows
            .SelectMany(DeserializeCandidates)
            .OrderBy(candidate => candidate.Datasource, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        ValidateStoredCandidates(scan, candidates, service);
        return candidates;
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
        ValidateStoredCandidates(scan, allCandidates, service);
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
            .GroupBy(candidate => candidate.Datasource!, StringComparer.OrdinalIgnoreCase)
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
            DetectionMethod = scan.DetectionMode.ToDetectionMethod(),
            Service = service,
            CandidatesByDatasource = candidatesByDatasource
        };
    }

    private static void ValidateStoredProjection(
        CachedCorruptionScan scan,
        CachedCorruptionProjection projection)
    {
        if (projection.Settings is null || projection.DetectionCounts is null)
        {
            throw new InvalidDataException("Stored corruption scan projection omitted required fields");
        }

        var method = scan.DetectionMode.ToDetectionMethod();
        var settingsValid = method switch
        {
            CorruptionDetectionMethod.RepeatedMiss =>
                projection.Settings.Threshold == scan.Threshold
                && projection.Settings.LookbackDays == scan.LookbackDays
                && projection.Settings.MinimumStableAgeSeconds is null
                && projection.Settings.MaximumPrefixBytes is null
                && projection.Coverage is null,
            CorruptionDetectionMethod.Structural =>
                projection.Settings.Threshold is null
                && projection.Settings.LookbackDays is null
                && projection.Settings.MinimumStableAgeSeconds == StructuralMinimumStableAgeSeconds
                && projection.Settings.MaximumPrefixBytes == StructuralMaximumPrefixBytes
                && projection.Coverage is not null,
            _ => false
        };
        if (!settingsValid)
        {
            throw new InvalidDataException("Stored corruption scan projection settings were malformed");
        }

        if (projection.Coverage != null)
        {
            // Coverage describes the original immutable scan, so its checked count still includes
            // candidates that may since have been removed. Validate shape here, not current totals.
            ValidateCoverage(projection.Coverage);
        }
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
                var currentScan = await RequireCurrentScanAsync(dbContext, scanId, cancellationToken);
                var rows = await dbContext.CachedCorruptionDetections
                    .Where(row => row.ScanId == scanId && row.ServiceName != ProjectionServiceName)
                    .ToListAsync(cancellationToken);
                var matchedIds = new HashSet<string>(StringComparer.Ordinal);
                var remainingCandidates = new List<CorruptionCandidate>();
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
                        remainingCandidates.AddRange(remaining);
                    }
                }

                if (!matchedIds.SetEquals(removedIds))
                {
                    throw new ConflictException(changedMessage);
                }

                var projectionRow = await dbContext.CachedCorruptionDetections.SingleAsync(
                    row => row.ScanId == scanId && row.ServiceName == ProjectionServiceName,
                    cancellationToken);
                var projection = JsonSerializer.Deserialize<CachedCorruptionProjection>(
                        projectionRow.CandidatesJson,
                        _candidateJsonOptions)
                    ?? throw new InvalidDataException("Stored v4 corruption scan projection was null");
                projection.DetectionCounts = ProjectDetectionCounts(
                    remainingCandidates,
                    currentScan.DetectionMode.ToDetectionMethod());
                projectionRow.CandidatesJson = JsonSerializer.Serialize(projection, _candidateJsonOptions);

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
            if (candidate.Datasource != null
                && !string.Equals(
                    candidate.Datasource,
                    row.DatasourceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Stored corruption evidence row {row.Id} contained a forged datasource");
            }
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

    private static void ValidateStoredCandidates(
        CachedCorruptionScan scan,
        IReadOnlyCollection<CorruptionCandidate> candidates,
        string? expectedService = null)
    {
        var method = scan.DetectionMode.ToDetectionMethod();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var evidenceMatches = method switch
            {
                CorruptionDetectionMethod.RepeatedMiss => candidate.Evidence is RepeatedMissCorruptionEvidence,
                CorruptionDetectionMethod.Structural => candidate.Evidence is StructuralCorruptionEvidence,
                _ => false
            };
            if (!evidenceMatches
                || string.IsNullOrWhiteSpace(candidate.CandidateId)
                || string.IsNullOrWhiteSpace(candidate.Service)
                || candidate.ExactPaths is not { Count: 1 }
                || string.IsNullOrWhiteSpace(candidate.ExactPaths[0])
                || string.IsNullOrWhiteSpace(candidate.Datasource)
                || (expectedService != null
                    && !string.Equals(candidate.Service, expectedService, StringComparison.OrdinalIgnoreCase))
                || !ids.Add(candidate.CandidateId)
                || !paths.Add(candidate.ExactPaths[0]))
            {
                throw new InvalidDataException("Stored v4 corruption evidence failed closed validation");
            }


            ValidateCandidateShape(
                candidate,
                method,
                scan.Threshold,
                scan.LookbackDays,
                scan.StartedAtUtc.AsUtc());
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

    private static Dictionary<string, long> ProjectDetectionCounts(
        IEnumerable<CorruptionCandidate> candidates,
        CorruptionDetectionMethod? detectionMethod = null)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (detectionMethod.HasValue)
        {
            counts[detectionMethod.Value.ToWireString()] = 0;
        }
        foreach (var candidate in candidates)
        {
            var method = candidate.Evidence switch
            {
                RepeatedMissCorruptionEvidence => CorruptionDetectionMethod.RepeatedMiss,
                StructuralCorruptionEvidence => CorruptionDetectionMethod.Structural,
                _ => throw new InvalidDataException("Corruption candidate has an unsupported evidence branch")
            };
            IncrementCount(counts, method.ToWireString());
        }

        return counts
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static CorruptionScanCoverage? AggregateCoverage(
        IEnumerable<CorruptionScanCoverage?> coverages)
    {
        CorruptionScanCoverage? aggregate = null;
        foreach (var coverage in coverages)
        {
            if (coverage == null)
            {
                continue;
            }

            aggregate ??= new CorruptionScanCoverage();
            aggregate.Add(coverage);
        }

        if (aggregate != null)
        {
            aggregate.SkippedByReason = aggregate.SkippedByReason
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        return aggregate;
    }

    private static void IncrementCount(Dictionary<string, long> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;

    private static Dictionary<string, long> SortCounts(Dictionary<string, long> counts) =>
        counts
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

    private static bool CountMapsEqual(
        Dictionary<string, long> actual,
        Dictionary<string, long> expected,
        bool allowZero = false)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        var normalized = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (service, count) in actual)
        {
            if (string.IsNullOrWhiteSpace(service)
                || (!allowZero && count <= 0)
                || (allowZero && count < 0)
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
        CorruptionDetectionMethod detectionMethod,
        int threshold,
        int lookbackDays,
        DateTime scanStartedUtc)
    {
        switch (candidate.Evidence)
        {
            case RepeatedMissCorruptionEvidence repeatedMiss
                when detectionMethod == CorruptionDetectionMethod.RepeatedMiss:
                ValidateRepeatedMissEvidence(repeatedMiss, threshold, lookbackDays, scanStartedUtc);
                return;
            case StructuralCorruptionEvidence structural
                when detectionMethod == CorruptionDetectionMethod.Structural:
                ValidateStructuralEvidence(structural, scanStartedUtc);
                return;
            default:
                throw new InvalidDataException(
                    "Corruption candidate evidence did not match the report detection method");
        }
    }

    private static void ValidateRepeatedMissEvidence(
        RepeatedMissCorruptionEvidence evidence,
        int threshold,
        int lookbackDays,
        DateTime scanStartedUtc)
    {
        if (evidence.ObservedRange is null
            || evidence.CacheSlice is null
            || evidence.Observations is null
            || evidence.Observations.Any(observation => observation is null)
            || string.IsNullOrWhiteSpace(evidence.RawUrl)
            || string.IsNullOrWhiteSpace(evidence.NormalizedUri)
            || !IsValidObservedRange(evidence.ObservedRange)
            || !IsValidCacheSlice(evidence.CacheSlice)
            || evidence.EvidenceCount != threshold
            || evidence.EvidenceCount != evidence.Observations.Count)
        {
            throw new InvalidDataException(
                "Corruption candidate did not contain threshold-qualified exact-path evidence");
        }

        if (!TryParseCanonicalUtc(evidence.FirstSeen, out var firstSeenUtc)
            || !TryParseCanonicalUtc(evidence.LastSeen, out var lastSeenUtc)
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

        var observedTimes = new List<DateTime>(evidence.Observations.Count);
        foreach (var observation in evidence.Observations)
        {
            if (!string.Equals(observation.Method, "GET", StringComparison.Ordinal)
                || observation.HttpStatus is not (200 or 206)
                || !string.Equals(observation.CacheStatus, "MISS", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(observation.RawUrl)
                || string.IsNullOrWhiteSpace(observation.ClientIp)
                || observation.RawRange == string.Empty
                || observation.BytesServed < 0
                || !TryParseCanonicalUtc(observation.Timestamp, out var observedAtUtc)
                || observedAtUtc < firstSeenUtc
                || observedAtUtc > lastSeenUtc)
            {
                throw new InvalidDataException(
                    "Corruption candidate contained an ineligible repeated-MISS observation");
            }
            observedTimes.Add(observedAtUtc);
        }

        if (observedTimes.Count == 0
            || observedTimes[0] != firstSeenUtc
            || observedTimes[^1] != lastSeenUtc
            || observedTimes.Zip(observedTimes.Skip(1)).Any(pair => pair.First > pair.Second)
            || !string.Equals(evidence.Observations[0].RawUrl, evidence.RawUrl, StringComparison.Ordinal)
            || !RawRangeMatches(evidence.Observations[0].RawRange, evidence.ObservedRange))
        {
            throw new InvalidDataException(
                "Corruption candidate observations did not match their ordered evidence identity");
        }
    }

    private static bool RawRangeMatches(string? rawRange, ObservedByteRange observedRange)
    {
        var value = rawRange?.Trim() ?? string.Empty;
        if (observedRange.Kind == "no_range")
        {
            return value is "" or "-";
        }

        if (observedRange.Kind != "inclusive"
            || !observedRange.Start.HasValue
            || !observedRange.End.HasValue
            || !value.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value[6..].Split('-', StringSplitOptions.None);
        return parts.Length == 2
            && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            && start == observedRange.Start
            && end == observedRange.End;
    }

    private static bool IsValidObservedRange(ObservedByteRange range) => range.Kind switch
    {
        "no_range" => range.Start is null && range.End is null,
        "inclusive" => range.Start.HasValue && range.End.HasValue && range.Start <= range.End,
        _ => false
    };

    private static bool IsValidCacheSlice(CacheSliceIdentity slice) => slice.Kind switch
    {
        "no_range" or "noslice" => slice.Start is null && slice.End is null,
        "ranged" => slice.Start.HasValue && slice.End.HasValue && slice.Start <= slice.End,
        _ => false
    };

    private static void ValidateStructuralEvidence(
        StructuralCorruptionEvidence evidence,
        DateTime scanStartedUtc)
    {
        if (evidence.Issues is null
            || evidence.Issues.Count == 0
            || evidence.Issues.Distinct().Count() != evidence.Issues.Count
            || !evidence.Issues.SequenceEqual(evidence.Issues.OrderBy(issue => issue.ToString(), StringComparer.Ordinal))
            || !string.Equals(evidence.CacheKeyEncoding, "hex", StringComparison.Ordinal)
            || (evidence.CacheKey.Length == 0
                && !evidence.Issues.Any(AllowsMissingStructuralCacheKey))
            || evidence.CacheKey.Length % 2 != 0
            || evidence.CacheKey.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character))
            || string.IsNullOrWhiteSpace(evidence.CacheKeyMd5)
            || evidence.CacheKeyMd5.Length != 32
            || evidence.CacheKeyMd5.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character))
            || evidence.CacheVersion != 5
            || (evidence.HttpStatus.HasValue && evidence.HttpStatus is not (200 or 206))
            || (evidence.ActualPayloadLength.HasValue
                && evidence.ActualPayloadLength > evidence.FileLength)
            || evidence.Fingerprint is null
            || evidence.Fingerprint.Length != evidence.FileLength
            || !TryParseUtc(evidence.DetectedAtUtc, out var detectedAtUtc)
            || detectedAtUtc > scanStartedUtc)
        {
            throw new InvalidDataException("Structural corruption evidence was malformed");
        }

        if (evidence.BodyStart.HasValue
            && evidence.BodyStart <= evidence.FileLength
            && evidence.ActualPayloadLength.HasValue
            && evidence.ActualPayloadLength != evidence.FileLength - evidence.BodyStart)
        {
            throw new InvalidDataException("Structural payload length did not match its file offsets");
        }

        if (evidence.BodyStart.HasValue
            && (evidence.BodyStart > evidence.FileLength
                || (evidence.HeaderStart.HasValue && evidence.HeaderStart > evidence.BodyStart)))
        {
            if (!evidence.Issues.Any(AllowsOutOfRangeStructuralOffsets))
            {
                throw new InvalidDataException("Structural offsets were inconsistent without a matching issue");
            }
        }
    }

    private static bool AllowsMissingStructuralCacheKey(StructuralCorruptionIssue issue) => issue is
        StructuralCorruptionIssue.EmptyCacheFile
        or StructuralCorruptionIssue.TruncatedCacheHeader
        or StructuralCorruptionIssue.MalformedCacheHeader
        or StructuralCorruptionIssue.InvalidPayloadOffset
        or StructuralCorruptionIssue.TruncatedBeforePayload;

    // The scanner proves a header offset before it can prove the file is long enough to hold
    // it, so a truncated or malformed header legitimately reports header/body offsets that fall
    // beyond the file length (or a header that starts after the body). Every other finding kind
    // is only emitted once the offsets have been validated against the file, so it must never
    // carry an out-of-range offset.
    private static bool AllowsOutOfRangeStructuralOffsets(StructuralCorruptionIssue issue) => issue is
        StructuralCorruptionIssue.TruncatedCacheHeader
        or StructuralCorruptionIssue.MalformedCacheHeader
        or StructuralCorruptionIssue.InvalidPayloadOffset
        or StructuralCorruptionIssue.TruncatedBeforePayload;

    private static void ValidateCoverage(
        CorruptionScanCoverage coverage,
        int? candidateCount = null,
        long reusedConsistent = 0)
    {
        if (coverage.FilesSeen < 0
            || coverage.FilesChecked < 0
            || coverage.FilesChecked > coverage.FilesSeen
            || coverage.Consistent < 0
            || coverage.BytesRead < 0
            || coverage.SparseFiles < 0
            || coverage.SparseFiles > coverage.FilesSeen
            || coverage.IoErrors < 0
            || reusedConsistent < 0
            || reusedConsistent > coverage.Consistent
            || coverage.SkippedByReason is null
            || coverage.SkippedByReason.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0))
        {
            throw new InvalidDataException("Structural scan coverage was malformed");
        }


        if (candidateCount.HasValue
            && checked(coverage.FilesChecked + reusedConsistent)
                != checked(coverage.Consistent + candidateCount.Value))
        {
            throw new InvalidDataException(
                "Structural scan coverage did not match its actionable candidate count");
        }
    }

    private static void ValidateStructuralProgressSummary(
        StructuralScanStatusResponse summary,
        StructuralScanMode expectedMode,
        CorruptionScanCoverage coverage,
        int candidateCount)
    {
        if (!string.Equals(summary.ScanMode, expectedMode.ToWireString(), StringComparison.Ordinal)
            || !StructuralEffectiveScanModeExtensions.TryParseWire(summary.EffectiveScanMode, out var effectiveMode)
            || !StructuralBaselineStatusExtensions.TryParseWire(summary.BaselineStatus, out var baselineStatus)
            || summary.FilesDiscovered != coverage.FilesSeen
            || summary.FilesProcessed != summary.FilesDiscovered
            || summary.FilesReused > summary.FilesDiscovered
            || summary.FilesInspected > summary.FilesDiscovered
            || checked(summary.FilesReused + summary.FilesInspected) != summary.FilesDiscovered
            || summary.FilesRevalidated > summary.FilesInspected
            || summary.InvalidFiles != candidateCount
            || summary.FilesPendingRetry > summary.FilesInspected
            // This validator runs only after the Rust child exited successfully and immediately
            // before the report can replace the authoritative cached result. An incomplete
            // baseline means traversal or cancellation prevented publication; accepting that
            // partial report would erase the last complete result while the prior baseline stayed
            // active. Fail closed and let the normal operation failure path preserve both.
            || baselineStatus != StructuralBaselineStatus.Ready
            || !summary.StateCommitted
            || (expectedMode == StructuralScanMode.Full && effectiveMode != StructuralEffectiveScanMode.Full)
            || (expectedMode == StructuralScanMode.Incremental
                && effectiveMode is not (StructuralEffectiveScanMode.Incremental
                    or StructuralEffectiveScanMode.Baseline)))
        {
            throw new InvalidDataException("Structural progress summary was inconsistent with the completed report");
        }
    }

    private static void ValidateCrossDatasourceIdentity(
        IReadOnlyList<DatasourceCorruptionReport> datasourceReports)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var datasourceReport in datasourceReports)
        {
            foreach (var candidate in datasourceReport.Report.Candidates)
            {
                if (!ids.Add(candidate.CandidateId))
                {
                    throw new InvalidDataException(
                        $"Duplicate corruption candidate ID '{candidate.CandidateId}' across datasource reports");
                }

                if (!paths.Add(candidate.ExactPaths.Single()))
                {
                    throw new InvalidDataException(
                        "Duplicate physical cache path across datasource reports");
                }
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

    private static bool TryParseUtc(string value, out DateTime timestampUtc)
    {
        var parsed = DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp);
        timestampUtc = timestamp.UtcDateTime;
        return parsed && value.EndsWith('Z');
    }

    private static Dictionary<string, object?> BuildStructuralContext(
        CorruptionDetectionMethod detectionMethod,
        StructuralScanMode? scanMode)
    {
        var context = new Dictionary<string, object?>
        {
            ["detectionMethod"] = detectionMethod.ToWireString()
        };
        if (scanMode.HasValue)
        {
            context["scanMode"] = scanMode.Value.ToWireString();
        }
        return context;
    }

    private static object? GetContextValue(
        IReadOnlyDictionary<string, object?> context,
        string key) =>
        context.TryGetValue(key, out var value) ? value : null;

    private static void AddStructuralMetrics(
        Dictionary<string, object?> context,
        CorruptionDetectionMetrics metrics)
    {
        if (!metrics.ScanMode.HasValue)
        {
            return;
        }

        context["scanMode"] = metrics.ScanMode.Value.ToWireString();
        context["effectiveScanMode"] = metrics.EffectiveScanMode?.ToWireString();
        context["baselineStatus"] = metrics.BaselineStatus?.ToWireString();
        context["resumed"] = metrics.Resumed;
        context["filesDiscovered"] = metrics.FilesDiscovered;
        context["filesProcessed"] = metrics.FilesProcessed;
        context["filesReused"] = metrics.FilesReused;
        context["filesInspected"] = metrics.FilesInspected;
        context["filesRevalidated"] = metrics.FilesRevalidated;
        context["invalidFiles"] = metrics.InvalidFiles;
        context["filesPendingRetry"] = metrics.FilesPendingRetry;
        context["filesPruned"] = metrics.FilesPruned;
        context["stateEntries"] = metrics.StateEntries;
        context["stateCommitted"] = metrics.StateCommitted;
    }

    internal static void UpdateStructuralProgressMetrics(
        CorruptionDetectionMetrics metrics,
        IReadOnlyDictionary<string, object?>? context)
    {
        if (!metrics.ScanMode.HasValue || context is null)
        {
            return;
        }

        if (TryReadString(context, "scanMode", out var scanModeWire))
        {
            if (!StructuralScanModeExtensions.TryParseWire(scanModeWire, out var scanMode)
                || scanMode != metrics.ScanMode.Value)
            {
                throw new InvalidDataException("Structural progress scan mode did not match the request");
            }
        }

        if (TryReadString(context, "effectiveScanMode", out var effectiveWire))
        {
            if (!StructuralEffectiveScanModeExtensions.TryParseWire(effectiveWire, out var effectiveMode))
            {
                throw new InvalidDataException("Structural progress contained an unknown effective scan mode");
            }
            metrics.EffectiveScanMode = effectiveMode;
        }

        if (TryReadString(context, "baselineStatus", out var baselineWire))
        {
            if (!StructuralBaselineStatusExtensions.TryParseWire(baselineWire, out var baselineStatus))
            {
                throw new InvalidDataException("Structural progress contained an unknown baseline status");
            }
            metrics.BaselineStatus = baselineStatus;
        }

        if (TryReadBoolean(context, "resumed", out var resumed)) metrics.Resumed = resumed;
        if (TryReadBoolean(context, "stateCommitted", out var committed)) metrics.StateCommitted = committed;
        if (TryReadNonnegativeInt64(context, "filesDiscovered", out var discovered)) metrics.FilesDiscovered = discovered;
        if (TryReadNonnegativeInt64(context, "filesProcessed", out var processed)) metrics.FilesProcessed = processed;
        if (TryReadNonnegativeInt64(context, "filesReused", out var reused)) metrics.FilesReused = reused;
        if (TryReadNonnegativeInt64(context, "filesInspected", out var inspected)) metrics.FilesInspected = inspected;
        if (TryReadNonnegativeInt64(context, "filesRevalidated", out var revalidated)) metrics.FilesRevalidated = revalidated;
        if (TryReadNonnegativeInt64(context, "invalidFiles", out var invalid)) metrics.InvalidFiles = invalid;
        if (TryReadNonnegativeInt64(context, "filesPendingRetry", out var retry)) metrics.FilesPendingRetry = retry;
        if (TryReadNonnegativeInt64(context, "filesPruned", out var pruned)) metrics.FilesPruned = pruned;
        if (TryReadNonnegativeInt64(context, "stateEntries", out var entries)) metrics.StateEntries = entries;
    }

    private static void ResetStructuralProgressMetrics(CorruptionDetectionMetrics metrics)
    {
        metrics.EffectiveScanMode = null;
        metrics.BaselineStatus = null;
        metrics.StateCommitted = false;
        metrics.Resumed = false;
        metrics.FilesDiscovered = 0;
        metrics.FilesProcessed = 0;
        metrics.FilesReused = 0;
        metrics.FilesInspected = 0;
        metrics.FilesRevalidated = 0;
        metrics.InvalidFiles = 0;
        metrics.FilesPendingRetry = 0;
        metrics.FilesPruned = 0;
        metrics.StateEntries = 0;
    }

    internal static StructuralScanStatusResponse? SnapshotStructuralSummary(
        CorruptionDetectionMetrics metrics)
    {
        if (metrics.ScanMode is not { } scanMode
            || metrics.EffectiveScanMode is not { } effectiveMode
            || metrics.BaselineStatus is not { } baselineStatus)
        {
            return null;
        }

        return new StructuralScanStatusResponse
        {
            ScanMode = scanMode.ToWireString(),
            EffectiveScanMode = effectiveMode.ToWireString(),
            BaselineStatus = baselineStatus.ToWireString(),
            Resumed = metrics.Resumed,
            FilesDiscovered = metrics.FilesDiscovered,
            FilesProcessed = metrics.FilesProcessed,
            FilesReused = metrics.FilesReused,
            FilesInspected = metrics.FilesInspected,
            FilesRevalidated = metrics.FilesRevalidated,
            InvalidFiles = metrics.InvalidFiles,
            FilesPendingRetry = metrics.FilesPendingRetry,
            FilesPruned = metrics.FilesPruned,
            StateEntries = metrics.StateEntries,
            StateCommitted = metrics.StateCommitted
        };
    }

    private static StructuralScanStatusResponse? AggregateStructuralSummary(
        IReadOnlyList<DatasourceCorruptionReport> datasourceReports,
        StructuralScanMode? scanMode)
    {
        if (!scanMode.HasValue)
        {
            return null;
        }

        var summaries = datasourceReports
            .Select(report => report.ScanSummary
                ?? throw new InvalidDataException(
                    $"Structural scan for datasource '{report.DatasourceName}' omitted terminal state progress"))
            .ToList();
        if (summaries.Count == 0)
        {
            throw new InvalidDataException("Structural scan did not process any datasource state");
        }
        if (summaries.Any(summary =>
                !string.Equals(summary.ScanMode, scanMode.Value.ToWireString(), StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Structural datasource state did not match the requested scan mode");
        }

        var aggregate = new StructuralScanStatusResponse
        {
            ScanMode = scanMode.Value.ToWireString(),
            EffectiveScanMode = summaries.Any(summary => summary.EffectiveScanMode == "baseline")
                ? "baseline"
                : scanMode.Value.ToWireString(),
            BaselineStatus = summaries.All(summary => summary.BaselineStatus == "ready")
                ? "ready"
                : "incomplete",
            Resumed = summaries.Any(summary => summary.Resumed),
            StateCommitted = summaries.All(summary => summary.StateCommitted)
        };

        foreach (var summary in summaries)
        {
            aggregate.FilesDiscovered = checked(aggregate.FilesDiscovered + summary.FilesDiscovered);
            aggregate.FilesProcessed = checked(aggregate.FilesProcessed + summary.FilesProcessed);
            aggregate.FilesReused = checked(aggregate.FilesReused + summary.FilesReused);
            aggregate.FilesInspected = checked(aggregate.FilesInspected + summary.FilesInspected);
            aggregate.FilesRevalidated = checked(aggregate.FilesRevalidated + summary.FilesRevalidated);
            aggregate.InvalidFiles = checked(aggregate.InvalidFiles + summary.InvalidFiles);
            aggregate.FilesPendingRetry = checked(aggregate.FilesPendingRetry + summary.FilesPendingRetry);
            aggregate.FilesPruned = checked(aggregate.FilesPruned + summary.FilesPruned);
            aggregate.StateEntries = checked(aggregate.StateEntries + summary.StateEntries);
        }

        return aggregate;
    }

    private static void ApplyStructuralSummary(
        CorruptionDetectionMetrics metrics,
        StructuralScanStatusResponse? summary)
    {
        if (summary is null)
        {
            return;
        }

        if (!StructuralEffectiveScanModeExtensions.TryParseWire(summary.EffectiveScanMode, out var effectiveMode)
            || !StructuralBaselineStatusExtensions.TryParseWire(summary.BaselineStatus, out var baselineStatus))
        {
            throw new InvalidDataException("Structural terminal state used an unknown wire value");
        }

        metrics.EffectiveScanMode = effectiveMode;
        metrics.BaselineStatus = baselineStatus;
        metrics.Resumed = summary.Resumed;
        metrics.FilesDiscovered = summary.FilesDiscovered;
        metrics.FilesProcessed = summary.FilesProcessed;
        metrics.FilesReused = summary.FilesReused;
        metrics.FilesInspected = summary.FilesInspected;
        metrics.FilesRevalidated = summary.FilesRevalidated;
        metrics.InvalidFiles = summary.InvalidFiles;
        metrics.FilesPendingRetry = summary.FilesPendingRetry;
        metrics.FilesPruned = summary.FilesPruned;
        metrics.StateEntries = summary.StateEntries;
        metrics.StateCommitted = summary.StateCommitted;
    }

    private static bool TryReadString(
        IReadOnlyDictionary<string, object?> context,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!context.TryGetValue(key, out var raw)) return false;
        if (raw is string text)
        {
            value = text;
            return true;
        }
        if (raw is JsonElement { ValueKind: JsonValueKind.String } json)
        {
            value = json.GetString() ?? string.Empty;
            return true;
        }
        throw new InvalidDataException($"Structural progress field '{key}' was not a string");
    }

    private static bool TryReadBoolean(
        IReadOnlyDictionary<string, object?> context,
        string key,
        out bool value)
    {
        value = false;
        if (!context.TryGetValue(key, out var raw)) return false;
        if (raw is bool boolean)
        {
            value = boolean;
            return true;
        }
        if (raw is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } json)
        {
            value = json.GetBoolean();
            return true;
        }
        throw new InvalidDataException($"Structural progress field '{key}' was not a boolean");
    }

    private static bool TryReadNonnegativeInt64(
        IReadOnlyDictionary<string, object?> context,
        string key,
        out long value)
    {
        value = 0;
        if (!context.TryGetValue(key, out var raw)) return false;
        value = raw switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            uint number => number,
            ulong number when number <= long.MaxValue => (long)number,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt64(out var number) => number,
            _ => throw new InvalidDataException($"Structural progress field '{key}' was not an integer")
        };
        if (value < 0)
        {
            throw new InvalidDataException($"Structural progress field '{key}' was negative");
        }
        return true;
    }

    internal static double CalculateOverallProgress(
        int datasourceIndex,
        int datasourceCount,
        double datasourcePercent)
    {
        if (datasourceCount <= 0
            || datasourceIndex < 0
            || datasourceIndex >= datasourceCount
            || !double.IsFinite(datasourcePercent)
            || datasourcePercent is < 0 or > 100)
        {
            throw new InvalidDataException("Corruption progress payload was outside its closed bounds");
        }

        return (datasourceIndex * 100.0 + datasourcePercent) / datasourceCount;
    }

    internal static StructuralScanMode? ResolveStructuralScanMode(
        CorruptionDetectionMethod detectionMethod,
        string? scanMode)
    {
        if (scanMode is null)
        {
            return NormalizeStructuralScanMode(detectionMethod, null);
        }

        if (!StructuralScanModeExtensions.TryParseWire(scanMode, out var parsed))
        {
            throw new ValidationException("Structural scan mode must be 'full' or 'incremental'");
        }

        return NormalizeStructuralScanMode(detectionMethod, parsed);
    }

    private static StructuralScanMode? NormalizeStructuralScanMode(
        CorruptionDetectionMethod detectionMethod,
        StructuralScanMode? scanMode)
    {
        if (detectionMethod == CorruptionDetectionMethod.RepeatedMiss)
        {
            if (scanMode.HasValue)
            {
                throw new ValidationException("Structural scan mode is only valid for structural detection");
            }

            return null;
        }

        if (detectionMethod == CorruptionDetectionMethod.Structural)
        {
            return scanMode ?? StructuralScanMode.Full;
        }

        throw new ValidationException("Unsupported corruption detection method");
    }

    internal static void ValidateScanInput(
        int threshold,
        int lookbackDays = DefaultLookbackDays,
        CorruptionDetectionMethod detectionMethod = CorruptionDetectionMethod.RepeatedMiss,
        StructuralScanMode? scanMode = null)
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

        if (!Enum.IsDefined(detectionMethod))
        {
            throw new ValidationException("Unsupported corruption detection method");
        }

        if (scanMode.HasValue && !Enum.IsDefined(scanMode.Value))
        {
            throw new ValidationException("Unsupported structural scan mode");
        }

        _ = NormalizeStructuralScanMode(detectionMethod, scanMode);
    }

    private static bool IsSupportedScan(CachedCorruptionScan scan) =>
        scan.ContractVersion == CorruptionReport.SupportedContractVersion
        && string.Equals(
            scan.Status,
            OperationStatus.Completed.ToWireString(),
            StringComparison.Ordinal)
        && scan.DetectionMode is CorruptionDetectionMode.RepeatedMiss or CorruptionDetectionMode.Structural
        && (scan.DetectionMode == CorruptionDetectionMode.Structural
            || scan.ScanMode is null);

    private static async Task<CachedCorruptionScan> RequireSupportedSnapshotAsync(
        AppDbContext dbContext,
        Guid scanId,
        CancellationToken cancellationToken)
    {
        var scan = await dbContext.CachedCorruptionScans
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ScanId == scanId, cancellationToken);
        if (scan == null || !IsSupportedScan(scan))
        {
            throw new NotFoundException("Corruption scan");
        }

        return scan;
    }

    private static async Task<CachedCorruptionScan> RequireCurrentScanAsync(
        AppDbContext dbContext,
        Guid scanId,
        CancellationToken cancellationToken)
    {
        var current = await dbContext.CachedCorruptionScans
            .SingleOrDefaultAsync(scan => scan.ScanId == scanId, cancellationToken);
        if (current == null)
        {
            throw new NotFoundException("Corruption scan");
        }

        if (!current.IsCurrent)
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

internal sealed record DatasourceCorruptionReport(
    string DatasourceName,
    CorruptionReport Report,
    StructuralScanStatusResponse? ScanSummary = null);

/// <summary>JSON model for Rust corruption detection progress.</summary>
public class CorruptionDetectionProgressData
{
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }

    public long FilesProcessed { get; set; }
    public long TotalFiles { get; set; }
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
    public CorruptionDetectionMethod DetectionMethod { get; set; }
    public StructuralScanMode? ScanMode { get; set; }
    public CorruptionScanSettings Settings { get; set; } = new();
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);
    public CorruptionScanCoverage? Coverage { get; set; }
    public DateTime LastDetectionTime { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
}

/// <summary>Read-only summary of one retained completed corruption scan.</summary>
public sealed class CorruptionScanHistorySummary
{
    public Guid ScanId { get; set; }
    public int ContractVersion { get; set; }
    public CorruptionDetectionMethod DetectionMethod { get; set; }
    public StructuralScanMode? ScanMode { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public CorruptionScanSettings Settings { get; set; } = new();
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);
    public CorruptionScanCoverage? Coverage { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class CachedCorruptionProjection
{
    [JsonRequired]
    public CorruptionScanSettings Settings { get; set; } = new();

    [JsonRequired]
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);

    public CorruptionScanCoverage? Coverage { get; set; }
}
