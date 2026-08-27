using System.Data;
using System.Globalization;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Finds cache files that no game or service detection row claims, and removes them on
/// request. Owns the single retained scan snapshot behind the Unmapped Cache section.
/// </summary>
public class UnmappedCacheService
{
    internal const string ScanOperationName = "Unmapped Cache Scan";
    internal const string RemovalOperationName = "Unmapped Cache Removal";
    private const string ScanStartingStageKey = "signalr.unmappedScan.starting";
    private const string ScanCompleteStageKey = "signalr.unmappedScan.complete";
    private const string RemoveStartingStageKey = "signalr.unmappedRemove.starting";
    private const string RemoveCompleteStageKey = "signalr.unmappedRemove.complete";
    private const string CacheBinaryName = "Corruption manager";
    private const int CacheFileDigestLength = 32;
    private const int ServiceNameMaxLength = 100;

    private static readonly JsonSerializerOptions _reportJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<UnmappedCacheService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public UnmappedCacheService(
        ILogger<UnmappedCacheService> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        ISignalRNotificationService notifications,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IUnifiedOperationTracker operationTracker,
        DatasourceCapabilityService capabilityService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _notifications = notifications;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;
        _operationTracker = operationTracker;
        _capabilityService = capabilityService;
    }

    /// <summary>Starts the background scan for cache files no detection row claims.</summary>
    public async Task<Guid> StartScanAsync(CancellationToken cancellationToken = default)
    {
        // Recovering a file's service needs one unambiguous key scheme, so refuse here as well as
        // at the controller gate: this is the check the queue replays at promotion time.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new ValidationException(capabilityDenial);
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            var activeOp = _operationTracker.GetActiveOperations(OperationType.UnmappedCacheScan).FirstOrDefault();
            if (activeOp != null)
            {
                _logger.LogWarning("[UnmappedCache] Scan already in progress: {OperationId}", activeOp.Id);
                return activeOp.Id;
            }

            var cts = new CancellationTokenSource();
            Guid operationId = Guid.Empty;
            operationId = _operationTracker.RegisterOperation(
                OperationType.UnmappedCacheScan,
                ScanOperationName,
                cts,
                onTerminalEmit: info => EmitTerminalAsync(
                    SignalREvents.UnmappedScanComplete,
                    ScanCompleteStageKey,
                    info,
                    operationId));
            _operationTracker.UpdateProgress(operationId, 0, ScanStartingStageKey);

            await _notifications.NotifyAllAsync(SignalREvents.UnmappedScanStarted, new
            {
                OperationId = operationId,
                StageKey = ScanStartingStageKey
            });

            var startedAt = CaptureScanStartedUtc();
            var token = cts.Token;
            _ = StartWorker(workerToken => RunScanAsync(operationId, startedAt, workerToken), token);
            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Starts the background removal of the stored unmapped files for the named services.</summary>
    public async Task<Guid> StartRemovalAsync(
        Guid scanId,
        IReadOnlyList<string> services,
        CancellationToken cancellationToken = default)
    {
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new ValidationException(capabilityDenial);
        }

        if (services.Count == 0)
        {
            throw new ValidationException("At least one service is required");
        }

        var pathGroups = await LoadStoredPathsAsync(scanId, services, cancellationToken);
        if (pathGroups.Count == 0)
        {
            throw new ValidationException("The stored scan has no unmapped files for the requested services");
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            var activeOp = _operationTracker.GetActiveOperations(OperationType.UnmappedCacheRemoval).FirstOrDefault();
            if (activeOp != null)
            {
                throw new ConflictException("An unmapped cache removal is already in progress");
            }

            var cts = new CancellationTokenSource();
            Guid operationId = Guid.Empty;
            operationId = _operationTracker.RegisterOperation(
                OperationType.UnmappedCacheRemoval,
                RemovalOperationName,
                cts,
                onTerminalEmit: info => EmitTerminalAsync(
                    SignalREvents.UnmappedRemovalComplete,
                    RemoveCompleteStageKey,
                    info,
                    operationId));
            _operationTracker.UpdateProgress(operationId, 0, RemoveStartingStageKey);

            await _notifications.NotifyAllAsync(SignalREvents.UnmappedRemovalStarted, new
            {
                OperationId = operationId,
                StageKey = RemoveStartingStageKey
            });

            var token = cts.Token;
            _ = StartWorker(
                workerToken => RunRemovalAsync(operationId, scanId, services, pathGroups, workerToken),
                token);
            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Reads the stored scan, or null when nothing usable is stored.</summary>
    public async Task<UnmappedCacheResponse?> GetScanAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await RequireSupportedScanAsync(dbContext, scanId: null, cancellationToken);
        if (scan == null)
        {
            return null;
        }

        // Projected column-by-column so the file lists stay out of the summary read.
        var groups = await dbContext.CachedUnmappedDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scan.ScanId)
            .Select(row => new { row.ServiceName, row.FileCount, row.TotalSizeBytes })
            .ToListAsync(cancellationToken);

        // One entry per service: two datasources that both hold a service's files store a row
        // each, and the section shows the service once.
        var services = groups
            .GroupBy(group => group.ServiceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new UnmappedServiceRow
            {
                Service = group.Key,
                FileCount = group.Sum(row => row.FileCount),
                TotalBytes = group.Sum(row => row.TotalSizeBytes)
            })
            .OrderBy(row => row.Service, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new UnmappedCacheResponse
        {
            HasCachedResults = true,
            ContractVersion = scan.ContractVersion,
            ScanId = scan.ScanId,
            LastScanTime = scan.CompletedAtUtc.AsUtc().ToString("o"),
            TotalFiles = services.Sum(row => row.FileCount),
            TotalBytes = services.Sum(row => row.TotalBytes),
            Services = services
        };
    }

    /// <summary>Reads one service's stored unmapped files for the expandable list.</summary>
    public async Task<IReadOnlyList<UnmappedFileResponse>> GetServiceFilesAsync(
        Guid scanId,
        string service,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await RequireSupportedScanAsync(dbContext, scanId, cancellationToken);
        if (scan == null)
        {
            return [];
        }

        var rows = await dbContext.CachedUnmappedDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scan.ScanId && row.ServiceName == service)
            .Select(row => row.FilesJson)
            .ToListAsync(cancellationToken);
        return rows
            .SelectMany(DeserializeFiles)
            .Select(file => new UnmappedFileResponse
            {
                Id = file.Digest,
                Path = file.Path,
                Url = file.Url,
                SizeBytes = file.SizeBytes
            })
            .ToList();
    }

