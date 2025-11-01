using System.Diagnostics;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services.Interfaces;

namespace LancacheManager.Middleware;

/// <summary>
/// Middleware to trigger garbage collection based on memory usage
/// Only runs GC when process memory exceeds threshold to clean up unmanaged SQLite memory
/// </summary>
public class GcMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GcMiddleware> _logger;
    private readonly SettingsRepository _gcSettingsService;
    private readonly IMemoryManager _memoryManager;
    private readonly IConfiguration _configuration;
    private static DateTime _lastGcTime = DateTime.MinValue;
    private static readonly object _gcLock = new object();

    public GcMiddleware(RequestDelegate next, ILogger<GcMiddleware> logger, SettingsRepository gcSettingsService, IMemoryManager memoryManager, IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _gcSettingsService = gcSettingsService;
        _memoryManager = memoryManager;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        // Check if GC management is enabled at all
        var gcManagementEnabled = _configuration.GetValue<bool>("Optimizations:EnableGarbageCollectionManagement", false);
        if (!gcManagementEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;

        // Get current settings
        var (memoryThresholdBytes, minTimeBetweenChecks, onPageLoadOnly, disabled) = _gcSettingsService.GetComputedSettings();

        // If GC is disabled, skip all checks
        if (disabled)
        {
            return;
        }

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

        // Only run GC if memory exceeds threshold
        // Memory threshold is respected in all modes, including OnPageLoad
        var shouldRunGc = workingSetBytes > memoryThresholdBytes;

        if (shouldRunGc)
        {
            lock (_gcLock)
            {
                // Double-check after acquiring lock
                process.Refresh();
                workingSetBytes = process.WorkingSet64;
                workingSetMB = workingSetBytes / (1024.0 * 1024.0);

                shouldRunGc = workingSetBytes > memoryThresholdBytes;

                if (shouldRunGc && now - _lastGcTime >= minTimeBetweenChecks)
                {
                    // Use platform-specific memory manager for garbage collection
                    // On Linux, this includes malloc_trim to force glibc to return memory to OS
                    // On Windows, standard GC + SQLite pool clearing is sufficient
                    _memoryManager.PerformAggressiveGarbageCollection(_logger);

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
