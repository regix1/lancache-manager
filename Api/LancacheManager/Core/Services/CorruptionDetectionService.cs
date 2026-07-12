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
    internal const ulong StructuralMinimumStableAgeSeconds = 600;
    internal const ulong StructuralMaximumPrefixBytes = 65_535;

    private const string CanonicalUtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const string ProjectionServiceName = "__scan_projection__";
    private const string ProjectionDatasourceName = "__aggregate__";
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
        CorruptionDetectionMethod detectionMethod = CorruptionDetectionMethod.RepeatedMiss,
        CancellationToken cancellationToken = default)
    {
        ValidateScanInput(threshold, lookbackDays, detectionMethod);

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
                LookbackDays = lookbackDays,
                DetectionMethod = detectionMethod
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
                StageKey = "signalr.corruptionDetect.starting",
                DetectionMethod = detectionMethod.ToWireString(),
                Context = new Dictionary<string, object?>
                {
                    ["detectionMethod"] = detectionMethod.ToWireString()
                }
            });

            var startedAtUtc = CaptureScanStartedUtc();
            var token = cts.Token;
            _ = Task.Run(
                () => RunDetectionAsync(
                    operationId,
                    threshold,
                    lookbackDays,
                    detectionMethod,
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
        var detectionMethod = metadata.DetectionMethod.ToWireString();

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
                    Context: new Dictionary<string, object?> { ["detectionMethod"] = detectionMethod }));
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
                    Context: new Dictionary<string, object?>
                    {
                        ["count"] = totalServicesWithCorruption,
                        ["scanId"] = metadata.ScanId,
                        ["detectionMethod"] = detectionMethod
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
                DetectionMethod: detectionMethod,
                DetectionCounts: metadata.DetectionCounts,
                Coverage: CorruptionScanCoverageResponse.From(metadata.Coverage),
                Context: new Dictionary<string, object?>
                {
                    ["errorDetail"] = info.Error,
                    ["detectionMethod"] = detectionMethod
                }));
    }

    private async Task RunDetectionAsync(
        Guid operationId,
        int threshold,
        int lookbackDays,
        CorruptionDetectionMethod detectionMethod,
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
                var report = await GetReportForDatasourceAsync(
                    datasource.LogPath,
                    datasource.CachePath,
                    timezone,
                    rustBinaryPath,
                    operationId,
                    datasource.Name,
                    threshold,
                    lookbackDays,
                    detectionMethod,
                    scanStartedUtc,
                    datasourceIndex,
                    datasources.Count,
                    cancellationToken);
                datasourceReports.Add(new DatasourceCorruptionReport(datasource.Name, report));
            }

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
                metrics.CorruptionCounts = counts;
                metrics.DetectionCounts = detectionCounts;
                metrics.Coverage = coverage;
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
        CorruptionDetectionMethod detectionMethod,
        string scanStartedUtc,
        int datasourceIndex,
        int datasourceCount,
        CancellationToken cancellationToken)
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var progressFile = Path.Combine(operationsDir, $"corruption_detection_{operationId}_{datasourceName}.json");

        try
        {
            var arguments = detectionMethod switch
            {
                CorruptionDetectionMethod.RepeatedMiss =>
                    $"summary \"{logDir}\" \"{cacheDir}\" \"{progressFile}\" \"{timezone}\" {threshold} --lookback-days {lookbackDays} --scan-started-utc \"{scanStartedUtc}\"",
                CorruptionDetectionMethod.Structural =>
                    $"structural-summary \"{cacheDir}\" \"{progressFile}\" --scan-started-utc \"{scanStartedUtc}\" --progress",
                _ => throw new ValidationException("Unsupported corruption detection method")
            };
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, arguments);

            var lastMessage = string.Empty;
            var lastPercent = 0.0;
            var lastOverallPercent = datasourceIndex * 100.0 / datasourceCount;
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
                    var overallPercent = Math.Max(
                        lastOverallPercent,
                        CalculateOverallProgress(
                            datasourceIndex,
                            datasourceCount,
                            progressData.PercentComplete));
                    lastOverallPercent = overallPercent;
                    _operationTracker.UpdateProgress(
                        operationId,
                        overallPercent,
                        progressData.StageKey ?? "signalr.corruptionDetect.scanning");
                    var context = progressData.Context ?? new Dictionary<string, object?>();
                    context["detectionMethod"] = detectionMethod.ToWireString();
                    context["datasourceName"] = datasourceName;
                    await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionProgress, new
                    {
                        OperationId = operationId,
                        PercentComplete = overallPercent,
                        Status = OperationStatus.Running,
                        StageKey = progressData.StageKey,
                        DetectionMethod = detectionMethod.ToWireString(),
                        Context = context,
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
                detectionMethod,
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
        CorruptionDetectionMethod expectedDetectionMethod,
        string expectedScanStartedUtc)
    {
        if (report.ContractVersion != CorruptionReport.SupportedContractVersion)
        {
            throw new InvalidDataException(
                $"Unsupported corruption report contract version {report.ContractVersion}");
        }

        if (report.DetectionMethod != expectedDetectionMethod)
        {
            throw new InvalidDataException("Corruption report method did not match the requested scan");
        }

        if (report.Candidates is null
            || report.ServiceCounts is null
            || report.DetectionCounts is null
            || report.Settings is null)
        {
            throw new InvalidDataException("Corruption report omitted required v4 fields");
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
            ValidateCoverage(report.Coverage
                ?? throw new InvalidDataException("Structural report omitted scan coverage"),
                report.Candidates.Count);
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
        CancellationToken cancellationToken = default)
    {
        ValidateScanInput(threshold, lookbackDays, detectionMethod);
        if (startedAtUtc.Kind != DateTimeKind.Utc
            || startedAtUtc.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new InvalidDataException(
                "Corruption scan start must be a whole-second UTC timestamp");
        }

        ValidateCrossDatasourceIdentity(datasourceReports);

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

                dbContext.CachedCorruptionScans.Add(new CachedCorruptionScan
                {
                    ScanId = scanId,
                    DetectionMode = detectionMethod.ToPersistenceMode(),
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
        var projectionRow = rows.SingleOrDefault(row => row.ServiceName == ProjectionServiceName);
        if (projectionRow == null)
        {
            throw new InvalidDataException("Stored v4 corruption scan omitted its projection row");
        }

        var projection = JsonSerializer.Deserialize<CachedCorruptionProjection>(
                projectionRow.CandidatesJson,
                _candidateJsonOptions)
            ?? throw new InvalidDataException("Stored v4 corruption scan projection was null");
        var candidates = rows
            .Where(row => row.ServiceName != ProjectionServiceName)
            .SelectMany(DeserializeCandidates)
            .ToList();
        ValidateStoredCandidates(scan, candidates);
        return BuildCachedResult(scan, candidates, projection);
    }

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
            Settings = projection.Settings,
            CorruptionCounts = counts,
            DetectionCounts = detectionCounts,
            Coverage = projection.Coverage,
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
            var offsetIssue = evidence.Issues.Contains(StructuralCorruptionIssue.InvalidPayloadOffset)
                || evidence.Issues.Contains(StructuralCorruptionIssue.TruncatedBeforePayload);
            if (!offsetIssue)
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

    private static void ValidateCoverage(CorruptionScanCoverage coverage, int? candidateCount = null)
    {
        if (coverage.FilesSeen < 0
            || coverage.FilesChecked < 0
            || coverage.FilesChecked > coverage.FilesSeen
            || coverage.Consistent < 0
            || coverage.BytesRead < 0
            || coverage.SparseFiles < 0
            || coverage.SparseFiles > coverage.FilesSeen
            || coverage.IoErrors < 0
            || coverage.SkippedByReason is null
            || coverage.SkippedByReason.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0))
        {
            throw new InvalidDataException("Structural scan coverage was malformed");
        }


        if (candidateCount.HasValue
            && coverage.FilesChecked != checked(coverage.Consistent + candidateCount.Value))
        {
            throw new InvalidDataException(
                "Structural scan coverage did not match its actionable candidate count");
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

    internal static void ValidateScanInput(
        int threshold,
        int lookbackDays = DefaultLookbackDays,
        CorruptionDetectionMethod detectionMethod = CorruptionDetectionMethod.RepeatedMiss)
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
    }

    private static bool IsSupportedScan(CachedCorruptionScan scan) =>
        scan.ContractVersion == CorruptionReport.SupportedContractVersion
        && scan.DetectionMode is CorruptionDetectionMode.RepeatedMiss or CorruptionDetectionMode.Structural;

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
    public CorruptionDetectionMethod DetectionMethod { get; set; }
    public CorruptionScanSettings Settings { get; set; } = new();
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);
    public CorruptionScanCoverage? Coverage { get; set; }
    public DateTime LastDetectionTime { get; set; }
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
