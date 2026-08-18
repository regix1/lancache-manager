namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Interface for platform-specific memory management operations
/// Handles garbage collection and platform-specific memory cleanup
/// </summary>
public interface IMemoryManager
{
    /// <summary>
    /// Performs aggressive garbage collection with platform-specific optimizations
    /// On Windows: Standard GC + Npgsql pool clearing
    /// On Linux: Standard GC + Npgsql pool clearing + malloc_trim to return memory to OS
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics</param>
    void CollectGarbage(ILogger? logger = null);
}
