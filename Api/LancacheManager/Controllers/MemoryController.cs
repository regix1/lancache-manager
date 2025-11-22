using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for memory diagnostics
/// Handles memory usage statistics and garbage collection information
/// </summary>
[ApiController]
[Route("api/memory")]
public class MemoryController : ControllerBase
{
    private readonly IMemoryManager _memoryManager;
    private readonly ILogger<MemoryController> _logger;

    public MemoryController(IMemoryManager memoryManager, ILogger<MemoryController> logger)
    {
        _memoryManager = memoryManager;
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
            // Use platform-specific memory manager for garbage collection
            // On Linux, this includes malloc_trim to force glibc to return memory to OS
            // On Windows, standard GC is sufficient
            _memoryManager.PerformAggressiveGarbageCollection(_logger);
        }

        var gcMemoryInfo = GC.GetGCMemoryInfo();
        var totalMemory = GC.GetTotalMemory(false);

        // MEMORY LEAK FIX: Dispose Process object to prevent leak
        // Each call to GetCurrentProcess() creates a new object that must be disposed
        using var process = System.Diagnostics.Process.GetCurrentProcess();

        // Calculate managed vs unmanaged memory
        var workingSetBytes = process.WorkingSet64;
        var managedBytes = gcMemoryInfo.HeapSizeBytes;
        var unmanagedBytes = workingSetBytes - managedBytes;

        // Get total system memory
        var totalSystemMemoryBytes = gcMemoryInfo.TotalAvailableMemoryBytes;

        var stats = new
        {
            timestamp = DateTime.UtcNow,
            // System Memory
            totalSystemMemoryMB = totalSystemMemoryBytes / 1024.0 / 1024.0,
            totalSystemMemoryGB = totalSystemMemoryBytes / 1024.0 / 1024.0 / 1024.0,
            // Process Memory
            workingSetMB = workingSetBytes / 1024.0 / 1024.0,
            workingSetGB = workingSetBytes / 1024.0 / 1024.0 / 1024.0,
            managedMB = managedBytes / 1024.0 / 1024.0,
            managedGB = managedBytes / 1024.0 / 1024.0 / 1024.0,
            unmanagedMB = unmanagedBytes / 1024.0 / 1024.0,
            unmanagedGB = unmanagedBytes / 1024.0 / 1024.0 / 1024.0,
            // Managed Memory Details
            totalAllocatedMB = totalMemory / 1024.0 / 1024.0,
            totalAllocatedGB = totalMemory / 1024.0 / 1024.0 / 1024.0,
            heapSizeMB = gcMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0,
            heapSizeGB = gcMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0 / 1024.0,
            fragmentedMB = gcMemoryInfo.FragmentedBytes / 1024.0 / 1024.0,
            fragmentedGB = gcMemoryInfo.FragmentedBytes / 1024.0 / 1024.0 / 1024.0,
            // Process Statistics
            gen0Collections = GC.CollectionCount(0),
            gen1Collections = GC.CollectionCount(1),
            gen2Collections = GC.CollectionCount(2),
            threadCount = process.Threads.Count,
            handleCount = process.HandleCount
        };

        return Ok(stats);
    }
}
