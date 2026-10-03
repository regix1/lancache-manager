using System.Diagnostics;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

public sealed class DatasourceRemovalTests
{
    [Fact]
    public async Task RemovalRunner_ServiceSourcesPublishCumulativeConfirmedCountersAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "lm-service-removal-counters-" + Guid.NewGuid().ToString("N"));
        await using var harness = await RemovalRepairHarness.CreateAsync(root);
        var metrics = new RemovalMetrics
        {
            EntityKey = "steam",
            EntityName = "steam",
            EntityKind = "service"
        };
        var config = harness.CreateConfig(
            OperationType.ServiceRemoval,
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
                await harness.SaveSourceAsync(operationId, "beta", 11, 230);
                await report(new RemovalProgressUpdate(
                    100,
                    "beta-complete",
                    FilesDeleted: 11,
                    BytesFreed: 230));
                return (11, 230L);
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        Assert.Equal(11, metrics.FilesDeleted);
        Assert.Equal(230L, metrics.BytesFreed);
        // The repair finishes after the card's terminal.
        var repair = await harness.WaitForCompletedRepairAsync(operationId);
        Assert.Equal(OperationRepairPhase.Completed, repair.Phase);
        Assert.Equal(11, repair.Removal!.FilesDeleted);
        Assert.Equal(230L, repair.Removal.BytesFreed);
        var progress = harness.ReadMessages()
            .Where(message => message.Value is RemovalProgressUpdate)
            .Select(message => (RemovalProgressUpdate)message.Value!)
            .ToList();
        Assert.Equal(new[] { 9, 9, 11 }, progress.Select(update => update.FilesDeleted));
        Assert.Equal(new[] { 200L, 200L, 230L }, progress.Select(update => update.BytesFreed));
        var complete = Assert.IsType<SignalRNotifications.ServiceRemovalComplete>(
            Assert.Single(
                harness.ReadMessages(),
                message => message.Event == "complete").Value);
        Assert.Equal(11, complete.FilesDeleted);
        Assert.Equal(230L, complete.BytesFreed);
        await harness.CompleteAnotherAsync(OperationType.ServiceRemoval);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRunner_ServiceProducerKeepsConfirmedCountersDuringBetaProgressAsync(
        bool cancelled)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "lm-service-removal-producer-" + cancelled + "-" + Guid.NewGuid().ToString("N"));
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(
            root,
            OperationType.ServiceRemoval);
        var metrics = new RemovalMetrics
        {
            EntityKey = "steam",
            EntityName = "steam",
            EntityKind = "service"
        };
        var config = harness.CreateConfig(
            OperationType.ServiceRemoval,
            metrics,
            async (operationId, cancellationToken, report) =>
            {
                var result = await harness.Manager.RemoveServiceFromCacheAsync(
                    "steam",
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
            var complete = Assert.IsType<SignalRNotifications.ServiceRemovalComplete>(
                await harness.WaitForCompleteMessageAsync());

            // Alpha finished and beta failed, so the run completes with a warning naming beta; a cancel
            // stays a cancel.
            Assert.Equal(
                cancelled ? OperationStatus.Cancelled : OperationStatus.Completed,
                terminal.Status);
            Assert.Equal(
                !cancelled,
                terminal.Warnings.Any(warning => warning.StageKey == "common.notifications.warnings.datasourcesFailed"));
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
            await harness.CompleteAnotherAsync(OperationType.ServiceRemoval);
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
    [InlineData("riot")]
    [InlineData("blizzard")]
    [InlineData("xbox")]
    public async Task NamedSelection_IsolatesServiceGameAndDatasourceAsync(string service)
    {
        await using var context = CreateContext();
        var selected = DownloadRow(service, "Shared title", "Alpha");
        context.Downloads.AddRange(
            selected,
            DownloadRow(service, "Shared title", "Beta"),
            DownloadRow("other", "Shared title", "Alpha"),
            DownloadRow(service, "Other title", "Alpha"),
            DownloadRow(service, "Shared title", "Alpha", gameAppId: 10),
            DownloadRow(service, "Shared title", "Alpha", epicAppId: "epic-id"));
        await context.SaveChangesAsync();
        var selection = new RemovalSelection(
            new[] { "alpha" },
            RemovalKind.Named,
            GameName: "Shared title",
            Service: service);

        var matches = await CacheManagementService.SelectRemovalDownloads(context, selection)
            .Select(download => download.Id)
            .ToListAsync();

        Assert.Equal(new[] { selected.Id }, matches);
    }

    [Fact]
    public async Task Validation_RejectsDifferentlyAttributedChildAsync()
    {
        await using var context = CreateContext();
        var download = DownloadRow("riot", "Scoped title", "Alpha");
        context.Downloads.Add(download);
        await context.SaveChangesAsync();
        context.LogEntries.Add(LogRow(download.Id, "riot", "Beta"));
        await context.SaveChangesAsync();
        var selection = new RemovalSelection(
            new[] { "alpha" },
            RemovalKind.Named,
            GameName: "Scoped title",
            Service: "riot");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CacheManagementService.ValidateRemovalSelectionAsync(
                context,
                selection,
                CancellationToken.None));

        Assert.Contains("different datasource", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_AcceptsMatchingChildAsync()
    {
        await using var context = CreateContext();
        var download = DownloadRow("xbox", "Scoped title", "Alpha");
        context.Downloads.Add(download);
        await context.SaveChangesAsync();
        context.LogEntries.Add(LogRow(download.Id, "xbox", "Alpha"));
        await context.SaveChangesAsync();
        var selection = new RemovalSelection(
            new[] { "alpha" },
            RemovalKind.Named,
            GameName: "Scoped title",
            Service: "xbox");

        await CacheManagementService.ValidateRemovalSelectionAsync(
            context,
            selection,
            CancellationToken.None);
    }

    [Fact]
    public async Task ProductionCleanup_Postgres_PreservesUnselectedRowsAsync()
    {
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_SCHEMA");
        if (string.IsNullOrWhiteSpace(schema))
        {
            return;
        }

        await using var context = CreatePostgresContext(schema, "ds-impl-a-removal");
        var selection = new RemovalSelection(
            new[] { "alpha" },
            RemovalKind.Service,
            Service: "steam");

        var result = await CacheManagementService.CleanupRemovalAsync(
            context,
            selection,
            CancellationToken.None);

        Assert.Equal(1, result.DownloadsDeleted);
        Assert.Equal(1, result.LogEntriesDeleted);
        Assert.False(await context.Downloads.AnyAsync(row => row.Datasource.ToLower() == "alpha"));
        Assert.False(await context.LogEntries.AnyAsync(row => row.Datasource.ToLower() == "alpha"));
        Assert.Equal(1, await context.Downloads.CountAsync(row => row.Datasource.ToLower() == "beta"));
        Assert.Equal(1, await context.LogEntries.CountAsync(row => row.Datasource.ToLower() == "beta"));
    }

    [Fact]
    public async Task ProductionCleanup_Postgres_RejectsMismatchedChildAsync()
    {
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_DENIAL_SCHEMA");
        if (string.IsNullOrWhiteSpace(schema))
        {
            return;
        }

        await using var context = CreatePostgresContext(schema, "ds-impl-a-denial");
        var selection = new RemovalSelection(
            new[] { "alpha" },
            RemovalKind.Service,
            Service: "steam");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CacheManagementService.CleanupRemovalAsync(
                context,
                selection,
                CancellationToken.None));

        Assert.Equal(2, await context.Downloads.CountAsync());
        Assert.Equal(2, await context.LogEntries.CountAsync());
        Assert.Equal(2, await context.LogEntries.CountAsync(row => row.DownloadId != null));
    }

    [Fact]
    public async Task ProductionCleanup_Postgres_RollsBackWhenParentDeleteFailsAsync()
    {
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_ROLLBACK_SCHEMA");
        if (string.IsNullOrWhiteSpace(schema))
        {
            return;
        }

        await using var context = CreatePostgresContext(schema, "ds-impl-a-rollback");
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION reject_target_download_delete() RETURNS trigger AS $$
            BEGIN
                IF OLD."Datasource" = 'Alpha' THEN
                    RAISE EXCEPTION 'forced parent delete failure';
                END IF;
                RETURN OLD;
            END;
            $$ LANGUAGE plpgsql;
            DROP TRIGGER IF EXISTS reject_target_download_delete ON "Downloads";
            CREATE TRIGGER reject_target_download_delete BEFORE DELETE ON "Downloads"
            FOR EACH ROW EXECUTE FUNCTION reject_target_download_delete();
            """);
        var selection = new RemovalSelection(
            new[] { "alpha" },
            RemovalKind.Service,
            Service: "steam");

        await Assert.ThrowsAsync<PostgresException>(() =>
            CacheManagementService.CleanupRemovalAsync(
                context,
                selection,
                CancellationToken.None));

        Assert.Equal(2, await context.Downloads.CountAsync());
        Assert.Equal(2, await context.LogEntries.CountAsync());
        Assert.Equal(2, await context.LogEntries.CountAsync(row => row.DownloadId != null));
    }

    [Fact]
    public async Task ProductionResetAndCleanup_Postgres_CompleteWithoutDeadlockAsync()
    {
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_RESET_SCHEMA");
        if (string.IsNullOrWhiteSpace(schema))
        {
            return;
        }

        var resetOptions = CreatePostgresOptions(schema, "ds-impl-a-reset");
        var removalOptions = CreatePostgresOptions(schema, "ds-impl-a-reset-removal");
        await using var resetContext = new AppDbContext(resetOptions);
        await using var removalContext = new AppDbContext(removalOptions);
        await resetContext.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION pause_target_download_delete() RETURNS trigger AS $$
            BEGIN
                PERFORM pg_sleep(2);
                RETURN OLD;
            END;
            $$ LANGUAGE plpgsql;
            DROP TRIGGER IF EXISTS pause_target_download_delete ON "Downloads";
            CREATE TRIGGER pause_target_download_delete BEFORE DELETE ON "Downloads"
            FOR EACH ROW EXECUTE FUNCTION pause_target_download_delete();
            """);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var resetLogger = new CapturingLogger<DatabaseService>();
        var resetRoot = Path.Combine(
            Path.GetTempPath(),
            "ds-impl-a-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resetRoot);
        var service = new DatabaseService(
            resetContext,
            notifications,
            resetLogger,
            new RemovalPathResolver(resetRoot),
            new TestDbContextFactory(resetOptions),
            null!,
            null!,
            null!,
            new ServiceCollection()
                .AddSingleton(services => new OperationStateService(
                    NullLogger<OperationStateService>.Instance,
                    new ConfigurationBuilder().Build(),
                    new StateService(
                        NullLogger<StateService>.Instance,
                        new RemovalPathResolver(resetRoot),
                        null!,
                        null!),
                    services.GetRequiredService<IServiceScopeFactory>(),
                    new RemovalLifetime(),
                    new ProcessManager(NullLogger<ProcessManager>.Instance),
                    tracker))
                .BuildServiceProvider(),
            null!,
            null!,
            null!,
            tracker);
        var operationId = tracker.RegisterOperation(
            OperationType.DatabaseReset,
            "reset",
            new CancellationTokenSource());
        var method = typeof(DatabaseService).GetMethod(
            "DoResetAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var resetTask = (Task)method.Invoke(
            service,
            [operationId, new List<string> { "Downloads" }, false, CancellationToken.None])!;
        await WaitForPostgresSleepAsync();
        var selection = new RemovalSelection(
            new[] { "alpha" },
            RemovalKind.Service,
            Service: "steam");

        var cleanupTask = CacheManagementService.CleanupRemovalAsync(
            removalContext,
            selection,
            CancellationToken.None);
        await Task.WhenAll(resetTask, cleanupTask);
        var cleanup = await cleanupTask;

        Assert.True(
            tracker.GetOperation(operationId)!.Status == OperationStatus.Completed,
            string.Join(Environment.NewLine, resetLogger.Entries.Select(entry =>
                entry.Exception is null ? entry.Message : entry.Message + ": " + entry.Exception)));
        Assert.Equal(0, cleanup.DownloadsDeleted);
        Assert.Equal(0, cleanup.LogEntriesDeleted);
        Assert.Equal(0, await removalContext.Downloads.CountAsync());
        Assert.Equal(2, await removalContext.LogEntries.CountAsync());
        Assert.Equal(0, await removalContext.LogEntries.CountAsync(row => row.DownloadId != null));
        Directory.Delete(resetRoot, recursive: true);
    }

    [CacheClearRun]
    public async Task CacheClear_PartialChildFailure_ReconcilesOnlyCompletedDatasourceAsync()
    {
        var schema = Environment.GetEnvironmentVariable(CacheClearRun.SchemaVariable)!;
        var binary = Environment.GetEnvironmentVariable(CacheClearRun.BinaryVariable)!;

        var root =Path.Combine(Path.GetTempPath(), "ds-impl-a-cache-clear-" + Guid.NewGuid().ToString("N"));
        var alphaCache = Path.Combine(root, "alpha", "cache");
        var betaCache = Path.Combine(root, "beta", "cache");
        var alphaFile = Path.Combine(alphaCache, "aa", "alpha.bin");
        var betaFile = Path.Combine(betaCache, "bb", "beta.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(alphaFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(betaFile)!);
        await File.WriteAllTextAsync(alphaFile, "alpha");
        await File.WriteAllTextAsync(betaFile, "beta");
        var alphaLogs = Path.Combine(root, "alpha", "logs");
        var betaLogs = Path.Combine(root, "beta", "logs");
        Directory.CreateDirectory(alphaLogs);
        Directory.CreateDirectory(betaLogs);

        var options = CreatePostgresOptions(schema, "ds-impl-a-cache-clear");
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.ExecuteSqlRawAsync(
                "UPDATE \"Downloads\" SET \"CacheHitBytes\" = 4096, \"IsActive\" = FALSE, \"IsEvicted\" = FALSE");
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "Alpha",
                ["LanCache:DataSources:0:CachePath"] = alphaCache,
                ["LanCache:DataSources:0:LogPath"] = alphaLogs,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:1:Name"] = "Beta",
                ["LanCache:DataSources:1:CachePath"] = betaCache,
                ["LanCache:DataSources:1:LogPath"] = betaLogs,
                ["LanCache:DataSources:1:Enabled"] = "true"
            })
            .Build();
        var pathResolver = DispatchProxy.Create<IPathResolver, CacheClearPathResolverProxy>();
        var pathState = (CacheClearPathResolverProxy)(object)pathResolver;
        pathState.Root = root;
        pathState.Binary = binary;
        var datasources = new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var state = new StateService(
            NullLogger<StateService>.Instance,
            pathResolver,
            null!,
            null!);
        var contexts = new TestDbContextFactory(options);
        var rust = new CacheClearFailureProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            processManager,
            pathResolver,
            tracker);
        var capability = new DatasourceCapabilityService(datasources);
        OperationStateService operationState = null!;
        CacheClearingService service = null!;
        CacheReconciliationService reconciliation = null!;
        var registrations = new ServiceCollection();
        registrations.AddScoped(_ => new AppDbContext(options));
        registrations.AddSingleton(datasources);
        registrations.AddSingleton(capability);
        registrations.AddSingleton(notifications);
        registrations.AddSingleton(_ => operationState);
        registrations.AddSingleton(_ => service);
        registrations.AddSingleton(_ => reconciliation);
        await using var services = registrations.BuildServiceProvider();
        var lifetime = new RemovalLifetime();
        operationState = new OperationStateService(
            NullLogger<OperationStateService>.Instance,
            configuration,
            state,
            services.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            processManager,
            tracker);
        reconciliation = new CacheClearReconciliation(
            services,
            configuration,
            datasources,
            state,
            notifications,
            tracker,
            rust,
            pathResolver,
            lifetime,
            capability,
            contexts);
        service = new CacheClearingService(
            NullLogger<CacheClearingService>.Instance,
            notifications,
            configuration,
            pathResolver,
            state,
            rust,
            datasources,
            tracker,
            capability,
            operationState);
        await operationState.StartAsync(CancellationToken.None);

        var operationId = Assert.IsType<Guid>(await service.StartCacheClearAsync());
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (tracker.GetOperation(operationId)?.Status.IsTerminal() == true)
            {
                break;
            }
            await Task.Delay(100);
        }

        var operation = tracker.GetOperation(operationId);
        Assert.NotNull(operation);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains("after clearing Alpha", operation.Message, StringComparison.Ordinal);
        Assert.Equal(2, rust.ExecutionCount);
        Assert.False(File.Exists(alphaFile));
        Assert.True(File.Exists(betaFile));
        await using (var verify = new AppDbContext(options))
        {
            Assert.True(await verify.Downloads
                .Where(download => download.Datasource.ToLower() == "alpha")
                .AllAsync(download => download.IsEvicted));
            Assert.True(await verify.Downloads
                .Where(download => download.Datasource.ToLower() == "beta")
                .AllAsync(download => !download.IsEvicted));
        }
        lifetime.StopApplication();
        await operationState.StopAsync(CancellationToken.None);
        Directory.Delete(root, recursive: true);
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("ds-impl-a-removal-" + Guid.NewGuid().ToString("N"))
            .Options;
        return new AppDbContext(options);
    }

    private static AppDbContext CreatePostgresContext(string schema, string applicationName)
    {
        return new AppDbContext(CreatePostgresOptions(schema, applicationName));
    }

    private static DbContextOptions<AppDbContext> CreatePostgresOptions(
        string schema,
        string applicationName)
    {
        var connection = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("The PostgreSQL test connection is missing");
        var settings = new NpgsqlConnectionStringBuilder(connection)
        {
            SearchPath = schema + ",public",
            ApplicationName = applicationName
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(settings.ConnectionString)
            .Options;
        return options;
    }

    private static async Task WaitForPostgresSleepAsync()
    {
        var connection = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("The PostgreSQL test connection is missing");
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'ds-impl-a-reset' AND wait_event = 'PgSleep'",
                database);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("The database reset did not reach the delete barrier");
    }

    private static Download DownloadRow(
        string service,
        string gameName,
        string datasource,
        long? gameAppId = null,
        string? epicAppId = null) => new()
    {
        Service = service,
        GameName = gameName,
        Datasource = datasource,
        GameAppId = gameAppId,
        EpicAppId = epicAppId,
        ClientIp = "10.0.0.5",
        StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
        EndTimeUtc = DateTime.UtcNow
    };

    private static LogEntryRecord LogRow(long downloadId, string service, string datasource) => new()
    {
        DownloadId = downloadId,
        Service = service,
        Datasource = datasource,
        ClientIp = "10.0.0.5",
        Timestamp = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>Runs only against the schema and the cache_clear binary its two variables name.</summary>
    public sealed class CacheClearRun : FactAttribute
    {
        public const string SchemaVariable = "DS_IMPL_CACHE_CLEAR_SCHEMA";
        public const string BinaryVariable = "DS_IMPL_CACHE_CLEAR_BINARY";

        public CacheClearRun()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SchemaVariable))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BinaryVariable)))
            {
                Skip = $"Set {SchemaVariable} and {BinaryVariable} to run.";
            }
        }
    }

    private sealed class RemovalPathResolver(string root) : PathResolverBase(NullLogger.Instance)
    {
        protected override string BasePath => root;
        protected override string RustExecutableExtension => string.Empty;
        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }

    private sealed class CacheClearReconciliation : CacheReconciliationService
    {
        private readonly IDbContextFactory<AppDbContext> _contexts;

        public CacheClearReconciliation(
            IServiceProvider services,
            IConfiguration configuration,
            DatasourceService datasources,
            IStateService state,
            ISignalRNotificationService notifications,
            IUnifiedOperationTracker tracker,
            RustProcessHelper rust,
            IPathResolver paths,
            IHostApplicationLifetime lifetime,
            DatasourceCapabilityService capability,
            IDbContextFactory<AppDbContext> contexts)
            : base(
                services,
                NullLogger<CacheReconciliationService>.Instance,
                configuration,
                datasources,
                state,
                notifications,
                tracker,
                rust,
                nginxLogRotationService: null!,
                paths,
                gameCacheDetectionDataService: null!,
                gameCacheDetectionService: null!,
                evictedDetectionPreservationService: null!,
                operationQueue: null!,
                lifetime,
                capability,
                CacheScanGateHarness.Idle())
        {
            _contexts = contexts;
        }

        public override async Task ReconcileRepairAsync(
            OperationRepair repair,
            CancellationToken stoppingToken)
        {
            await using var context = await _contexts.CreateDbContextAsync(stoppingToken);
            foreach (var source in repair.Sources.Where(source =>
                         source.NativeLaunchAuthorized && source.ReconcileCache))
            {
                var cacheFilesRemain = source.CacheRoot is { } root
                    && Directory.Exists(root)
                    && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Any(path => !Path.GetFileName(path).StartsWith(
                            ".lancache-repair-",
                            StringComparison.OrdinalIgnoreCase));
                if (cacheFilesRemain)
                {
                    continue;
                }

                await context.Downloads
                    .Where(download => download.Datasource.ToLower() == source.Datasource.ToLower())
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(download => download.IsEvicted, true),
                        stoppingToken);
            }
        }
    }

    private sealed class RemovalLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
        }
    }

    private sealed class CacheClearFailureProcessHelper : RustProcessHelper
    {
        public CacheClearFailureProcessHelper(
            ILogger<RustProcessHelper> logger,
            ProcessManager processManager,
            IPathResolver pathResolver,
            IUnifiedOperationTracker operationTracker)
            : base(logger, processManager, pathResolver, operationTracker)
        {
        }

        public int ExecutionCount { get; private set; }

        public override Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo process,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            ExecutionCount++;
            if (ExecutionCount == 2)
            {
                throw new InvalidOperationException("Injected Beta cache-clear child failure");
            }
            return base.ExecuteTrackedProcessWithProgressEventsAsync(
                process,
                operationId,
                cancellationToken,
                onProgressEvent,
                processLabel);
        }
    }

    private class CacheClearPathResolverProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;
        public string Binary { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IPathResolver.GetRustCacheCleanerPath))
            {
                return Binary;
            }
            if (targetMethod.Name == nameof(IPathResolver.GetOperationsDirectory))
            {
                var path = Path.Combine(Root, "operations");
                Directory.CreateDirectory(path);
                return path;
            }
            if (targetMethod.Name == nameof(IPathResolver.GetStateDirectory))
            {
                var path = Path.Combine(Root, "state");
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
                return false;
            }
            if (targetMethod.Name == nameof(IPathResolver.IsDirectoryWritable))
            {
                return true;
            }
            if (targetMethod.ReturnType == typeof(string))
            {
                return Path.Combine(Root, targetMethod.Name);
            }
            if (targetMethod.ReturnType == typeof(bool))
            {
                return false;
            }
            return null;
        }
    }
}
