using System.Diagnostics;
using System.Reflection;
using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

/// <summary>
/// A persisted <c>IsEvicted</c> flag says what a past scan saw, not what is on disk now. Clients
/// re-download and nginx re-caches without clearing it, so a game whose rows all read evicted can
/// still own cache files. Removal must therefore always sweep the cache directory: the log rewrite
/// and the row deletes run either way, and skipping the sweep leaves the files attributable to no
/// game and no service, where no named removal path can reach them again.
/// </summary>
public sealed class SteamGameRemovalCacheSweepTests : IDisposable
{
    private const long GameAppId = 570;

    private readonly string _root;
    private readonly CapturingLogger<CacheManagementService> _logger = new();
    private readonly TestDbContextFactory _dbContextFactory;
    private readonly CacheManagementService _service;

    public SteamGameRemovalCacheSweepTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-steam-removal-sweep-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(_root, "cache");
        var logPath = Path.Combine(_root, "logs");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(logPath);
        File.WriteAllText(
            Path.Combine(logPath, "access.log"),
            "10.0.0.5 - - [26/Sep/2026:12:00:00 +0000] \"GET /depot/570/chunk/a HTTP/1.1\" 200 1 \"-\" \"-\" HIT\n");

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        // PrepareRemovalExecutionPlan calls EnsureBinaryExists before it builds any arguments, and
        // that is a File.Exists check. A placeholder is enough to reach the argument string; the
        // launch that follows fails, which is where this test stops.
        File.WriteAllText(pathResolver.GetRustSteamRemoverPath(), string.Empty);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = cachePath,
                ["LanCache:DataSources:0:LogPath"] = logPath,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();

