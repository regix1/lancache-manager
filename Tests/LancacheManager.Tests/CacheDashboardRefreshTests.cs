using LancacheManager.Core;
using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public sealed class CacheDashboardRefreshTests
{
    [Fact]
    public void SelectFullScanCachePaths_DeduplicatesExactAliasesAndKeepsNestedDatasourceRoots()
    {
        var testRoot = CreateTempDirectory();
        try
        {
            var alpha = Directory.CreateDirectory(Path.Combine(testRoot, "alpha")).FullName;
            var beta = Directory.CreateDirectory(Path.Combine(testRoot, "beta")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(alpha, "nested")).FullName;
            var duplicateAlpha = OperatingSystem.IsWindows() ? alpha.ToUpperInvariant() : alpha;
            var datasources = new List<ResolvedDatasource>
            {
                new() { CachePath = alpha + Path.DirectorySeparatorChar, Enabled = true },
                new() { CachePath = beta, Enabled = true },
                new() { CachePath = duplicateAlpha, Enabled = true },
                new() { CachePath = nested, Enabled = true },
                new() { CachePath = Path.Combine(testRoot, "disabled"), Enabled = false }
            };

            var paths = CacheManagementService.SelectFullScanCachePaths(
                datasources,
                Path.Combine(testRoot, "legacy"));

            Assert.Equal([alpha, beta, nested], paths);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void SelectFullScanCachePaths_NoEnabledDatasource_UsesLegacyRoot()
    {
        var testRoot = CreateTempDirectory();
        try
        {
            var legacy = Directory.CreateDirectory(Path.Combine(testRoot, "legacy")).FullName;
            var datasources = new List<ResolvedDatasource>
            {
                new() { CachePath = Path.Combine(testRoot, "disabled"), Enabled = false }
            };

            var paths = CacheManagementService.SelectFullScanCachePaths(datasources, legacy);

            Assert.Equal([legacy], paths);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void SelectFullScanCachePaths_ResolvesSymbolicLinksToTheSamePhysicalRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = CreateTempDirectory();
        try
        {
            var physicalRoot = Directory.CreateDirectory(Path.Combine(testRoot, "physical")).FullName;
            var symbolicRoot = Path.Combine(testRoot, "symbolic");
            Directory.CreateSymbolicLink(symbolicRoot, physicalRoot);
            var datasources = new List<ResolvedDatasource>
            {
                new() { CachePath = symbolicRoot, Enabled = true },
                new() { CachePath = physicalRoot, Enabled = true }
            };

            var paths = CacheManagementService.SelectFullScanCachePaths(datasources, testRoot);

            Assert.Equal([physicalRoot], paths);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void AggregateCacheSizeResponses_SumsAllDatasourceResults()
    {
        var results = new List<CacheSizeResponse>
        {
            CacheSizeResult(bytes: 1_000, files: 3, directories: 2, hexDirectories: 1, seconds: 2),
            CacheSizeResult(bytes: 2_000, files: 5, directories: 4, hexDirectories: 2, seconds: 3)
        };

        var aggregate = CacheManagementService.AggregateCacheSizeResponses(results);

        Assert.Equal(3_000, aggregate.TotalBytes);
        Assert.Equal(8, aggregate.TotalFiles);
        Assert.Equal(6, aggregate.TotalDirectories);
        Assert.Equal(3, aggregate.HexDirectories);
        Assert.Equal(5, aggregate.EstimatedDeletionTimes.PreserveSeconds);
        Assert.Equal("5 seconds", aggregate.EstimatedDeletionTimes.PreserveFormatted);
    }

    [Fact]
    public void RefreshCachedScanUsageBaseline_ClearsDriftWithoutCreatingNewScanData()
    {
        const long gibibyte = 1024L * 1024 * 1024;
        const long oldUsage = 100 * gibibyte;
        const long currentUsage = 110 * gibibyte;
        var scanTimestamp = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var cachedScan = new CacheManagementService.CachedCacheScan
        {
            ScanResult = new CacheSizeResponse { TotalFiles = 42, TotalBytes = 12_345 },
            UsedCacheSizeAtScan = oldUsage,
            ScannedAtUtc = scanTimestamp
        };

        Assert.True(CacheScanStaleCalculator.IsAnyScanStale(currentUsage, cachedScan.UsedCacheSizeAtScan));

        CacheManagementService.RefreshCachedScanUsageBaseline(cachedScan, currentUsage);

        Assert.False(CacheScanStaleCalculator.IsAnyScanStale(currentUsage, cachedScan.UsedCacheSizeAtScan));
        Assert.Equal(scanTimestamp, cachedScan.ScannedAtUtc);
        Assert.Equal(42, cachedScan.ScanResult.TotalFiles);
    }

    [Fact]
    public void IsAnyMountUsageStale_DetectsDriftOnNonDefaultMount()
    {
        const long gibibyte = 1024L * 1024 * 1024;
        var baseline = new Dictionary<string, long>
        {
            ["/default"] = 100 * gibibyte,
            ["/secondary"] = 200 * gibibyte
        };
        var current = new Dictionary<string, long>
        {
            ["/default"] = baseline["/default"],
            ["/secondary"] = 210 * gibibyte
        };

        Assert.True(CacheManagementService.IsAnyMountUsageStale(baseline, current));
    }

    [Fact]
    public void CacheInfo_NoScanAndCompletedEmptyScanAreDistinguishable()
    {
        var noScan = new CacheInfo();
        var completedEmptyScan = new CacheInfo { HasCacheScan = true, TotalFiles = 0, CacheScanTotalBytes = 0 };

        Assert.False(noScan.HasCacheScan);
        Assert.True(completedEmptyScan.HasCacheScan);
        Assert.Equal(noScan.TotalFiles, completedEmptyScan.TotalFiles);
    }

    [Fact]
    public void GameDetectionComplete_InvalidatesDetectionAcrossAllDashboardRanges()
    {
        var notificationSource = ReadSource("Infrastructure", "Services", "SignalRNotificationService.cs");
        var batchSource = ReadSource("Core", "Services", "DashboardBatchService.cs");

        Assert.Contains(
            "GetRequiredService<IDashboardBatchService>().InvalidateDetectionCache()",
            notificationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ":{liveCacheGeneration}:{detectionCacheGeneration}",
            batchSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardBatch_CapturesGenerationBeforeComputeAndGuardsCacheStore()
    {
        var source = ReadSource("Core", "Services", "DashboardBatchService.cs");
        var captureIndex = source.IndexOf(
            "var detectionCacheGeneration = Volatile.Read",
            StringComparison.Ordinal);
        var computeIndex = source.IndexOf("GetEventDownloadIdsAsync", StringComparison.Ordinal);
        var guardIndex = source.IndexOf("var generationsAreCurrent", StringComparison.Ordinal);
        var storeIndex = source.IndexOf("_memoryCache.Set(cacheKey, response", StringComparison.Ordinal);

        Assert.True(captureIndex >= 0 && captureIndex < computeIndex);
        Assert.True(guardIndex > computeIndex && guardIndex < storeIndex);
    }

    [Fact]
    public void FullDetection_RefreshesCacheScanBaselineBeforeCompletion()
    {
        var source = ReadSource("Core", "Services", "GameCacheDetectionService.cs");

        Assert.Contains("if (!incremental)", source, StringComparison.Ordinal);
        Assert.Contains(
            "RefreshCacheScanStalenessBaselineAsync(cancellationToken)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SystemConfig_ExposesEffectiveSchemeOverrideAndDenialReason()
    {
        var source = ReadSource("Controllers", "SystemController.cs");

        Assert.Contains("SchemeOverride = ds.SchemeOverride.ToWireValue()", source, StringComparison.Ordinal);
        Assert.Contains("CacheKeyScheme = DatasourceCapabilityService.GetSchemeWireValue(capabilities)", source, StringComparison.Ordinal);
        Assert.Contains("CapabilityDenialReason = capabilities.DenialReason", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheScan_RemainsScheduledOnlyWithNoInitialScanService()
    {
        var servicesDirectory = GetRepositoryPath("Api", "LancacheManager", "Infrastructure", "Services");
        var initialScanServices = Directory.GetFiles(servicesDirectory, "*Initial*Cache*Scan*.cs");
        var scheduledSource = ReadSource("Infrastructure", "Services", "CacheSizeScanScheduledService.cs");

        Assert.Empty(initialScanServices);
        Assert.Contains("public override bool DefaultRunOnStartup => false;", scheduledSource, StringComparison.Ordinal);
    }

    private static CacheSizeResponse CacheSizeResult(
        long bytes,
        long files,
        long directories,
        int hexDirectories,
        double seconds)
    {
        return new CacheSizeResponse
        {
            TotalBytes = bytes,
            TotalFiles = files,
            TotalDirectories = directories,
            HexDirectories = hexDirectories,
            ScanDurationMs = 10,
            EstimatedDeletionTimes = new EstimatedDeletionTimes
            {
                PreserveSeconds = seconds,
                FullSeconds = seconds,
                RsyncSeconds = seconds
            }
        };
    }

    private static string ReadSource(params string[] pathSegments)
        => File.ReadAllText(GetRepositoryPath(["Api", "LancacheManager", .. pathSegments]));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "lancache-manager-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetRepositoryPath(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return Path.Combine([root, .. pathSegments]);
    }
}
