using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Platform;

/// <summary>
/// Windows-specific memory manager
/// Windows memory allocator returns memory to OS promptly, no special handling needed
/// </summary>
public class WindowsMemoryManager : IMemoryManager
{
    private readonly ILogger<WindowsMemoryManager> _logger;

    public WindowsMemoryManager(ILogger<WindowsMemoryManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Performs aggressive garbage collection with Windows-specific optimizations
    /// Windows memory allocator already returns memory promptly, so no additional steps needed
    /// </summary>
    /// <param name="logger">Optional logger override for diagnostics</param>
    public void PerformAggressiveGarbageCollection(ILogger? logger = null)
    {
        var activeLogger = logger ?? _logger;

        // Standard .NET garbage collection pattern
        // 1. Collect managed objects
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        // 2. Wait for finalizers to run (releases unmanaged resources)
        GC.WaitForPendingFinalizers();

        // 3. Collect again to clean up finalized objects
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        // 4. Clear SQLite connection pool to free native memory
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        activeLogger?.LogDebug("Garbage collection completed on Windows");
    }
}