        var datasourceService = new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);

        _dbContextFactory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"steam-removal-sweep-{Guid.NewGuid():N}")
                .Options);

        var operationTracker = DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>();

        _service = new CacheManagementService(
            configuration,
            _logger,
            pathResolver,
            new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver,
                operationTracker),
            new NginxLogRotationService(
                NullLogger<NginxLogRotationService>.Instance,
                configuration,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver,
                TimeProvider.System),
            datasourceService,
            DispatchProxy.Create<IStateService, NullReturningProxy>(),
            _dbContextFactory,
            gameCacheDetectionService: null!,
            operationTracker,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
            DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(),
            new DatasourceCapabilityService(datasourceService),
            CacheScanGateHarness.Idle(),
            (OperationStateService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(OperationStateService)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task FullyEvictedGameStillLaunchesTheCacheSweepAsync()
    {
        await using (var context = _dbContextFactory.CreateDbContext())
        {
            context.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.5",
                StartTimeUtc = DateTime.UtcNow.AddHours(-1),
                EndTimeUtc = DateTime.UtcNow,
                GameAppId = GameAppId,
                CacheHitBytes = 4096,
                IsActive = false,
                IsEvicted = true
            });
            await context.SaveChangesAsync();
        }

        // The placeholder binary cannot run, so removal always throws. The arguments are built and
        // logged before the launch, which is the whole of what this test reads.
        await Assert.ThrowsAnyAsync<Exception>(() => _service.RemoveGameFromCacheAsync(GameAppId));

        var launch = Assert.Single(
            _logger.Entries.Select(entry => entry.Message),
            message => message.Contains("Running removal for datasource", StringComparison.Ordinal));
        Assert.DoesNotContain("--skip-file-probe", launch, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRunner_GameFailureOrCancellationRetainsConfirmedCountersAsync(
        bool cancelled)
    {
        await using var harness = await RemovalRepairHarness.CreateAsync(
            Path.Combine(_root, "game-counter-" + cancelled));
        var metrics = new RemovalMetrics
        {
            EntityKey = GameAppId.ToString(),
            EntityName = "Dota 2",
            EntityKind = "steam"
        };
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            metrics,
            async (operationId, cancellationToken, report) =>
            {
                await harness.Owner.StartWorkAsync(operationId, "alpha", cancellationToken);
                await harness.SaveSourceAsync(operationId, "alpha", 9, 200);
                await report(new RemovalProgressUpdate(
                    50,
                    "alpha-complete",
                    FilesDeleted: 9,
                    BytesFreed: 200));

                await harness.Owner.StartWorkAsync(operationId, "beta", cancellationToken);
                await report(new RemovalProgressUpdate(
                    60,
                    "beta-progress",
                    FilesDeleted: 9,
                    BytesFreed: 200));
                if (cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new IOException("Injected beta removal failure.");
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);

        Assert.Equal(
            cancelled ? OperationStatus.Cancelled : OperationStatus.Failed,
            terminal.Status);
        Assert.Equal(9, metrics.FilesDeleted);
        Assert.Equal(200L, metrics.BytesFreed);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);
        Assert.Equal(OperationRepairPhase.Completed, repair.Phase);
        Assert.Equal(9, repair.Removal!.FilesDeleted);
        Assert.Equal(200L, repair.Removal.BytesFreed);
        var complete = Assert.IsType<SignalRNotifications.GameRemovalComplete>(
            Assert.Single(
                harness.ReadMessages(),
                message => message.Event == "complete").Value);
        Assert.Equal(9, complete.FilesDeleted);
        Assert.Equal(200L, complete.BytesFreed);
        Assert.Equal(cancelled, complete.Cancelled);
        await harness.CompleteAnotherAsync(OperationType.GameRemoval);
    }

    [Fact]
    public async Task RemovalRunner_CancelEndsTheCardWhileItsRepairWaitsAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateAsync(Path.Combine(_root, "cancel-waits"));
        var blocker = await harness.PrepareAnotherAsync(OperationType.GameRemoval);
        await harness.Owner.StartWorkAsync(blocker, "alpha", CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            new RemovalMetrics { EntityKey = GameAppId.ToString(), EntityName = "Dota 2", EntityKind = "steam" },
            async (operationId, cancellationToken, _) =>
            {
                await harness.Owner.StartWorkAsync(operationId, "alpha", cancellationToken);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return (0, 0L);
            });
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Another job is still running, so the repair this cancel owes has to wait; the card does not.
        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
        var terminal = await harness.WaitForTerminalAsync(operationId).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        Assert.True(Assert.IsType<SignalRNotifications.GameRemovalComplete>(
            await harness.WaitForCompleteMessageAsync()).Cancelled);
        Assert.Equal(OperationRepairPhase.Repairing, harness.ReadRepair(operationId).Phase);
        Assert.True(harness.Tracker.GetRuns().Runs.Single(run => run.OperationId == operationId).Repairing);

        await harness.Owner.FinishRepairAsync(blocker, true, false, null);
        await harness.WaitForCompletedRepairAsync(operationId);
        await harness.WaitForCompletedRepairAsync(blocker);
        Assert.DoesNotContain(
            harness.Tracker.GetRuns().Runs,
            run => run.OperationId == operationId && run.Repairing);
    }

    [Fact]
    public async Task RemovalRunner_ForceStopEndsTheCardAndTheRepairStillRunsAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateAsync(Path.Combine(_root, "force-stop"));
        var blocker = await harness.PrepareAnotherAsync(OperationType.ServiceRemoval);
        await harness.Owner.StartWorkAsync(blocker, "alpha", CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            new RemovalMetrics { EntityKey = GameAppId.ToString(), EntityName = "Dota 2", EntityKind = "steam" },
            async (operationId, cancellationToken, _) =>
            {
                await harness.Owner.StartWorkAsync(operationId, "alpha", cancellationToken);
                started.TrySetResult();
                // A job whose native step does not stop on cancel.
                await ownerRelease.Task;
                cancellationToken.ThrowIfCancellationRequested();
                return (0, 0L);
            });
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancellation = new OperationCancellationService(
            harness.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            harness.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var outcomeAtTerminal = new TaskCompletionSource<OperationRepair>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.OperationTerminal += operation =>
        {
            if (operation.Id == operationId)
            {
                outcomeAtTerminal.TrySetResult(harness.ReadRepair(operationId));
            }
        };

        Assert.True(await cancellation.ForceKillAsync(operationId));
        var terminal = await harness.WaitForTerminalAsync(operationId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        var stored = await outcomeAtTerminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationStatus.Cancelled, stored.Outcome);
        Assert.Equal(OperationRepairPhase.Repairing, harness.ReadRepair(operationId).Phase);

        // A conflicting job queues behind the repair the stopped job owes.
        var queued = await harness.PrepareAnotherAsync(OperationType.GameRemoval);
        var queuedStart = harness.Owner.StartWorkAsync(queued, "alpha", CancellationToken.None);
        Assert.False(queuedStart.IsCompleted);

        ownerRelease.TrySetResult();
        await harness.Owner.FinishRepairAsync(blocker, true, false, null);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        await queuedStart.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RemovalRunner_CancelBeforeWorkStartsSkipsTheRepairAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateAsync(Path.Combine(_root, "cancel-prepared"));
        var blocker = await harness.PrepareAnotherAsync(OperationType.GameRemoval);
        await harness.Owner.StartWorkAsync(blocker, "alpha", CancellationToken.None);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            new RemovalMetrics { EntityKey = GameAppId.ToString(), EntityName = "Dota 2", EntityKind = "steam" },
            async (_, cancellationToken, _) =>
            {
                // Still waiting for the cache lock, so no native work has started.
                waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return (0, 0L);
            });
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Tracker.CancelOperation(operationId);
        var terminal = await harness.WaitForTerminalAsync(operationId).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        var repair = harness.ReadRepair(operationId);
        Assert.Equal(OperationRepairPhase.Completed, repair.Phase);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        Assert.False(harness.Tracker.GetRuns().Runs.Single(run => run.OperationId == operationId).Repairing);
        Assert.Equal(OperationRepairPhase.Running, harness.ReadRepair(blocker).Phase);
        await harness.Owner.FinishRepairAsync(blocker, true, false, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRunner_ExternalTerminalKeepsPresentationAndFinishesRepairAsync(
        bool cancelled)
    {
        await using var harness = await RemovalRepairHarness.CreateAsync(
            Path.Combine(_root, "external-terminal-" + cancelled));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metrics = new RemovalMetrics
        {
            EntityKey = GameAppId.ToString(),
            EntityName = "Dota 2",
            EntityKind = "steam"
        };
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            metrics,
            async (operationId, cancellationToken, report) =>
            {
                await harness.Owner.StartWorkAsync(operationId, "alpha", cancellationToken);
                await harness.SaveSourceAsync(operationId, "alpha", 9, 200);
                await report(new RemovalProgressUpdate(
                    50,
                    "alpha-complete",
                    FilesDeleted: 9,
                    BytesFreed: 200));
                entered.TrySetResult();
                await release.Task;
                await report(new RemovalProgressUpdate(
                    90,
                    "late-progress",
                    FilesDeleted: 90,
                    BytesFreed: 900));
                return (90, 900L);
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Tracker.CompleteOperation(
            operationId,
            success: false,
            error: cancelled ? null : "disk failure",
            cancelled: cancelled);
        var completedAt = harness.Tracker.GetOperation(operationId)!.CompletedAt;
        release.TrySetResult();
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        var terminal = harness.Tracker.GetOperation(operationId)!;
        Assert.Equal(
            cancelled ? OperationStatus.Cancelled : OperationStatus.Failed,
            terminal.Status);
        Assert.Equal(completedAt, terminal.CompletedAt);
        Assert.Equal(9, metrics.FilesDeleted);
        Assert.Equal(200L, metrics.BytesFreed);
        Assert.Equal(9, repair.Removal!.FilesDeleted);
        Assert.Equal(200L, repair.Removal.BytesFreed);
        Assert.DoesNotContain(
            harness.ReadMessages(),
            message => message.Value is RemovalProgressUpdate { StageKey: "late-progress" });
        Assert.Single(
            harness.ReadMessages(),
            message => message.Event == "complete");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRunner_GameProducerKeepsConfirmedCountersDuringBetaProgressAsync(
        bool cancelled)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(
            Path.Combine(_root, "game-producer-" + cancelled),
            OperationType.GameRemoval);
        var metrics = new RemovalMetrics
        {
            EntityKey = GameAppId.ToString(),
            EntityName = "Dota 2",
            EntityKind = "steam"
        };
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            metrics,
            async (operationId, cancellationToken, report) =>
            {
                var result = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    (percent, stage, context, files, bytes) => report(new RemovalProgressUpdate(
                        percent,
                        stage,
                        context,
                        files,
                        bytes)),
                    operationId);
                return (result.CacheFilesDeleted, checked((long)result.TotalBytesFreed));
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        try
        {
            await harness.WaitForBetaProgressAsync();

            Assert.Equal(1, harness.RawFilesProcessed);
            Assert.Equal(2, harness.RustCalls);
            var runningRepair = harness.ReadRepair(operationId);
            Assert.Equal(9, runningRepair.Removal!.FilesDeleted);
            Assert.Equal(200L, runningRepair.Removal.BytesFreed);
            var alpha = runningRepair.Sources.Single(source => source.Datasource == "alpha");
            var beta = runningRepair.Sources.Single(source => source.Datasource == "beta");
            Assert.True(alpha.NativeCompletionAccepted);
            Assert.True(beta.NativeLaunchAuthorized);
            Assert.False(beta.NativeCompletionAccepted);

            var progress = harness.ReadMessages()
                .Where(message => message.Value is RemovalProgressUpdate)
                .Select(message => (RemovalProgressUpdate)message.Value!)
                .ToList();
            var betaProgress = Assert.Single(
                progress,
                update => update.StageKey == harness.BetaStage);
            Assert.Equal(9, betaProgress.FilesDeleted);
            Assert.Equal(200L, betaProgress.BytesFreed);
            Assert.All(progress, update =>
            {
                Assert.True(update.FilesDeleted >= 9);
                Assert.True(update.BytesFreed >= 200);
            });

            if (cancelled)
            {
                Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
            }
            else
            {
                harness.ReleaseRust();
            }

            var terminal = await harness.WaitForTerminalAsync(operationId);
            var repair = await harness.WaitForCompletedRepairAsync(operationId);
            var complete = Assert.IsType<SignalRNotifications.GameRemovalComplete>(
                await harness.WaitForCompleteMessageAsync());

            Assert.Equal(
                cancelled ? OperationStatus.Cancelled : OperationStatus.Failed,
                terminal.Status);
            Assert.Equal(9, complete.FilesDeleted);
            Assert.Equal(200L, complete.BytesFreed);
            Assert.Equal(cancelled, complete.Cancelled);
            Assert.Equal(9, repair.Removal!.FilesDeleted);
            Assert.Equal(200L, repair.Removal.BytesFreed);
            Assert.True(repair.Sources.Single(source => source.Datasource == "alpha")
                .NativeCompletionAccepted);
            Assert.False(repair.Sources.Single(source => source.Datasource == "beta")
                .NativeCompletionAccepted);
            Assert.Single(
                harness.ReadMessages(),
                message => message.Event == "complete");
            Assert.Equal(2, harness.RustCalls);
            await harness.CompleteAnotherAsync(OperationType.GameRemoval);
        }
        finally
        {
            harness.ReleaseRust();
            var operation = harness.Tracker.GetOperation(operationId);
            if (operation?.Status.IsTerminal() != true)
            {
                harness.Tracker.CancelOperation(operationId);
            }
            if (harness.RustCalls >= 2)
            {
                await harness.WaitForRustExitAsync();
            }
            await harness.WaitForTerminalAsync(operationId);
        }
    }

    [Theory]
    [InlineData("cache_steam_remove.exe", "Steam", "steam", "570", 570L, null, null)]
    [InlineData("cache_epic_remove.exe", "Epic", "epicgames", "epic-game", null, "epic-game", "epic-game")]
    [InlineData("cache_riot_remove.exe", "Named", "riot", "Shared title", null, null, "Shared title")]
    [InlineData("cache_blizzard_remove.exe", "Named", "blizzard", "Shared title", null, null, "Shared title")]
    [InlineData("cache_xbox_remove.exe", "Named", "xbox", "Shared title", null, null, "Shared title")]
    [InlineData("cache_service_remove.exe", "Service", "steam", "steam", null, null, null)]
    public async Task ManagerCore_MultiDatasourceFailureRetainsHistoryAndRetryScopesCleanupAsync(
        string binaryName,
        string removalKindName,
        string service,
        string target,
        long? gameAppId,
        string? epicAppId,
        string? gameName)
    {
        var removalKind = Enum.Parse<RemovalKind>(removalKindName);
        var binaryDirectory = Environment.GetEnvironmentVariable("DS_IMPL_RUST_BIN_DIR");
        var connection = Environment.GetEnvironmentVariable("DS_IMPL_MANAGER_CONNECTION");
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_MANAGER_SCHEMA");
        var connectionUrl = Environment.GetEnvironmentVariable("DS_IMPL_MANAGER_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(binaryDirectory) ||
            string.IsNullOrWhiteSpace(connection) ||
            string.IsNullOrWhiteSpace(schema) ||
            string.IsNullOrWhiteSpace(connectionUrl))
        {
            return;
        }

        var binary = Path.Combine(binaryDirectory, binaryName);
        Assert.True(File.Exists(binary));
        var runRoot = Path.Combine(_root, "manager-core-" + Path.GetFileNameWithoutExtension(binaryName));
        var alphaCache = Path.Combine(runRoot, "alpha-cache");
        var alphaLogs = Path.Combine(runRoot, "alpha-logs");
        var betaCache = Path.Combine(runRoot, "beta-cache");
        var betaLogs = Path.Combine(runRoot, "beta-logs");
        foreach (var path in new[] { alphaCache, alphaLogs, betaCache, betaLogs })
        {
            Directory.CreateDirectory(path);
        }
        var targetUrl = $"/target/{service}";
        var targetLine = $"[{service}] 192.0.2.30 / - - - [26/Sep/2026:18:00:00 +0000] \"GET {targetUrl} HTTP/1.1\" 200 12 \"-\" \"test\" \"HIT\" \"-\" \"-\"";
        var keepLine = "[epicgames] 192.0.2.30 / - - - [26/Sep/2026:18:00:01 +0000] \"GET /keep HTTP/1.1\" 200 8 \"-\" \"test\" \"HIT\" \"-\" \"-\"";
        var alphaLog = Path.Combine(alphaLogs, "access.log");
        var betaLog = Path.Combine(betaLogs, "access.log");
        await File.WriteAllLinesAsync(alphaLog, new[] { targetLine, keepLine });
        await File.WriteAllLinesAsync(betaLog, new[] { targetLine, keepLine });
        var settings = new NpgsqlConnectionStringBuilder(connection)
        {
            SearchPath = schema + ",public",
            ApplicationName = "ds-impl-a-manager"
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(settings.ConnectionString)
            .Options;
        var contexts = new TestDbContextFactory(options);
        await using (var context = contexts.CreateDbContext())
        {
            await context.LogEntries.ExecuteDeleteAsync();
            await context.Downloads.ExecuteDeleteAsync();
            foreach (var name in new[] { "alpha", "beta", "retired" })
            {
                var download = new Download
                {
                    Service = service,
                    ClientIp = "192.0.2.30",
                    StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
                    EndTimeUtc = DateTime.UtcNow,
                    GameAppId = gameAppId,
                    EpicAppId = epicAppId,
                    GameName = gameName,
                    CacheHitBytes = 12,
                    IsActive = false,
                    IsEvicted = false,
                    Datasource = name,
                    LastUrl = targetUrl
                };
                context.Downloads.Add(download);
                await context.SaveChangesAsync();
                context.LogEntries.Add(new LogEntryRecord
                {
                    DownloadId = download.Id,
                    Service = service,
                    Datasource = name,
                    ClientIp = "192.0.2.30",
                    Timestamp = DateTime.UtcNow,
                    Method = "GET",
                    Url = targetUrl,
                    StatusCode = 200,
                    BytesServed = 12,
                    CacheStatus = "HIT",
                    CreatedAt = DateTime.UtcNow
                });
            }
            await context.SaveChangesAsync();
        }
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = alphaCache,
                ["LanCache:DataSources:0:LogPath"] = alphaLogs,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic,
                ["LanCache:DataSources:1:Name"] = "beta",
                ["LanCache:DataSources:1:CachePath"] = betaCache,
                ["LanCache:DataSources:1:LogPath"] = betaLogs,
                ["LanCache:DataSources:1:Enabled"] = "true",
                ["LanCache:DataSources:1:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic,
                ["NginxLogRotation:ContainerName"] = "alpha,beta"
            })
            .Build();
        var pathResolver = DispatchProxy.Create<IPathResolver, ManagerPathResolverProxy>();
        var pathState = (ManagerPathResolverProxy)(object)pathResolver;
        pathState.Root = runRoot;
        pathState.SteamBinary = binary;
        var sources = new DatasourceService(configuration, pathResolver, NullLogger<DatasourceService>.Instance);
        Assert.Equal(2, sources.GetDatasources().Count);
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>();
        var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var rust = new RustProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            processes,
            pathResolver,
            tracker);
        var rotation = new NginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            configuration,
            processes,
            pathResolver,
            TimeProvider.System);
        var state = DispatchProxy.Create<IStateService, NullReturningProxy>();
        var managerLogger = new CapturingLogger<CacheManagementService>();
        CacheManagementService CreateManager(NginxLogRotationService rotationService) => new(
            configuration,
            managerLogger,
            pathResolver,
            rust,
            rotationService,
            sources,
            state,
            contexts,
            gameCacheDetectionService: null!,
            tracker,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
            DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(),
            new DatasourceCapabilityService(sources),
            CacheScanGateHarness.Idle(),
            (OperationStateService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(OperationStateService)));
        var manager = CreateManager(rotation);
        var method = typeof(CacheManagementService).GetMethod(
            "RunGameRemovalAcrossDatasourcesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var failAfterFirstDatasource = true;
        void InjectFailure(
            CacheManagementService.GameCacheRemovalReport aggregatedReport,
            CacheManagementService.GameCacheRemovalReport datasourceReport)
        {
            if (failAfterFirstDatasource)
            {
                failAfterFirstDatasource = false;
                throw new InvalidOperationException("Injected failure after the first datasource completed");
            }
        }
        Task<CacheManagementService.GameCacheRemovalReport> RunAsync(
            CacheManagementService selectedManager,
            Action<CacheManagementService.GameCacheRemovalReport, CacheManagementService.GameCacheRemovalReport>? aggregate = null,
            Func<double, string, Dictionary<string, object?>?, int, long, Task>? progress = null) =>
            (Task<CacheManagementService.GameCacheRemovalReport>)method.Invoke(
            selectedManager,
            [
                "[GameRemoval]", binary, "Game cache remover", "game_removal",
                target, target, "game_cache_remover", "GameRemoval",
                new RemovalSelection(
                    Array.Empty<string>(),
                    removalKind,
                    gameAppId,
                gameName,
                service),
                new CacheManagementService.GameCacheRemovalReport { GameAppId = gameAppId ?? 0 }, CancellationToken.None,
                progress, null,
                aggregate
            ])!;
        var priorUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        Environment.SetEnvironmentVariable("DATABASE_URL", connectionUrl);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(manager, InjectFailure));

            await using (var context = contexts.CreateDbContext())
            {
                Assert.Equal(3, await context.Downloads.CountAsync());
                Assert.Equal(3, await context.LogEntries.CountAsync());
            }
            var alphaChanged = !File.Exists(alphaLog) ||
                !((await File.ReadAllTextAsync(alphaLog)).Contains(targetUrl, StringComparison.Ordinal));
            var betaChanged = !File.Exists(betaLog) ||
                !((await File.ReadAllTextAsync(betaLog)).Contains(targetUrl, StringComparison.Ordinal));
            Assert.True(
                alphaChanged ^ betaChanged,
                string.Join(Environment.NewLine, managerLogger.Entries.Select(entry => entry.Message)));

            if (binaryName == "cache_steam_remove.exe")
            {
                pathState.DockerSocketAvailable = true;
                foreach (var failingWriter in new[] { "alpha", "beta" })
                {
                    await File.WriteAllLinesAsync(alphaLog, new[] { targetLine, keepLine });
                    await File.WriteAllLinesAsync(betaLog, new[] { targetLine, keepLine });
                    var requiredRotation = new RequiredReopenFailureService(
                        configuration,
                        processes,
                        pathResolver,
                        alphaLogs,
                        betaLogs,
                        failingWriter);
                    await Assert.ThrowsAsync<IOException>(() =>
                        RunAsync(CreateManager(requiredRotation)));
                    await using var retainedContext = contexts.CreateDbContext();
                    Assert.Equal(3, await retainedContext.Downloads.CountAsync());
                    Assert.Equal(3, await retainedContext.LogEntries.CountAsync());
                }
                pathState.DockerSocketAvailable = false;
                await File.WriteAllLinesAsync(alphaLog, new[] { targetLine, keepLine });
                await File.WriteAllLinesAsync(betaLog, new[] { targetLine, keepLine });
            }

            var progressCounts = new List<(int Files, long Bytes)>();
            var report = await RunAsync(
                manager,
                progress: (_, _, _, files, bytes) =>
                {
                    progressCounts.Add((files, bytes));
                    return Task.CompletedTask;
                });

            Assert.True(report.LogEntriesRemoved > 0);
            Assert.NotEmpty(progressCounts);
            Assert.All(
                progressCounts.Zip(progressCounts.Skip(1)),
                pair =>
                {
                    Assert.True(pair.First.Files <= pair.Second.Files);
                    Assert.True(pair.First.Bytes <= pair.Second.Bytes);
                });
            Assert.Equal(report.CacheFilesDeleted, progressCounts[^1].Files);
            Assert.Equal(checked((long)report.TotalBytesFreed), progressCounts[^1].Bytes);
            await using var finalContext = contexts.CreateDbContext();
            var remainingDownload = Assert.Single(await finalContext.Downloads.ToListAsync());
            Assert.Equal("retired", remainingDownload.Datasource);
            var remainingLog = Assert.Single(await finalContext.LogEntries.ToListAsync());
            Assert.Equal("retired", remainingLog.Datasource);
            Assert.DoesNotContain(targetUrl, await File.ReadAllTextAsync(alphaLog), StringComparison.Ordinal);
            Assert.DoesNotContain(targetUrl, await File.ReadAllTextAsync(betaLog), StringComparison.Ordinal);
            Assert.Contains("/keep", await File.ReadAllTextAsync(alphaLog), StringComparison.Ordinal);
            Assert.Contains("/keep", await File.ReadAllTextAsync(betaLog), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATABASE_URL", priorUrl);
        }
    }

    private class ManagerPathResolverProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;
        public string SteamBinary { get; set; } = string.Empty;
        public string LogManagerBinary { get; set; } = string.Empty;
        public bool DockerSocketAvailable { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IPathResolver.GetRustSteamRemoverPath))
            {
                return SteamBinary;
            }
            if (targetMethod.Name == nameof(IPathResolver.GetRustLogManagerPath))
            {
                return LogManagerBinary;
            }
            if (targetMethod.Name == nameof(IPathResolver.GetOperationsDirectory))
            {
                var path = Path.Combine(Root, "operations");
                Directory.CreateDirectory(path);
                return path;
            }
            if (targetMethod.Name == nameof(IPathResolver.ResolvePath))
            {
                var path = Assert.IsType<string>(args![0]);
                return Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            }
            if (targetMethod.Name == nameof(IPathResolver.NormalizePath))
            {
                return Assert.IsType<string>(args![0]);
            }
            if (targetMethod.Name == nameof(IPathResolver.IsDockerSocketAvailable))
            {
                return DockerSocketAvailable;
            }
            if (targetMethod.ReturnType == typeof(string))
            {
                return Path.Combine(Root, targetMethod.Name);
            }
            if (targetMethod.ReturnType == typeof(bool))
            {
                return true;
            }
            if (targetMethod.ReturnType == typeof(int))
            {
                return 0;
            }
            return null;
        }
    }

    private sealed class RequiredReopenFailureService : NginxLogRotationService
    {
        private readonly string _alphaLogs;
        private readonly string _betaLogs;
        private readonly string _failingWriter;

        public RequiredReopenFailureService(
            IConfiguration configuration,
            ProcessManager processManager,
            IPathResolver pathResolver,
            string alphaLogs,
            string betaLogs,
            string failingWriter)
            : base(
                NullLogger<NginxLogRotationService>.Instance,
                configuration,
                processManager,
                pathResolver,
                TimeProvider.System)
        {
            _alphaLogs = alphaLogs;
            _betaLogs = betaLogs;
            _failingWriter = failingWriter;
        }

        protected override bool CanProbeHostWriters => false;
        protected override bool CanReplaceDockerLogs => true;

        protected override Task<ProcessCommandResult> RunProcessAsync(
            ProcessStartInfo process,
            string label,
            CancellationToken cancellationToken = default)
        {
            var arguments = process.Arguments;
            var writer = arguments.Contains(" beta", StringComparison.Ordinal) ? "beta" : "alpha";
            if (label == "docker nginx mount inspection")
            {
                var root = writer == "beta" ? _betaLogs : _alphaLogs;
                return Task.FromResult(new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{root}|/logs\n"
                });
            }
            if (label == "docker nginx writer identity")
            {
                return Task.FromResult(new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = writer == "beta" ? "2|200\n" : "1|100\n"
                });
            }
            if (label == "docker nginx verified reopen")
            {
                return Task.FromResult(new ProcessCommandResult
                {
                    ExitCode = writer == _failingWriter ? 1 : 0,
                    Error = writer == _failingWriter ? "injected reopen failure" : string.Empty
                });
            }
            return Task.FromResult(new ProcessCommandResult { ExitCode = 1 });
        }
    }
}