    // Do not pass the operation token to Task.Run itself. If cancellation wins during the
    // asynchronous start notification, Task.Run(token) suppresses the delegate entirely and the
    // registered operation never reaches its cancellation cleanup. The worker receives the same
    // token and owns the terminal transition.
    internal static Task StartWorker(
        Func<CancellationToken, Task> worker,
        CancellationToken cancellationToken) =>
        Task.Run(() => worker(cancellationToken));

    internal static IReadOnlyList<string> BuildScanArguments(
        string cacheDir,
        string claimedDigestsFile,
        string progressFile,
        string scanStartedUtc,
        string keyScheme) =>
        [
            "--key-scheme",
            keyScheme,
            "unmapped-scan",
            cacheDir,
            claimedDigestsFile,
            progressFile,
            "--scan-started-utc",
            scanStartedUtc
        ];

    internal static IReadOnlyList<string> BuildRemovalArguments(
        string cacheDir,
        string claimedDigestsFile,
        string progressFile,
        string evidenceFile,
        string keyScheme) =>
        [
            "--key-scheme",
            keyScheme,
            "unmapped-remove",
            cacheDir,
            claimedDigestsFile,
            progressFile,
            "--evidence-file",
            evidenceFile
        ];

    /// <summary>Whole-second UTC, so the anchor the scanner echoes back is byte-comparable.</summary>
    internal static DateTimeOffset CaptureScanStartedUtc()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    internal static string FormatScanStartedUtc(DateTimeOffset startedAt) =>
        startedAt.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

