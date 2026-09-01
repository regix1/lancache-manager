using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for memory diagnostics
/// Handles memory usage statistics and garbage collection information
/// </summary>
[ApiController]
[Route("api/memory")]
[Authorize(Policy = "AccountHolder")]
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
    /// Gets current memory usage for debugging memory leaks.
    /// </summary>
    /// <remarks>
    /// Requires API key authentication for security.
    /// </remarks>
    /// <param name="forceGC">Optional: Force garbage collection before reading stats (use for diagnostics only)</param>
    [HttpGet]
    [ProducesResponseType(typeof(MemoryStatsResponse), StatusCodes.Status200OK)]
    public ActionResult<MemoryStatsResponse> GetMemoryStats([FromQuery] bool forceGC = false)
    {
        if (forceGC)
        {
            _logger.LogWarning("Forcing garbage collection - this should only be used for diagnostics");
            // Use platform-specific memory manager for garbage collection
            // On Linux, this includes malloc_trim to force glibc to return memory to OS
            // On Windows, standard GC is sufficient
            _memoryManager.CollectGarbage(_logger);
        }

        var gcMemoryInfo = GC.GetGCMemoryInfo();
        var totalMemory = GC.GetTotalMemory(false);

        // MEMORY LEAK FIX: Dispose Process object to prevent leak
        // Each call to GetCurrentProcess() creates a new object that must be disposed
        using var process = System.Diagnostics.Process.GetCurrentProcess();

        // Calculate managed vs unmanaged memory.
        // HeapSizeBytes is the heap as of the last collection and does not move between
        // collections, so the live allocated figure is the one reported as managed.
        // Unmanaged is what is resident beyond the heap the GC has committed; the two counters
        // come from different sources and a heap that is committed but paged out reads larger
        // than the working set, so the difference is floored at zero instead of going negative.
        var workingSetBytes = process.WorkingSet64;
        var managedBytes = totalMemory;
        var committedBytes = gcMemoryInfo.TotalCommittedBytes;
        var unmanagedBytes = Math.Max(0, workingSetBytes - committedBytes);

        // Get total system memory
        var totalSystemMemoryBytes = gcMemoryInfo.TotalAvailableMemoryBytes;

        var stats = new MemoryStatsResponse
        {
            Timestamp = DateTime.UtcNow,
            // System Memory
            TotalSystemMemoryMB = totalSystemMemoryBytes / 1024.0 / 1024.0,
            TotalSystemMemoryGB = totalSystemMemoryBytes / 1024.0 / 1024.0 / 1024.0,
            // Process Memory
            WorkingSetMB = workingSetBytes / 1024.0 / 1024.0,
            WorkingSetGB = workingSetBytes / 1024.0 / 1024.0 / 1024.0,
            ManagedMB = managedBytes / 1024.0 / 1024.0,
            ManagedGB = managedBytes / 1024.0 / 1024.0 / 1024.0,
            UnmanagedMB = unmanagedBytes / 1024.0 / 1024.0,
            UnmanagedGB = unmanagedBytes / 1024.0 / 1024.0 / 1024.0,
            // Managed Memory Details
            TotalAllocatedMB = totalMemory / 1024.0 / 1024.0,
            TotalAllocatedGB = totalMemory / 1024.0 / 1024.0 / 1024.0,
            HeapSizeMB = gcMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0,
            HeapSizeGB = gcMemoryInfo.HeapSizeBytes / 1024.0 / 1024.0 / 1024.0,
            CommittedMB = committedBytes / 1024.0 / 1024.0,
            CommittedGB = committedBytes / 1024.0 / 1024.0 / 1024.0,
            FragmentedMB = gcMemoryInfo.FragmentedBytes / 1024.0 / 1024.0,
            FragmentedGB = gcMemoryInfo.FragmentedBytes / 1024.0 / 1024.0 / 1024.0,
            // Process Statistics
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            ThreadCount = process.Threads.Count,
            HandleCount = process.HandleCount
        };

        return Ok(stats);
    }
}
