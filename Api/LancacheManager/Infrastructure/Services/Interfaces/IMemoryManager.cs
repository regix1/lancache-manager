namespace LancacheManager.Infrastructure.Services.Interfaces;

/// <summary>
/// Interface for platform-specific memory management operations
/// Handles garbage collection and platform-specific memory cleanup
/// </summary>
public interface IMemoryManager
{
    /// <summary>
    /// Performs aggressive garbage collection with platform-specific optimizations
    /// On Windows: Standard GC + SQLite pool clearing
    /// On Linux: Standard GC + SQLite pool clearing + malloc_trim to return memory to OS
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics</param>
    void PerformAggressiveGarbageCollection(ILogger? logger = null);
}