    internal static bool IsCacheFileDigest(string? value)
    {
        if (value is not { Length: CacheFileDigestLength })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The stored strings come from the scanner's path formatting, so their separator follows
    /// whichever host ran detection. Split on both so a database written on Windows still yields
    /// digests instead of silently reporting every claimed file as unmapped.
    /// </summary>
    internal static string? DigestFromStoredPath(string? storedPath)
    {
        if (storedPath == null)
        {
            return null;
        }

        var name = storedPath[(storedPath.LastIndexOfAny(['/', '\\']) + 1)..];
        return IsCacheFileDigest(name) ? name : null;
    }

    internal static string CachePathForDigest(string cacheRoot, string digest) =>
        $"{cacheRoot}/{digest[30..32]}/{digest[28..30]}/{digest}";

    /// <summary>
    /// Rejects a scan report outright rather than repairing it. Anything less and a stale or
    /// forged report is persisted as this scan's evidence and then deleted from.
    /// </summary>
    internal static void ValidateReport(
        UnmappedScanReport report,
        string expectedScanStartedUtc,
        string expectedCacheRoot)
    {
        if (report.ContractVersion != UnmappedScanReport.SupportedContractVersion)
        {
            throw new InvalidDataException(
                $"Unsupported unmapped scan report contract version {report.ContractVersion}");
        }

        if (report.Cancelled)
        {
            throw new InvalidDataException(
                "A cancelled unmapped scan report cannot be validated as a completed scan");
        }

        // Compared as raw strings, not parsed timestamps: a report echoing a different anchor
        // belongs to another run, and parsing would let two spellings of one instant through. [7]
        if (!string.Equals(report.ScanStartedUtc, expectedScanStartedUtc, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The unmapped scan report start did not match the requested scan");
        }

        var cacheRoot = expectedCacheRoot.TrimEnd('/');
        if (!string.Equals(report.CacheRoot.TrimEnd('/'), cacheRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The unmapped scan report cache root did not match the scanned datasource");
        }

        var digests = new HashSet<string>(StringComparer.Ordinal);
        var orphanCount = 0L;
        var orphanBytes = 0L;
        foreach (var group in report.Services)
        {
            if (string.IsNullOrWhiteSpace(group.Service) || group.Service.Length > ServiceNameMaxLength)
            {
                throw new InvalidDataException("The unmapped scan report contained an unusable service name");
            }

            if (group.FileCount != group.Files.Count
                || group.TotalBytes != group.Files.Sum(file => file.SizeBytes))
            {
                throw new InvalidDataException(
                    $"Unmapped scan totals for service '{group.Service}' did not match its files");
            }

            orphanCount += group.FileCount;
            orphanBytes += group.TotalBytes;
            foreach (var file in group.Files)
            {
                if (!IsCacheFileDigest(file.Digest))
                {
                    throw new InvalidDataException(
                        "The unmapped scan report contained a malformed cache file digest");
                }

                // The cache layout is fixed, so rebuilding the path from the digest proves both
                // that the file sits under the scanned root and that its shape is a cache file.
                if (!string.Equals(file.Path, CachePathForDigest(cacheRoot, file.Digest), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Unmapped cache file '{file.Digest}' was not at its cache path under the scanned root");
                }

                // Identity is the digest, so a repeat would delete one file twice and count it twice.
                if (!digests.Add(file.Digest))
                {
                    throw new InvalidDataException($"Duplicate unmapped cache file digest '{file.Digest}'");
                }
            }
        }

        if (report.OrphanCount != orphanCount || report.OrphanBytes != orphanBytes)
        {
            throw new InvalidDataException(
                "The unmapped scan report totals did not match its per-service groups");
        }
    }

    /// <summary>
    /// Streams both detection tables into newline-delimited digests. Neither table is
    /// materialized: a single service row can carry hundreds of thousands of paths, which is
    /// why the ordinary loaders project that column out at the SQL level.
    /// </summary>
    internal static async Task<ClaimedDigestCounts> WriteClaimedDigestsAsync(
        AppDbContext dbContext,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var games = await WriteDigestLinesAsync(
            writer,
            dbContext.CachedGameDetections
                .AsNoTracking()
                .OrderBy(game => game.Id)
                .Select(game => game.CacheFilePathsJson)
                .AsAsyncEnumerable(),
            cancellationToken);
        var services = await WriteDigestLinesAsync(
            writer,
            dbContext.CachedServiceDetections
                .AsNoTracking()
                .OrderBy(service => service.Id)
                .Select(service => service.CacheFilePathsJson)
                .AsAsyncEnumerable(),
            cancellationToken);
        return new ClaimedDigestCounts(
            games.Written + services.Written,
            games.Skipped + services.Skipped);
    }

    private async Task<long> BuildClaimedDigestFileAsync(string claimedFilePath, CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var writer = new StreamWriter(claimedFilePath, append: false);
        var counts = await WriteClaimedDigestsAsync(dbContext, writer, cancellationToken);
        if (counts.Skipped > 0)
        {
            _logger.LogWarning(
                "[UnmappedCache] {Skipped} stored path(s) did not end in a cache file name and were not counted as claimed",
                counts.Skipped);
        }

        return counts.Written;
    }

    internal async Task PersistCompletedScanAsync(
        Guid scanId,
        DateTime completedAtUtc,
        IReadOnlyList<DatasourceUnmappedReport> reports,
        CancellationToken cancellationToken)
    {
        await using var strategyContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            // One scan is retained. Replacing it wholesale keeps the stored totals and the stored
            // file lists from describing two different walks of the same cache.
            await dbContext.CachedUnmappedDetections.ExecuteDeleteAsync(cancellationToken);
            await dbContext.CachedUnmappedScans.ExecuteDeleteAsync(cancellationToken);

            // Written even with zero findings so an empty result survives a restart.
            dbContext.CachedUnmappedScans.Add(new CachedUnmappedScan
            {
                ScanId = scanId,
                ContractVersion = UnmappedScanReport.SupportedContractVersion,
                CompletedAtUtc = completedAtUtc
            });

            foreach (var datasourceReport in reports)
            {
                foreach (var group in datasourceReport.Report.Services)
                {
                    dbContext.CachedUnmappedDetections.Add(new CachedUnmappedDetection
                    {
                        ScanId = scanId,
                        ServiceName = group.Service,
                        DatasourceName = datasourceReport.DatasourceName,
                        FileCount = group.FileCount,
                        TotalSizeBytes = group.TotalBytes,
                        FilesJson = JsonSerializer.Serialize(group.Files, _reportJsonOptions)
                    });
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static async Task<ClaimedDigestCounts> WriteDigestLinesAsync(
        TextWriter writer,
        IAsyncEnumerable<string> pathsJsonRows,
        CancellationToken cancellationToken)
    {
        var written = 0L;
        var skipped = 0L;
        await foreach (var pathsJson in pathsJsonRows.WithCancellation(cancellationToken))
        {
            using var document = JsonDocument.Parse(pathsJson);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var digest = DigestFromStoredPath(element.GetString());
                if (digest == null)
                {
                    skipped++;
                    continue;
                }

                await writer.WriteLineAsync(digest.AsMemory(), cancellationToken);
                written++;
            }
        }

        return new ClaimedDigestCounts(written, skipped);
    }

    private static IEnumerable<UnmappedCacheFile> DeserializeFiles(string filesJson) =>
        JsonSerializer.Deserialize<List<UnmappedCacheFile>>(filesJson, _reportJsonOptions)
        ?? throw new InvalidDataException("A stored unmapped cache file list was null");

    /// <summary>
    /// Returns the stored scan only when its stored contract version is the one this build
    /// writes. A scan on any other version is ignored rather than migrated. [8]
    /// </summary>
    internal static async Task<CachedUnmappedScan?> RequireSupportedScanAsync(
        AppDbContext dbContext,
        Guid? scanId,
        CancellationToken cancellationToken)
    {
        var scan = await dbContext.CachedUnmappedScans
            .AsNoTracking()
            .OrderByDescending(item => item.CompletedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (scan == null || scan.ContractVersion != UnmappedScanReport.SupportedContractVersion)
        {
            return null;
        }

        return scanId.HasValue && scanId.Value != scan.ScanId ? null : scan;
    }

    private async Task RunScanAsync(Guid operationId, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var claimedFile = Path.Combine(
            _pathResolver.GetOperationsDirectory(),
            $"unmapped_claimed_{operationId}.txt");
        try
        {
            Directory.CreateDirectory(_pathResolver.GetOperationsDirectory());
            var datasources = _datasourceService.GetDatasources();
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();
            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, CacheBinaryName);
            var scanStartedUtc = FormatScanStartedUtc(startedAt);

            var claimedCount = await BuildClaimedDigestFileAsync(claimedFile, cancellationToken);
            _logger.LogInformation(
                "[UnmappedCache] Streamed {Claimed} claimed digest(s) for {Count} datasource(s)",
                claimedCount,
                datasources.Count);

            var reports = new List<DatasourceUnmappedReport>();
            for (var index = 0; index < datasources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var datasource = datasources[index];
                var report = await ScanDatasourceAsync(
                    operationId,
                    datasource,
                    rustBinaryPath,
                    claimedFile,
                    scanStartedUtc,
                    index,
                    cancellationToken);
                reports.Add(new DatasourceUnmappedReport(datasource.Name, report));
            }

            await PersistCompletedScanAsync(Guid.NewGuid(), DateTime.UtcNow, reports, cancellationToken);
            _operationTracker.UpdateProgress(operationId, 100, ScanCompleteStageKey);
            _operationTracker.CompleteOperation(operationId, success: true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[UnmappedCache] Scan {OperationId} was cancelled", operationId);
            _operationTracker.CompleteOperation(operationId, success: false, cancelled: true);
        }
        catch (Exception ex)
        {
            LogRustFailure(ex, operationId);
            _logger.LogError(ex, "[UnmappedCache] Scan failed for operation {OperationId}", operationId);
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(claimedFile);
        }
    }

    private async Task<UnmappedScanReport> ScanDatasourceAsync(
        Guid operationId,
        ResolvedDatasource datasource,
        string rustBinaryPath,
        string claimedFile,
        string scanStartedUtc,
        int datasourceIndex,
        CancellationToken cancellationToken)
    {
        var progressFile = Path.Combine(
            _pathResolver.GetOperationsDirectory(),
            $"unmapped_scan_{operationId}_{datasourceIndex}.json");
        try
        {
            var rustCancellationReported = 0;

            // The awaited start notification gives cache-layout evidence a chance to change, so the
            // guarded wire value is resolved only now, immediately before launch.
            var keyScheme = _capabilityService.GetKeySchemeWireValue(datasource);
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                BuildScanArguments(datasource.CachePath, claimedFile, progressFile, scanStartedUtc, keyScheme));

            var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<CorruptionDetectionProgressData>(
                startInfo,
                operationId,
                cancellationToken,
                progressFile,
                async progressData =>
                {
                    if (string.Equals(progressData.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Exchange(ref rustCancellationReported, 1);
                    }

                    await ReportProgressAsync(
                        operationId,
                        SignalREvents.UnmappedScanProgress,
                        ScanStartingStageKey,
                        progressData);
                },
                "unmapped_scan");

            result.EnsureSuccess("unmapped_scan", datasource.Name);
            if (Volatile.Read(ref rustCancellationReported) != 0)
            {
                // Classified from the progress checkpoint even when the stdout report is missing,
                // so a cancelled walk can never be validated and persisted as a completed scan.
                _operationTracker.CancelOperation(operationId);
                throw new OperationCanceledException("The unmapped scan reported cancellation", cancellationToken);
            }

            var report = JsonSerializer.Deserialize<UnmappedScanReport>(result.Output, _reportJsonOptions)
                ?? throw new InvalidDataException(
                    $"The unmapped scan returned an empty report for datasource '{datasource.Name}'");
            ValidateReport(report, scanStartedUtc, datasource.CachePath);
            _logger.LogInformation(
                "[UnmappedCache] {Datasource}: {Orphans} unmapped file(s) of {OnDisk} on disk, "
                + "{Claimed} claimed, {Skipped} unreadable name(s), {Unreadable} unreadable key(s)",
                datasource.Name,
                report.OrphanCount,
                report.FilesOnDisk,
                report.ClaimedDigests,
                report.SkippedNonHashNames,
                report.UnreadableKeys);
            return report;
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(progressFile);
        }
    }

    private async Task RunRemovalAsync(
        Guid operationId,
        Guid scanId,
        IReadOnlyList<string> services,
        IReadOnlyList<DatasourceUnmappedPaths> pathGroups,
        CancellationToken cancellationToken)
    {
        var claimedFile = Path.Combine(
            _pathResolver.GetOperationsDirectory(),
            $"unmapped_claimed_{operationId}.txt");
        try
        {
            Directory.CreateDirectory(_pathResolver.GetOperationsDirectory());
            var datasources = _datasourceService.GetDatasources();
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();
            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, CacheBinaryName);

            // Regenerated here rather than reused from the scan. This file IS the still-unclaimed
            // re-check: a file a detection run attributed since the scan is now claimed and the
            // remover refuses it.
            await BuildClaimedDigestFileAsync(claimedFile, cancellationToken);

            var deletedFiles = 0L;
            var bytesFreed = 0L;
            var refused = 0L;
            for (var index = 0; index < pathGroups.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = pathGroups[index];
                var datasource = datasources.FirstOrDefault(
                        item => string.Equals(item.Name, group.DatasourceName, StringComparison.Ordinal))
                    ?? throw new ValidationException(
                        $"Datasource '{group.DatasourceName}' from the stored scan is no longer configured");
                var report = await RemoveFromDatasourceAsync(
                    operationId,
                    datasource,
                    group.Paths,
                    rustBinaryPath,
                    claimedFile,
                    index,
                    cancellationToken);
                deletedFiles += report.DeletedFiles;
                bytesFreed += report.BytesFreed;
                refused += report.ClaimedSinceScan;
            }

            await DeleteStoredServicesAsync(scanId, services, cancellationToken);
            _operationTracker.UpdateProgress(operationId, 100, RemoveCompleteStageKey);
            _operationTracker.CompleteOperation(operationId, success: true);
            _logger.LogInformation(
                "[UnmappedCache] Removed {Deleted} file(s) freeing {Bytes} byte(s); "
                + "{Refused} refused as claimed since the scan",
                deletedFiles,
                bytesFreed,
                refused);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[UnmappedCache] Removal {OperationId} was cancelled", operationId);
            _operationTracker.CompleteOperation(operationId, success: false, cancelled: true);
        }
        catch (Exception ex)
        {
            LogRustFailure(ex, operationId);
            _logger.LogError(ex, "[UnmappedCache] Removal failed for operation {OperationId}", operationId);
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(claimedFile);
        }
    }

    private async Task<UnmappedRemovalReport> RemoveFromDatasourceAsync(
        Guid operationId,
        ResolvedDatasource datasource,
        IReadOnlyList<string> paths,
        string rustBinaryPath,
        string claimedFile,
        int datasourceIndex,
        CancellationToken cancellationToken)
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        var progressFile = Path.Combine(operationsDir, $"unmapped_remove_{operationId}_{datasourceIndex}.json");
        var evidenceFile = Path.Combine(operationsDir, $"unmapped_evidence_{operationId}_{datasourceIndex}.json");
        try
        {
            await File.WriteAllTextAsync(
                evidenceFile,
                JsonSerializer.Serialize(
                    new UnmappedRemovalRequest
                    {
                        ContractVersion = UnmappedScanReport.SupportedContractVersion,
                        Paths = [.. paths]
                    },
                    _reportJsonOptions),
                cancellationToken);

            var keyScheme = _capabilityService.GetKeySchemeWireValue(datasource);
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                BuildRemovalArguments(datasource.CachePath, claimedFile, progressFile, evidenceFile, keyScheme));

            var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<CorruptionDetectionProgressData>(
                startInfo,
                operationId,
                cancellationToken,
                progressFile,
                progressData => ReportProgressAsync(
                    operationId,
                    SignalREvents.UnmappedRemovalProgress,
                    RemoveStartingStageKey,
                    progressData),
                "unmapped_remove");

            result.EnsureSuccess("unmapped_remove", datasource.Name);
            var report = JsonSerializer.Deserialize<UnmappedRemovalReport>(result.Output, _reportJsonOptions)
                ?? throw new InvalidDataException(
                    $"The unmapped removal returned an empty report for datasource '{datasource.Name}'");
            if (report.ContractVersion != UnmappedScanReport.SupportedContractVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported unmapped removal report contract version {report.ContractVersion}");
            }

            if (report.Cancelled)
            {
                _operationTracker.CancelOperation(operationId);
                throw new OperationCanceledException(
                    "The unmapped removal reported cancellation",
                    cancellationToken);
            }

            return report;
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(progressFile);
            await _rustProcessHelper.DeleteTempFileAsync(evidenceFile);
        }
    }

    private async Task<IReadOnlyList<DatasourceUnmappedPaths>> LoadStoredPathsAsync(
        Guid scanId,
        IReadOnlyList<string> services,
        CancellationToken cancellationToken)
    {
        var serviceNames = services.ToList();
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scan = await RequireSupportedScanAsync(dbContext, scanId, cancellationToken);
        if (scan == null)
        {
            throw new ValidationException("The requested unmapped scan is no longer the stored scan");
        }

        var rows = await dbContext.CachedUnmappedDetections
            .AsNoTracking()
            .Where(row => row.ScanId == scan.ScanId && serviceNames.Contains(row.ServiceName))
            .Select(row => new { row.DatasourceName, row.FilesJson })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.DatasourceName, StringComparer.Ordinal)
            .Select(group => new DatasourceUnmappedPaths(
                group.Key,
                group.SelectMany(row => DeserializeFiles(row.FilesJson)).Select(file => file.Path).ToList()))
            .ToList();
    }

    private async Task DeleteStoredServicesAsync(
        Guid scanId,
        IReadOnlyList<string> services,
        CancellationToken cancellationToken)
    {
        var serviceNames = services.ToList();
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.CachedUnmappedDetections
            .Where(row => row.ScanId == scanId && serviceNames.Contains(row.ServiceName))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task ReportProgressAsync(
        Guid operationId,
        string eventName,
        string fallbackStageKey,
        CorruptionDetectionProgressData progressData)
    {
        var stageKey = string.IsNullOrWhiteSpace(progressData.StageKey)
            ? fallbackStageKey
            : progressData.StageKey;
        _operationTracker.UpdateProgress(operationId, progressData.PercentComplete, stageKey);
        await _notifications.NotifyAllAsync(eventName, new
        {
            OperationId = operationId,
            StageKey = stageKey,
            progressData.Context,
            progressData.PercentComplete,
            Status = OperationStatus.Running
        });
    }

    private Task EmitTerminalAsync(
        string eventName,
        string completeStageKey,
        OperationTerminalInfo info,
        Guid operationId) =>
        _notifications.NotifyAllAsync(eventName, new
        {
            OperationId = operationId,
            StageKey = info.Success ? completeStageKey : null,
            info.Success,
            info.Cancelled,
            info.Error,
            Status = info.Cancelled
                ? OperationStatus.Cancelled
                : info.Success
                    ? OperationStatus.Completed
                    : OperationStatus.Failed,
            PercentComplete = 100
        });

    private void LogRustFailure(Exception exception, Guid operationId)
    {
        // RustProcessException keeps stderr out of Message so it never reaches the client, which
        // also means it never reaches the log unless it is written here.
        if (exception is RustProcessException rustFailure && !string.IsNullOrWhiteSpace(rustFailure.Stderr))
        {
            _logger.LogError(
                "[UnmappedCache] {Tool} exited {ExitCode} for operation {OperationId}: {Stderr}",
                rustFailure.Tool,
                rustFailure.ExitCode,
                operationId,
                rustFailure.Stderr!.Trim());
        }
    }
}

internal readonly record struct ClaimedDigestCounts(long Written, long Skipped);

internal sealed record DatasourceUnmappedReport(string DatasourceName, UnmappedScanReport Report);

internal sealed record DatasourceUnmappedPaths(string DatasourceName, IReadOnlyList<string> Paths);
