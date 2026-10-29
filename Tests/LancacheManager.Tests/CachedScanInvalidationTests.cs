using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A cache clear or removal invalidates the persisted cache-size scan. A dashboard read that was
/// already loading the scan file when that happened must not put the old scan back.
/// </summary>
public sealed class CachedScanInvalidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lm-cached-scan-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AnInvalidationDuringALoad_IsNotUndoneByThatLoadAsync()
    {
        var paths = new AuthCredentialFormatTests.TempDirPathResolver(_root);
        var service = CacheFileCountContractTests.NewServiceForCountState(pathResolver: paths);
        var scanFile = Path.Combine(paths.GetStateDirectory(), "cached_cache_scan.json");
        Directory.CreateDirectory(Path.GetDirectoryName(scanFile)!);
        await File.WriteAllTextAsync(scanFile, LargeScanJson());

        // The file is large enough that the read is still running when the invalidation arrives.
        var load = service.GetStaleCachedSizeResultAsync();
        Assert.False(load.IsCompleted, "the load finished before the invalidation could overlap it");
        await Task.Run(service.InvalidateCachedScan);
        await load;

        Assert.Null(await service.GetStaleCachedSizeResultAsync());
        Assert.False(File.Exists(scanFile));
    }

    private static string LargeScanJson()
    {
        var roots = new Dictionary<string, CacheSizeResponse>();
        for (var index = 0; index < 60_000; index++)
        {
            roots[$"/cache/root-{index:D6}"] = new CacheSizeResponse
            {
                TotalBytes = index,
                TotalFiles = index,
                FormattedSize = "1 B",
                Timestamp = DateTime.UtcNow
            };
        }

        var scan = new CacheManagementService.CachedCacheScan
        {
            ScanResult = new CacheSizeResponse { TotalBytes = 1234, TotalFiles = 5, FormattedSize = "1 KB" },
            UsedCacheSizeAtScan = 1234,
            RootResults = roots,
            ScannedAtUtc = DateTime.UtcNow
        };
        return JsonSerializer.Serialize(scan, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by the process must not fail the test.
        }
    }
}
