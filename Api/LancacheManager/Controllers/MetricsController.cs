using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(IConfiguration configuration, ILogger<MetricsController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Get metrics endpoint security status
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var requiresAuth = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);

        return Ok(new
        {
            requiresAuthentication = requiresAuth,
            endpoint = "/metrics",
            authMethod = requiresAuth ? "X-Api-Key header required" : "Public access"
        });
    }

    /// <summary>
    /// Get current memory usage for debugging memory leaks
    /// </summary>
    [HttpGet("memory")]
    public IActionResult GetMemoryStats()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var gcMemoryInfo = GC.GetGCMemoryInfo();
        var totalMemory = GC.GetTotalMemory(false);
        var process = System.Diagnostics.Process.GetCurrentProcess();

        var stats = new
        {
            timestamp = DateTime.UtcNow,
            totalAllocatedBytes = totalMemory,
            totalAllocatedMB = totalMemory / 1024.0 / 1024.0,
            workingSetBytes = process.WorkingSet64,
            workingSetMB = process.WorkingSet64 / 1024.0 / 1024.0,
            privateMemoryBytes = process.PrivateMemorySize64,
            privateMemoryMB = process.PrivateMemorySize64 / 1024.0 / 1024.0,
            gen0Collections = GC.CollectionCount(0),
            gen1Collections = GC.CollectionCount(1),
            gen2Collections = GC.CollectionCount(2),
            heapSizeBytes = gcMemoryInfo.HeapSizeBytes,
            heapSizeMB = gcMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0,
            fragmentedBytes = gcMemoryInfo.FragmentedBytes,
            fragmentedMB = gcMemoryInfo.FragmentedBytes / 1024.0 / 1024.0
        };

        _logger.LogInformation("Memory Stats: WorkingSet={WorkingSetMB:F2}MB, Heap={HeapMB:F2}MB, Fragmented={FragmentedMB:F2}MB",
            stats.workingSetMB, stats.heapSizeMB, stats.fragmentedMB);

        return Ok(stats);
    }
}
