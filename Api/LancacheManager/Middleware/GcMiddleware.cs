using System.Diagnostics;
using LancacheManager.Services;
using Microsoft.Data.Sqlite;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware to trigger garbage collection based on memory usage
/// Only runs GC when process memory exceeds threshold to clean up unmanaged SQLite memory
/// </summary>
public class GcMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GcMiddleware> _logger;
    private readonly GcSettingsService _gcSettingsService;
    private static DateTime _lastGcTime = DateTime.MinValue;
    private static readonly object _gcLock = new object();

    public GcMiddleware(RequestDelegate next, ILogger<GcMiddleware> logger, GcSettingsService gcSettingsService)
    {
        _next = next;
        _logger = logger;
        _gcSettingsService = gcSettingsService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        var now = DateTime.UtcNow;

        // Get current settings
        var (memoryThresholdBytes, minTimeBetweenChecks, onPageLoadOnly) = _gcSettingsService.GetComputedSettings();

        // Only check memory periodically to avoid performance impact
        if (now - _lastGcTime < minTimeBetweenChecks)
        {
            return;
        }

        // If OnPageLoad mode, only trigger on actual page loads (non-API requests)
        if (onPageLoadOnly)
        {
            var isPageLoad = IsPageLoadRequest(context);
            if (!isPageLoad)
            {
                return;
            }
        }

        // Get current process memory usage
        var process = Process.GetCurrentProcess();
        var workingSetBytes = process.WorkingSet64;
        var workingSetMB = workingSetBytes / (1024.0 * 1024.0);

        // In OnPageLoad mode, always run GC on page load regardless of threshold
        // In other modes, only run if memory exceeds threshold
        var shouldRunGc = onPageLoadOnly || (workingSetBytes > memoryThresholdBytes);

        if (shouldRunGc)
        {
            lock (_gcLock)
            {
                // Double-check after acquiring lock
                process.Refresh();
                workingSetBytes = process.WorkingSet64;
                workingSetMB = workingSetBytes / (1024.0 * 1024.0);

                shouldRunGc = onPageLoadOnly || (workingSetBytes > memoryThresholdBytes);

                if (shouldRunGc && now - _lastGcTime >= minTimeBetweenChecks)
                {
                    // Force generation 2 collection to clean up unmanaged SQLite memory
                    // Using the proper pattern for releasing unmanaged resources:
                    // 1. Collect managed objects
                    // 2. Wait for finalizers to run (releases unmanaged resources)
                    // 3. Collect again to clean up finalized objects
                    // 4. Clear SQLite connection pool to release native memory
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);

                    // CRITICAL: Clear SQLite connection pool to free native memory
                    // This forces any pooled connections to be discarded and their unmanaged memory released
                    SqliteConnection.ClearAllPools();

                    _lastGcTime = DateTime.UtcNow;

                    // Log memory before and after
                    process.Refresh();
                    var afterGcMB = process.WorkingSet64 / (1024.0 * 1024.0);
                    _logger.LogInformation("GC triggered at {BeforeMB:F0}MB, after GC: {AfterMB:F0}MB (freed {FreedMB:F0}MB)",
                        workingSetMB, afterGcMB, workingSetMB - afterGcMB);
                }
            }
        }
    }

    private bool IsPageLoadRequest(HttpContext context)
    {
        // Only trigger on GET requests
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path.Value ?? "";

        // Don't trigger on API calls, SignalR hubs, metrics, or health checks
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check if this is a browser page load (not an API call or asset request)
        // Browser page loads send Accept: text/html
        var acceptHeader = context.Request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Exclude common asset extensions
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".js" || extension == ".css" || extension == ".map" ||
            extension == ".png" || extension == ".jpg" || extension == ".svg" ||
            extension == ".ico" || extension == ".woff" || extension == ".woff2" ||
            extension == ".json")
        {
            return false;
        }

        return false;
    }
}
