using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Tests;

public sealed class CacheRepairNativeTests
{
    private const string ConnectionVariable = "LANCACHE_NATIVE_TEST_CONNECTION";
    private const string DatabaseUrlVariable = "LANCACHE_TEST_DATABASE_URL";
    private const string BinaryDirectoryVariable = "LANCACHE_NATIVE_TEST_BIN";

    [NativeRun]
    public async Task RestartAfterNativeClearRepairsCacheBeforeSingleTerminalAsync()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var databaseUrl = Environment.GetEnvironmentVariable(DatabaseUrlVariable);
        var binaryDirectory = Environment.GetEnvironmentVariable(BinaryDirectoryVariable);
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException($"{ConnectionVariable} must be set.");
        }
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            throw new InvalidOperationException($"{DatabaseUrlVariable} must be set.");
        }
        if (string.IsNullOrWhiteSpace(binaryDirectory))
        {
            throw new InvalidOperationException($"{BinaryDirectoryVariable} must be set.");
        }
        Assert.True(OperatingSystem.IsLinux(), "The native repair fixture requires Linux.");

        var connectionSettings = new NpgsqlConnectionStringBuilder(connection);
        Assert.Equal("127.0.0.1", connectionSettings.Host);
        Assert.Equal(5432, connectionSettings.Port);
        Assert.Matches(
            new Regex("^lcm_gate_[a-z0-9_]+_native$", RegexOptions.IgnoreCase),
            connectionSettings.Database);

        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var databaseUri))
        {
            throw new InvalidOperationException($"{DatabaseUrlVariable} must be an absolute URI.");
        }
        Assert.Contains(databaseUri.Scheme, new[] { "postgres", "postgresql" });
        Assert.Equal("127.0.0.1", databaseUri.Host);
        Assert.Equal(5432, databaseUri.Port);
        var uriDatabase = Uri.UnescapeDataString(databaseUri.AbsolutePath.TrimStart('/'));
        Assert.DoesNotContain('/', uriDatabase);
        Assert.Equal(connectionSettings.Database, uriDatabase);

        Assert.True(Path.IsPathFullyQualified(binaryDirectory));
        Assert.True(Directory.Exists(binaryDirectory));
        var clearBinary = Path.Combine(binaryDirectory, "cache_clear");
        var scanBinary = Path.Combine(binaryDirectory, "cache_eviction_scan");
        Assert.True(File.Exists(clearBinary));
        Assert.True(File.Exists(scanBinary));

        await using (var connectionCheck = new NpgsqlConnection(connectionSettings.ConnectionString))
        {
            await connectionCheck.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT current_database(), host(inet_server_addr()), inet_server_port()",
                connectionCheck);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(connectionSettings.Database, reader.GetString(0));
            Assert.Equal("127.0.0.1", reader.GetString(1));
            Assert.Equal(5432, reader.GetInt32(2));
        }

        var contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionSettings.ConnectionString)
            .Options;
        await using (var emptyCheck = new AppDbContext(contextOptions))
        {
            Assert.False(await emptyCheck.Downloads.AsNoTracking().AnyAsync());
            Assert.False(await emptyCheck.LogEntries.AsNoTracking().AnyAsync());
            Assert.False(await emptyCheck.CachedGameDetections.AsNoTracking().AnyAsync());
            Assert.False(await emptyCheck.CachedDetectionSummaries.AsNoTracking().AnyAsync());
            Assert.False(await emptyCheck.EvictionScanCheckpoints.AsNoTracking().AnyAsync());
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "lcm-native-repair-" + Guid.NewGuid().ToString("N"));
        var cacheRoot = Path.Combine(root, "cache");
        var logRoot = Path.Combine(root, "logs");
        var operationsRoot = Path.Combine(root, "operations");
        var digest = "0123456789abcdef0123456789abcdef";
        var cacheFile = Path.Combine(cacheRoot, "ef", "cd", digest);
        var progressPath = Path.Combine(operationsRoot, "cache-clear.json");
        var operationId = Guid.NewGuid();
        var receiptPath = Path.Combine(cacheRoot, $".lancache-repair-{operationId:N}.json");
        const long gameAppId = 570;
        const string datasourceName = "native";
        const string url = "/depot/570/chunk/native-repair";
        const long cacheBytes = 4;
        long selectedDownloadId = 0;
        long foreignDownloadId = 0;
        IHost? firstHost = null;
        IHost? secondHost = null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            Directory.CreateDirectory(logRoot);
            Directory.CreateDirectory(operationsRoot);
            await File.WriteAllBytesAsync(cacheFile, [1, 2, 3, 4]);

            await using (var seed = new AppDbContext(contextOptions))
            {
                var selected = new Download
                {
                    Service = "steam",
                    ClientIp = "127.0.0.2",
                    StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
                    EndTimeUtc = DateTime.UtcNow,
                    CacheHitBytes = cacheBytes,
                    IsActive = false,
                    IsEvicted = false,
                    GameAppId = gameAppId,
                    GameName = "Native Repair",
                    LastUrl = url,
                    Datasource = datasourceName.ToUpperInvariant()
                };
                var foreign = new Download
                {
                    Service = "steam",
                    ClientIp = "127.0.0.3",
                    StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
                    EndTimeUtc = DateTime.UtcNow,
                    CacheHitBytes = cacheBytes,
                    IsActive = false,
                    IsEvicted = false,
                    GameAppId = 730,
                    GameName = "Foreign Control",
                    LastUrl = "/depot/730/chunk/foreign-control",
                    Datasource = "foreign"
                };
                seed.Downloads.AddRange(selected, foreign);
                await seed.SaveChangesAsync();
                selectedDownloadId = selected.Id;
                foreignDownloadId = foreign.Id;
                seed.LogEntries.Add(new LogEntryRecord
                {
                    Timestamp = DateTime.UtcNow,
                    ClientIp = selected.ClientIp,
                    Service = selected.Service,
                    Method = "GET",
                    Url = url,
                    StatusCode = 200,
                    BytesServed = cacheBytes,
                    CacheStatus = "HIT",
                    Datasource = datasourceName.ToUpperInvariant(),
                    DownloadId = selected.Id,
                    CreatedAt = DateTime.UtcNow
                });
                seed.CachedGameDetections.Add(new CachedGameDetection
                {
                    GameAppId = gameAppId,
                    GameName = "Native Repair",
                    CacheFilesFound = 1,
                    TotalSizeBytes = cacheBytes,
                    DepotIdsJson = "[]",
                    SampleUrlsJson = JsonSerializer.Serialize(new[] { url }),
                    DatasourcesJson = JsonSerializer.Serialize(new[] { datasourceName }),
                    Service = "steam",
                    IsEvicted = false,
                    LastDetectedUtc = DateTime.UtcNow,
                    CreatedAtUtc = DateTime.UtcNow
                });
                seed.CachedDetectionSummaries.Add(new CachedDetectionSummary
                {
                    GamesOnDiskBytes = cacheBytes,
                    GamesOnDiskCount = 1,
                    IdentifiedCacheBytes = cacheBytes,
                    ComputedAtUtc = DateTime.UtcNow
                });
                await seed.SaveChangesAsync();
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = datasourceName,
                    ["LanCache:DataSources:0:CachePath"] = cacheRoot,
                    ["LanCache:DataSources:0:LogPath"] = logRoot,
                    ["LanCache:DataSources:0:Enabled"] = "true",
                    ["LanCache:DataSources:0:SchemeOverride"] = "monolithic"
                })
                .Build();
            var pathResolver = DispatchProxy.Create<IPathResolver, NativePaths>();
            var paths = (NativePaths)(object)pathResolver;
            paths.Root = root;
            paths.CacheRoot = cacheRoot;
            paths.LogRoot = logRoot;
            paths.ClearBinary = clearBinary;
            paths.ScanBinary = scanBinary;

            var clearProcesses = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var clearTracker = new UnifiedOperationTracker(
                clearProcesses,
                NullLogger<UnifiedOperationTracker>.Instance);
            var clearRust = new NativeRust(
                NullLogger<RustProcessHelper>.Instance,
                clearProcesses,
                pathResolver,
                clearTracker,
                databaseUrl);
            var clearStart = clearRust.CreateProcessStartInfo(
                clearBinary,
                [
                    cacheRoot,
                    progressPath,
                    "full",
                    "--progress",
                    "--operation-id",
                    operationId.ToString()
                ]);
            var clearResult = await clearRust.ExecuteTrackedProcessWithProgressEventsAsync(
                clearStart,
                operationId: null,
                CancellationToken.None,
                onProgressEvent: null,
                processLabel: "cache_clear");
            Assert.Equal(0, clearResult.ExitCode);
            Assert.False(File.Exists(cacheFile));
            Assert.True(File.Exists(receiptPath));

            using var progress = JsonDocument.Parse(await File.ReadAllTextAsync(progressPath));
            var progressRoot = progress.RootElement;
            Assert.Equal("completed", progressRoot.GetProperty("status").GetString());
            Assert.False(progressRoot.GetProperty("isProcessing").GetBoolean());
            var filesDeleted = progressRoot.GetProperty("filesDeleted").GetInt64();
            var bytesDeleted = progressRoot.GetProperty("bytesDeleted").GetInt64();
            var directoriesProcessed = progressRoot.GetProperty("directoriesProcessed").GetInt32();
            var totalDirectories = progressRoot.GetProperty("totalDirectories").GetInt32();
            Assert.Equal(1, filesDeleted);
            Assert.Equal(cacheBytes, bytesDeleted);
            Assert.True(directoriesProcessed > 0);
            Assert.True(totalDirectories >= directoriesProcessed);

            using (var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath)))
            {
                Assert.Equal(1, receipt.RootElement.GetProperty("version").GetInt32());
                Assert.Equal(
                    operationId,
                    receipt.RootElement.GetProperty("operationId").GetGuid());
                Assert.Equal(
                    Path.GetFullPath(cacheRoot),
                    receipt.RootElement.GetProperty("cachePath").GetString());
                Assert.True(receipt.RootElement.GetProperty("hadCacheFiles").GetBoolean());
            }

            var state = CreateState(root, configuration, pathResolver);
            state.SetSetupCompleted(true);
            state.SaveOperationRepairs(
            [
                new OperationRepair
                {
                    Id = operationId,
                    Type = OperationType.CacheClearing,
                    Name = "Cache clear",
                    StartedAt = DateTime.UtcNow,
                    Notice = new RunNotice(NotificationMode.All, RunTrigger.Manual),
                    Phase = OperationRepairPhase.Running,
                    Sources =
                    [
                        new OperationRepairSource
                        {
                            Datasource = datasourceName,
                            LogRoot = logRoot,
                            CacheRoot = cacheRoot,
                            KeyScheme = "monolithic",
                            NativeLaunchAuthorized = true,
                            NativeCompletionAccepted = true,
                            ReceiptPath = receiptPath,
                            RefreshDownloads = true,
                            ReconcileCache = true,
                            RefreshDetection = true,
                            InvalidateCorruption = true
                        }
                    ],
                    CacheClearing = new CacheClearingRepair
                    {
                        EntityKey = "all",
                        DirectoriesProcessed = directoriesProcessed,
                        TotalDirectories = totalDirectories,
                        BytesDeleted = bytesDeleted,
                        FilesDeleted = filesDeleted,
                        DatasourcesCleared = 1
                    }
                }
            ]);

            OperationRepair? repairAtNotification = null;
            EvictionScanCheckpoint? checkpointAtNotification = null;
            CachedGameDetection? detectionAtNotification = null;
            CachedDetectionSummary? summaryAtNotification = null;
            bool? selectedEvictedAtNotification = null;
            bool? foreignEvictedAtNotification = null;
            bool? receiptPresentAtNotification = null;
            var notificationBarrier = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, NotificationSink>();
            var notificationSink = (NotificationSink)(object)notifications;
            notificationSink.OnCacheClearingComplete = async message =>
            {
                try
                {
                    var persistedRepair = Assert.Single(state.LoadOperationRepairs());
                    repairAtNotification = persistedRepair;
                    receiptPresentAtNotification = File.Exists(receiptPath);
                    await using var callbackContext = new AppDbContext(contextOptions);
                    selectedEvictedAtNotification = await callbackContext.Downloads
                        .Where(download => download.Id == selectedDownloadId)
                        .Select(download => download.IsEvicted)
                        .SingleAsync();
                    foreignEvictedAtNotification = await callbackContext.Downloads
                        .Where(download => download.Id == foreignDownloadId)
                        .Select(download => download.IsEvicted)
                        .SingleAsync();
                    detectionAtNotification = await callbackContext.CachedGameDetections
                        .AsNoTracking()
                        .SingleAsync(detection => detection.GameAppId == gameAppId);
                    summaryAtNotification = await callbackContext.CachedDetectionSummaries
                        .AsNoTracking()
                        .SingleAsync(summary => summary.Id == CachedDetectionSummary.SingletonId);
                    checkpointAtNotification = await callbackContext.EvictionScanCheckpoints
                        .AsNoTracking()
                        .SingleAsync(checkpoint =>
                            checkpoint.OperationId == persistedRepair.EvictionScanId);
                    notificationBarrier.TrySetResult(message);
                }
                catch (Exception exception)
                {
                    notificationBarrier.TrySetException(exception);
                    throw;
                }
            };

            firstHost = CreateHost(
                configuration,
                pathResolver,
                state,
                contextOptions,
                databaseUrl,
                notifications);
            var firstTracker = firstHost.Services.GetRequiredService<UnifiedOperationTracker>();
            var terminalCount = 0;
            var terminalBarrier = new TaskCompletionSource<OperationInfo>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnTerminal(OperationInfo operation)
            {
                if (operation.Id == operationId)
                {
                    Interlocked.Increment(ref terminalCount);
                    terminalBarrier.TrySetResult(operation);
                }
            }
            firstTracker.OperationTerminal += OnTerminal;
            await firstHost.StartAsync();
            var notification = Assert.IsType<CacheClearComplete>(
                await notificationBarrier.Task.WaitAsync(TimeSpan.FromMinutes(2)));
            var terminalOperation = await terminalBarrier.Task.WaitAsync(TimeSpan.FromMinutes(2));
            firstTracker.OperationTerminal -= OnTerminal;

            Assert.Equal(1, Volatile.Read(ref terminalCount));
            Assert.Equal(1, notificationSink.CacheClearingCompleteCount);
            Assert.Equal(operationId, notification.OperationId);
            Assert.False(notification.Success);
            Assert.Equal(OperationStatus.Failed, notification.Status);
            Assert.False(notification.Cancelled);
            Assert.Equal(OperationStatus.Failed, terminalOperation.Status);

            var completedRepair = Assert.IsType<OperationRepair>(repairAtNotification);
            Assert.Equal(operationId, completedRepair.Id);
            Assert.Equal(OperationRepairPhase.Completed, completedRepair.Phase);
            Assert.Equal(OperationStatus.Failed, completedRepair.Outcome);
            Assert.Equal("Operation interrupted by application restart", completedRepair.Error);
            Assert.Null(completedRepair.RetryAtUtc);
            Assert.NotNull(completedRepair.CompletedAt);
            Assert.NotNull(completedRepair.EvictionScanId);
            Assert.NotEqual(operationId, completedRepair.EvictionScanId);
            var acceptedSource = Assert.Single(completedRepair.Sources);
            Assert.True(acceptedSource.NativeCompletionAccepted);
            Assert.False(receiptPresentAtNotification);
            var acceptedMetrics = Assert.IsType<CacheClearingRepair>(completedRepair.CacheClearing);
            Assert.Equal(directoriesProcessed, acceptedMetrics.DirectoriesProcessed);
            Assert.Equal(totalDirectories, acceptedMetrics.TotalDirectories);
            Assert.Equal(bytesDeleted, acceptedMetrics.BytesDeleted);
            Assert.Equal(filesDeleted, acceptedMetrics.FilesDeleted);
            Assert.Equal(1, acceptedMetrics.DatasourcesCleared);

            Assert.True(selectedEvictedAtNotification);
            Assert.False(foreignEvictedAtNotification);
            var completedCheckpoint = Assert.IsType<EvictionScanCheckpoint>(checkpointAtNotification);
            Assert.Equal(completedRepair.EvictionScanId, completedCheckpoint.OperationId);
            Assert.Equal(1, completedCheckpoint.Processed);
            Assert.Equal(1, completedCheckpoint.Evicted);
            Assert.Equal(0, completedCheckpoint.UnEvicted);
            Assert.NotNull(completedCheckpoint.FinalizedAtUtc);
            var completedDetection = Assert.IsType<CachedGameDetection>(detectionAtNotification);
            Assert.True(completedDetection.IsEvicted);
            Assert.Equal(0UL, completedDetection.TotalSizeBytes);
            var completedSummary = Assert.IsType<CachedDetectionSummary>(summaryAtNotification);
            Assert.Equal(0UL, completedSummary.GamesOnDiskBytes);
            Assert.Equal(0, completedSummary.GamesOnDiskCount);
            Assert.Equal(0UL, completedSummary.IdentifiedCacheBytes);

            await firstHost.StopAsync();
            firstHost.Dispose();
            firstHost = null;

            var secondState = CreateState(root, configuration, pathResolver);
            secondState.SetSetupCompleted(true);
            var secondNotifications = DispatchProxy.Create<ISignalRNotificationService, NotificationSink>();
            var secondSink = (NotificationSink)(object)secondNotifications;
            secondHost = CreateHost(
                configuration,
                pathResolver,
                secondState,
                contextOptions,
                databaseUrl,
                secondNotifications);
            var secondTracker = secondHost.Services.GetRequiredService<UnifiedOperationTracker>();
            var repeatedTerminalCount = 0;
            void OnRepeatedTerminal(OperationInfo operation)
            {
                if (operation.Id == operationId)
                {
                    Interlocked.Increment(ref repeatedTerminalCount);
                }
            }
            secondTracker.OperationTerminal += OnRepeatedTerminal;
            await secondHost.StartAsync();
            var secondOwner = secondHost.Services.GetRequiredService<OperationStateService>();
            await secondOwner.WaitForRecoveryOwnershipAsync(CancellationToken.None);

            Assert.Empty(secondOwner.GetPendingRepairs());
            Assert.Null(secondTracker.GetOperation(operationId));
            Assert.Equal(0, Volatile.Read(ref repeatedTerminalCount));
            Assert.Equal(0, secondSink.CacheClearingCompleteCount);
            var retainedRepair = Assert.Single(secondState.LoadOperationRepairs());
            Assert.Equal(OperationRepairPhase.Completed, retainedRepair.Phase);

            secondTracker.OperationTerminal -= OnRepeatedTerminal;
            await secondHost.StopAsync();
            secondHost.Dispose();
            secondHost = null;
        }
        finally
        {
            if (secondHost is not null)
            {
                await secondHost.StopAsync(CancellationToken.None);
                secondHost.Dispose();
            }
            if (firstHost is not null)
            {
                await firstHost.StopAsync(CancellationToken.None);
                firstHost.Dispose();
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static StateService CreateState(
        string root,
        IConfiguration configuration,
        IPathResolver pathResolver)
    {
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys")));
        var apiKeys = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            pathResolver);
        var encryption = new SecureStateEncryptionService(
            protection,
            apiKeys,
            NullLogger<SecureStateEncryptionService>.Instance);
        var steam = new SteamAuthStorageService(
            NullLogger<SteamAuthStorageService>.Instance,
            pathResolver,
            encryption);
        return new StateService(
            NullLogger<StateService>.Instance,
            pathResolver,
            encryption,
            steam);
    }

    private static IHost CreateHost(
        IConfiguration configuration,
        IPathResolver pathResolver,
        StateService state,
        DbContextOptions<AppDbContext> contextOptions,
        string databaseUrl,
        ISignalRNotificationService notifications)
    {
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(configuration);
                services.AddSingleton(pathResolver);
                services.AddSingleton(state);
                services.AddSingleton<IStateService>(state);
                services.AddScoped(_ => new AppDbContext(contextOptions));
                services.AddSingleton<IDbContextFactory<AppDbContext>>(
                    new NativeContexts(contextOptions));
                services.AddSingleton<ProcessManager>();
                services.AddSingleton<UnifiedOperationTracker>();
                services.AddSingleton<IUnifiedOperationTracker>(container =>
                    container.GetRequiredService<UnifiedOperationTracker>());
                services.AddSingleton(notifications);
                services.AddSingleton<DatasourceService>();
                services.AddSingleton<DatasourceCapabilityService>();
                services.AddSingleton<RustProcessHelper>(container => new NativeRust(
                    container.GetRequiredService<ILogger<RustProcessHelper>>(),
                    container.GetRequiredService<ProcessManager>(),
                    pathResolver,
                    container.GetRequiredService<IUnifiedOperationTracker>(),
                    databaseUrl));
                services.AddSingleton<OperationStateService>();
                services.AddSingleton<GameCacheDetectionDataService>();
                services.AddSingleton<EvictedDetectionPreservationService>();
                services.AddSingleton<UnknownGameResolutionService>();
                services.AddSingleton(_ => CacheScanGateHarness.Idle());
                services.AddSingleton<GameCacheDetectionService>();
                services.AddSingleton<NginxLogRotationService>();
                services.AddSingleton<ILancacheEnvFileReader, LancacheEnvFileReader>();
                services.AddSingleton<IOperationConflictChecker, OperationConflictChecker>();
                services.AddSingleton<IOperationQueue, IdleQueue>();
                services.AddSingleton<CorruptionDetectionService>();
                services.AddSingleton<CacheManagementService>();
                services.AddSingleton<CacheReconciliationService>();
                services.AddSingleton<CacheClearingService>();
                services.AddSingleton<IHostedService>(container =>
                    container.GetRequiredService<OperationStateService>());
            })
            .Build();
    }

    public sealed class NativeRun : FactAttribute
    {
        public NativeRun()
        {
            if (Environment.GetEnvironmentVariable(ConnectionVariable) is null
                && Environment.GetEnvironmentVariable(DatabaseUrlVariable) is null
                && Environment.GetEnvironmentVariable(BinaryDirectoryVariable) is null)
            {
                Skip = $"Set {ConnectionVariable}, {DatabaseUrlVariable}, and {BinaryDirectoryVariable} to run.";
            }
        }
    }

    private sealed class NativeContexts(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppDbContext(options));
        }
    }

    private sealed class NativeRust : RustProcessHelper
    {
        private readonly string _databaseUrl;

        public NativeRust(
            ILogger<RustProcessHelper> logger,
            ProcessManager processManager,
            IPathResolver pathResolver,
            IUnifiedOperationTracker operationTracker,
            string databaseUrl)
            : base(logger, processManager, pathResolver, operationTracker)
        {
            _databaseUrl = databaseUrl;
        }

        public override Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo processStart,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            processStart.Environment["DATABASE_URL"] = _databaseUrl;
            return base.ExecuteTrackedProcessWithProgressEventsAsync(
                processStart,
                operationId,
                cancellationToken,
                onProgressEvent,
                processLabel);
        }
    }

    private sealed class IdleQueue : IOperationQueue
    {
        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            bool reportRefusal = false,
            RunNotice? notice = null)
        {
            return Task.FromException<QueuedOperationResponse>(
                new InvalidOperationException("The native repair fixture does not start queued work."));
        }
    }

    private class NativePaths : PathResolverProxy
    {
        public required string CacheRoot { get; set; }
        public required string LogRoot { get; set; }
        public required string ClearBinary { get; set; }
        public required string ScanBinary { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return targetMethod.Name switch
            {
                nameof(IPathResolver.GetCacheDirectory) => CacheRoot,
                nameof(IPathResolver.GetLogsDirectory) => LogRoot,
                nameof(IPathResolver.GetStateDirectory) => Path.Combine(Root, "state"),
                nameof(IPathResolver.GetOperationsDirectory) => Path.Combine(Root, "operations"),
                nameof(IPathResolver.GetRustCacheCleanerPath) => ClearBinary,
                nameof(IPathResolver.GetRustEvictionScanPath) => ScanBinary,
                nameof(IPathResolver.GetStructuralCorruptionStateScope) =>
                    Assert.IsType<string>(args![0]) + ":" + Path.GetFullPath(Assert.IsType<string>(args![1])),
                _ => base.Invoke(targetMethod, args)
            };
        }
    }

    private class NotificationSink : DispatchProxy
    {
        private int _cacheClearingCompleteCount;

        public Func<object?, Task>? OnCacheClearingComplete { get; set; }
        public int CacheClearingCompleteCount => Volatile.Read(ref _cacheClearingCompleteCount);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                var eventName = Assert.IsType<string>(args![0]);
                if (eventName == SignalREvents.CacheClearingComplete)
                {
                    Interlocked.Increment(ref _cacheClearingCompleteCount);
                    return OnCacheClearingComplete?.Invoke(args![1]) ?? Task.CompletedTask;
                }

                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }
            if (targetMethod.ReturnType == typeof(void))
            {
                return null;
            }

            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
