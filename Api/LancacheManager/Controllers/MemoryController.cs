using Microsoft.AspNetCore.Mvc;
using LancacheManager.Security;

namespace LancacheManager.Controllers;

[ApiController]
[Route("[controller]")]
public class MemoryController : ControllerBase
{
    private readonly ILogger<MemoryController> _logger;

    public MemoryController(ILogger<MemoryController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get current memory usage for debugging memory leaks
    /// Requires API key authentication for security
    /// </summary>
    /// <param name="forceGC">Optional: Force garbage collection before reading stats (use for diagnostics only)</param>
    [HttpGet]
    [RequireAuth]
    public IActionResult GetMemoryStats([FromQuery] bool forceGC = false)
    {
        if (forceGC)
        {
            _logger.LogWarning("Forcing garbage collection - this should only be used for diagnostics");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

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
