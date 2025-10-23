using Microsoft.AspNetCore.Mvc;
using LancacheManager.Security;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
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

        // Calculate managed vs unmanaged memory
        var workingSetBytes = process.WorkingSet64;
        var managedBytes = gcMemoryInfo.HeapSizeBytes;
        var unmanagedBytes = workingSetBytes - managedBytes;

        // Get total system memory
        var totalSystemMemoryBytes = gcMemoryInfo.TotalAvailableMemoryBytes;

        var stats = new
        {
            timestamp = DateTime.UtcNow,
            totalSystemMemoryBytes = totalSystemMemoryBytes,
            totalSystemMemoryMB = totalSystemMemoryBytes / 1024.0 / 1024.0,
            totalSystemMemoryGB = totalSystemMemoryBytes / 1024.0 / 1024.0 / 1024.0,
            workingSetBytes = workingSetBytes,
            workingSetMB = workingSetBytes / 1024.0 / 1024.0,
            workingSetGB = workingSetBytes / 1024.0 / 1024.0 / 1024.0,
            managedBytes = managedBytes,
            managedMB = managedBytes / 1024.0 / 1024.0,
            managedGB = managedBytes / 1024.0 / 1024.0 / 1024.0,
            unmanagedBytes = unmanagedBytes,
            unmanagedMB = unmanagedBytes / 1024.0 / 1024.0,
            unmanagedGB = unmanagedBytes / 1024.0 / 1024.0 / 1024.0,
            totalAllocatedBytes = totalMemory,
            totalAllocatedMB = totalMemory / 1024.0 / 1024.0,
            totalAllocatedGB = totalMemory / 1024.0 / 1024.0 / 1024.0,
            heapSizeBytes = gcMemoryInfo.HeapSizeBytes,
            heapSizeMB = gcMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0,
            heapSizeGB = gcMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0 / 1024.0,
            fragmentedBytes = gcMemoryInfo.FragmentedBytes,
            fragmentedMB = gcMemoryInfo.FragmentedBytes / 1024.0 / 1024.0,
            fragmentedGB = gcMemoryInfo.FragmentedBytes / 1024.0 / 1024.0 / 1024.0,
            gen0Collections = GC.CollectionCount(0),
            gen1Collections = GC.CollectionCount(1),
            gen2Collections = GC.CollectionCount(2),
            threadCount = process.Threads.Count,
            handleCount = process.HandleCount
        };

        return Ok(stats);
    }
}
