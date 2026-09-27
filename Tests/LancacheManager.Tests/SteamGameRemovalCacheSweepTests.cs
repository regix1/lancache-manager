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
            CacheScanGateHarness.Idle());
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
            CacheScanGateHarness.Idle());
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
            Action<CacheManagementService.GameCacheRemovalReport, CacheManagementService.GameCacheRemovalReport>? aggregate = null) =>
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
                null, null,
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

            var report = await RunAsync(manager);

            Assert.True(report.LogEntriesRemoved > 0);
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

    [Fact]
    public async Task LogRemoval_ProcessesWritableDatasourceAndScopesDatabaseCleanupAsync()
    {
        var binaryDirectory = Environment.GetEnvironmentVariable("DS_IMPL_RUST_BIN_DIR");
        var connection = Environment.GetEnvironmentVariable("DS_IMPL_MANAGER_CONNECTION");
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_LOG_REMOVAL_SCHEMA");
        var connectionUrl = Environment.GetEnvironmentVariable("DS_IMPL_LOG_REMOVAL_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(binaryDirectory) ||
            string.IsNullOrWhiteSpace(connection) ||
            string.IsNullOrWhiteSpace(schema) ||
            string.IsNullOrWhiteSpace(connectionUrl))
        {
            return;
        }

        var binary = Path.Combine(binaryDirectory, "log_service_manager.exe");
        Assert.True(File.Exists(binary));
        var runRoot = Path.Combine(_root, "log-removal");
        var alphaCache = Path.Combine(runRoot, "alpha-cache");
        var alphaLogs = Path.Combine(runRoot, "alpha-logs");
        var betaCache = Path.Combine(runRoot, "beta-cache");
        var betaLogs = Path.Combine(runRoot, "beta-logs-missing");
        Directory.CreateDirectory(alphaCache);
        Directory.CreateDirectory(alphaLogs);
        Directory.CreateDirectory(betaCache);
        Directory.CreateDirectory(betaLogs);
        var targetLine = "[steam] 192.0.2.40 / - - - [26/Sep/2026:19:00:00 +0000] \"GET /depot/570/chunk/b HTTP/1.1\" 200 12 \"-\" \"test\" \"HIT\" \"-\" \"-\"";
        var keepLine = "[epicgames] 192.0.2.40 / - - - [26/Sep/2026:19:00:01 +0000] \"GET /keep HTTP/1.1\" 200 8 \"-\" \"test\" \"HIT\" \"-\" \"-\"";
        var alphaLog = Path.Combine(alphaLogs, "access.log");
        var betaLog = Path.Combine(betaLogs, "access.log");
        await File.WriteAllLinesAsync(alphaLog, new[] { targetLine, keepLine });
        await File.WriteAllLinesAsync(betaLog, new[] { targetLine, keepLine });

        var settings = new NpgsqlConnectionStringBuilder(connection)
        {
            SearchPath = schema + ",public",
            ApplicationName = "ds-impl-a-log-removal"
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
                    Service = "steam",
                    ClientIp = "192.0.2.40",
                    StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
                    EndTimeUtc = DateTime.UtcNow,
                    CacheHitBytes = 12,
                    IsActive = false,
                    IsEvicted = false,
                    Datasource = name,
                    LastUrl = "/depot/570/chunk/b"
                };
                context.Downloads.Add(download);
                await context.SaveChangesAsync();
                context.LogEntries.Add(new LogEntryRecord
                {
                    DownloadId = download.Id,
                    Service = "steam",
                    Datasource = name,
                    ClientIp = "192.0.2.40",
                    Timestamp = DateTime.UtcNow,
                    Method = "GET",
                    Url = "/depot/570/chunk/b",
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
                ["LanCache:DataSources:1:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();
        var pathResolver = DispatchProxy.Create<IPathResolver, ManagerPathResolverProxy>();
        var pathState = (ManagerPathResolverProxy)(object)pathResolver;
        pathState.Root = runRoot;
        pathState.LogManagerBinary = binary;
        pathState.ReadOnlyDirectory = betaLogs;
        var sources = new DatasourceService(configuration, pathResolver, NullLogger<DatasourceService>.Instance);
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var rust = new RustProcessHelper(NullLogger<RustProcessHelper>.Instance, processes, pathResolver, tracker);
        var rotation = new NginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            configuration,
            processes,
            pathResolver,
            TimeProvider.System);
        var state = DispatchProxy.Create<IStateService, NullReturningProxy>();
        var cacheManager = new CacheManagementService(
            configuration,
            NullLogger<CacheManagementService>.Instance,
            pathResolver,
            rust,
            rotation,
            sources,
            state,
            contexts,
            gameCacheDetectionService: null!,
            tracker,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
            DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(),
            new DatasourceCapabilityService(sources),
            CacheScanGateHarness.Idle());
        var removalLogger = new CapturingLogger<RustLogRemovalService>();
        var removal = new RustLogRemovalService(
            removalLogger,
            pathResolver,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            cacheManager,
            rust,
            rotation,
            contexts,
            sources,
            tracker,
            state);
        var start = typeof(RustLogRemovalService).GetMethod(
            "StartRemovalAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var priorUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        Environment.SetEnvironmentVariable("DATABASE_URL", connectionUrl);
        try
        {
            Assert.True(await (Task<bool>)start.Invoke(removal, ["steam"])!);
            var rewritten = await File.ReadAllTextAsync(alphaLog);
            Assert.False(
                rewritten.Contains("/depot/570/chunk/b", StringComparison.Ordinal),
                string.Join(Environment.NewLine, removalLogger.Entries.Select(entry => entry.Message)));
            Assert.Contains("/keep", rewritten, StringComparison.Ordinal);
            Assert.Contains("/depot/570/chunk/b", await File.ReadAllTextAsync(betaLog), StringComparison.Ordinal);
            await using var finalContext = contexts.CreateDbContext();
            Assert.Equal(2, await finalContext.Downloads.CountAsync());
            Assert.Equal(2, await finalContext.LogEntries.CountAsync());
            Assert.DoesNotContain(await finalContext.Downloads.ToListAsync(), row => row.Datasource == "alpha");
            Assert.Contains(await finalContext.Downloads.ToListAsync(), row => row.Datasource == "beta");
            Assert.Contains(await finalContext.Downloads.ToListAsync(), row => row.Datasource == "retired");

            var alphaDownload = new Download
            {
                Service = "steam",
                ClientIp = "192.0.2.40",
                StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
                EndTimeUtc = DateTime.UtcNow,
                CacheHitBytes = 12,
                IsActive = false,
                IsEvicted = false,
                Datasource = "alpha",
                LastUrl = "/depot/570/chunk/b"
            };
            finalContext.Downloads.Add(alphaDownload);
            await finalContext.SaveChangesAsync();
            finalContext.LogEntries.Add(new LogEntryRecord
            {
                DownloadId = alphaDownload.Id,
                Service = "steam",
                Datasource = "alpha",
                ClientIp = "192.0.2.40",
                Timestamp = DateTime.UtcNow,
                Method = "GET",
                Url = "/depot/570/chunk/b",
                StatusCode = 200,
                BytesServed = 12,
                CacheStatus = "HIT",
                CreatedAt = DateTime.UtcNow
            });
            await finalContext.SaveChangesAsync();
            await finalContext.Database.ExecuteSqlRawAsync(
                """
                CREATE OR REPLACE FUNCTION reject_target_download_delete() RETURNS trigger AS $$
                BEGIN
                    IF OLD."Datasource" = 'alpha' THEN
                        RAISE EXCEPTION 'forced parent delete failure';
                    END IF;
                    RETURN OLD;
                END;
                $$ LANGUAGE plpgsql;
                DROP TRIGGER IF EXISTS reject_target_download_delete ON "Downloads";
                CREATE TRIGGER reject_target_download_delete BEFORE DELETE ON "Downloads"
                FOR EACH ROW EXECUTE FUNCTION reject_target_download_delete();
                """);
            await File.WriteAllLinesAsync(alphaLog, new[] { targetLine, keepLine });
            OperationInfo? terminal = null;
            tracker.OperationTerminal += operation =>
            {
                if (operation.Type == OperationType.LogRemoval)
                {
                    terminal = operation;
                }
            };

            Assert.False(await (Task<bool>)start.Invoke(removal, ["steam"])!);

            for (var attempt = 0; attempt < 50 && terminal is null; attempt++)
            {
                await Task.Delay(20);
            }
            Assert.NotNull(terminal);
            Assert.Equal(OperationStatus.Failed, terminal.Status);
            Assert.Equal(3, await finalContext.Downloads.CountAsync());
            Assert.Equal(3, await finalContext.LogEntries.CountAsync());
            Assert.Equal(3, await finalContext.LogEntries.CountAsync(row => row.DownloadId != null));
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
        public string? ReadOnlyDirectory { get; set; }
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
            if (targetMethod.Name == nameof(IPathResolver.IsDirectoryWritable))
            {
                var path = Assert.IsType<string>(args![0]);
                return !string.Equals(path, ReadOnlyDirectory, StringComparison.OrdinalIgnoreCase);
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
            string label)
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
