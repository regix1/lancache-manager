using System.Reflection;
using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
            // The nginx log reopen and the detection refresh both run after the Rust phase, which
            // this test never completes.
            nginxLogRotationService: null!,
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
}
