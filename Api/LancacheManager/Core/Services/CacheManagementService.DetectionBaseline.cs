using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

/// <summary>
/// Games-on-disk freshness baseline. A successful game detection run records current cache
/// mount usage here; the dashboard compares live usage against it to flag the detection
/// summary as out of date. Fully independent of the cache-file scan's baseline: re-running
/// detection re-anchors only this baseline and never touches the Cache Files card.
/// </summary>
public partial class CacheManagementService
{
    private DetectionUsageBaseline? _detectionUsageBaseline;
    private readonly SemaphoreSlim _detectionBaselineLock = new(1, 1);

    private string DetectionBaselineFilePath =>
        Path.Combine(_pathResolver.GetStateDirectory(), "detection_usage_baseline.json");

    /// <summary>
    /// Records current cache mount usage as the games-on-disk freshness baseline.
    /// Called after a successful game detection run. Never throws: a failed capture only
    /// means the staleness flag keeps comparing against the previous baseline.
    /// </summary>
    public async Task CaptureDetectionUsageBaselineAsync()
    {
        try
        {
            var allCachePaths = SelectFullScanCachePaths(
                _datasourceService.GetDatasources(),
                _pathResolver.GetCacheDirectory());
            var usedBytesByMount = OperatingSystemDetector.IsWindows
                ? null
                : GetCacheMountUsage(allCachePaths);
            var usedBytesTotal = usedBytesByMount is { Count: > 0 }
                ? usedBytesByMount.Values.Sum()
                : (await GetCacheInfoAsync()).UsedCacheSize;

            var baseline = new DetectionUsageBaseline
            {
                UsedCacheSizeAtDetection = usedBytesTotal,
                UsedCacheSizeByMountAtDetection = usedBytesByMount?.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    CachePathComparer) ?? new Dictionary<string, long>(CachePathComparer),
                CapturedAtUtc = DateTime.UtcNow
            };

            await _detectionBaselineLock.WaitAsync();
            try
            {
                // In-memory readers observe the new baseline even if persistence fails;
                // the next successful detection run retries the file write.
                _detectionUsageBaseline = baseline;

                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                };
                var json = System.Text.Json.JsonSerializer.Serialize(baseline, options);
                var dir = Path.GetDirectoryName(DetectionBaselineFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(DetectionBaselineFilePath, json);
            }
            finally
            {
                _detectionBaselineLock.Release();
            }

            _logger.LogInformation(
                "Captured detection usage baseline (usedBytes={UsedBytes}, mounts={MountCount})",
                usedBytesTotal, baseline.UsedCacheSizeByMountAtDetection.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture detection usage baseline");
        }
    }

    /// <summary>
    /// True when live cache usage has drifted from the baseline captured at the last
    /// successful game detection run. False when no baseline exists yet, so installs
    /// upgraded before this baseline existed never flag until a detection run records one.
    /// </summary>
    /// <param name="liveUsedCacheSize">
    /// Already-known live used cache size, when the caller has one. When omitted and the
    /// baseline has no per-mount data, current usage is read via <see cref="GetCacheInfoAsync"/>.
    /// </param>
    public async Task<bool> IsDetectionSummaryStaleAsync(long? liveUsedCacheSize = null)
    {
        await LoadDetectionBaselineAsync();
        var baseline = _detectionUsageBaseline;
        if (baseline == null)
        {
            return false;
        }

        var currentUsedBytesByMount = baseline.UsedCacheSizeByMountAtDetection is { Count: > 0 }
            ? ReadCacheMountUsage(baseline.UsedCacheSizeByMountAtDetection.Keys)
            : null;
        var currentUsedBytes = baseline.UsedCacheSizeByMountAtDetection is { Count: > 0 }
            ? 0L
            : liveUsedCacheSize ?? (await GetCacheInfoAsync()).UsedCacheSize;

        return IsDetectionBaselineStale(baseline, currentUsedBytesByMount, currentUsedBytes);
    }

    /// <summary>
    /// Pure staleness decision: per-mount drift when the baseline recorded mounts,
    /// total-usage drift otherwise. Shares the cache-file scan's drift thresholds.
    /// </summary>
    internal static bool IsDetectionBaselineStale(
        DetectionUsageBaseline? baseline,
        IReadOnlyDictionary<string, long>? currentUsedBytesByMount,
        long liveUsedCacheSize)
    {
        if (baseline == null)
        {
            return false;
        }

        if (baseline.UsedCacheSizeByMountAtDetection is { Count: > 0 })
        {
            return IsAnyMountUsageStale(
                baseline.UsedCacheSizeByMountAtDetection,
                currentUsedBytesByMount);
        }

        return CacheScanStaleCalculator.IsAnyScanStale(
            liveUsedCacheSize,
            baseline.UsedCacheSizeAtDetection);
    }

    private async Task LoadDetectionBaselineAsync()
    {
        if (_detectionUsageBaseline != null)
        {
            return;
        }

        try
        {
            if (!File.Exists(DetectionBaselineFilePath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(DetectionBaselineFilePath);
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            };
            _detectionUsageBaseline =
                System.Text.Json.JsonSerializer.Deserialize<DetectionUsageBaseline>(json, options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load detection usage baseline from {FilePath} - staleness stays unflagged until the next detection run",
                DetectionBaselineFilePath);
            _detectionUsageBaseline = null;
        }
    }

    /// <summary>
    /// Persisted model for the cache usage recorded at the last successful detection run.
    /// </summary>
    public class DetectionUsageBaseline
    {
        public long UsedCacheSizeAtDetection { get; set; }
        public Dictionary<string, long> UsedCacheSizeByMountAtDetection { get; set; } = new();
        public DateTime CapturedAtUtc { get; set; }
    }
}
