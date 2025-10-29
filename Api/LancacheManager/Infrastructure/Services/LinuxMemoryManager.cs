using System.Runtime.InteropServices;
using LancacheManager.Infrastructure.Services.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Linux-specific memory manager that includes malloc_trim and mallopt for forcing glibc to return memory to OS
/// Also configures glibc malloc parameters to reduce fragmentation
/// </summary>
public class LinuxMemoryManager : IMemoryManager
{
    private readonly ILogger<LinuxMemoryManager> _logger;
    private static bool _mallocConfigured = false;
    private static readonly object _mallocConfigLock = new object();

    // mallopt parameter constants from malloc.h
    private const int M_TRIM_THRESHOLD = -1;  // Minimum size for top chunk to trigger trimming
    private const int M_ARENA_MAX = -8;       // Maximum number of arenas

    /// <summary>
    /// P/Invoke declaration for malloc_trim from glibc (Linux only)
    /// This forces the glibc allocator to return freed memory to the OS
    /// CRITICAL for Linux: Without this, memory stays allocated in glibc's arena
    /// even after GC.Collect() releases it from .NET's perspective
    /// </summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int malloc_trim(int pad);

    /// <summary>
    /// P/Invoke declaration for mallopt from glibc (Linux only)
    /// Used to configure malloc behavior at runtime
    /// </summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int mallopt(int param, int value);

    public LinuxMemoryManager(ILogger<LinuxMemoryManager> logger)
    {
        _logger = logger;

        // Configure glibc malloc on first initialization
        ConfigureMallocSettings();
    }

    /// <summary>
    /// Configures glibc malloc settings to reduce memory fragmentation
    /// This is critical for .NET applications on Linux
    /// See: https://github.com/dotnet/runtime/issues/90163
    /// </summary>
    private void ConfigureMallocSettings()
    {
        lock (_mallocConfigLock)
        {
            if (_mallocConfigured)
                return;

            try
            {
                // Set M_TRIM_THRESHOLD to 128KB (131072 bytes)
                // This prevents glibc from dynamically resizing the trim threshold
                // which causes memory fragmentation. Fixed value ensures consistent behavior.
                // Without this, memory can grow significantly and not be released.
                var trimResult = mallopt(M_TRIM_THRESHOLD, 131072);
                _logger.LogInformation(
                    "Linux malloc M_TRIM_THRESHOLD set to 128KB (131072 bytes). Result: {Result}",
                    trimResult == 1 ? "Success" : "Failed");

                // Set M_ARENA_MAX to 4
                // Default is 8 * cores, which can create up to 32 arenas on a quad-core
                // More arenas = more fragmentation and memory usage
                // Reducing to 4 balances performance and memory efficiency
                var arenaResult = mallopt(M_ARENA_MAX, 4);
                _logger.LogInformation(
                    "Linux malloc M_ARENA_MAX set to 4. Result: {Result}",
                    arenaResult == 1 ? "Success" : "Failed");

                _mallocConfigured = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to configure malloc settings - memory management may be suboptimal");
            }
        }
    }

    /// <summary>
    /// Performs aggressive garbage collection with Linux-specific optimizations
    /// Includes malloc_trim to force glibc to return memory to OS
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

        // 5. Linux-specific: Force glibc to return memory to OS
        try
        {
            // malloc_trim(0) tells glibc to return all possible memory to the OS
            // This is critical on Linux where glibc's allocator caches freed memory
            // Combined with our M_TRIM_THRESHOLD setting, this should be very effective
            var freedBytes = malloc_trim(0);
            activeLogger?.LogDebug("malloc_trim(0) returned {FreedBytes} on Linux", freedBytes);
        }
        catch (Exception ex)
        {
            // malloc_trim might not be available on all Linux systems
            // Log but don't fail if it's not available
            activeLogger?.LogWarning(ex, "Failed to call malloc_trim on Linux - memory may not be fully released");
        }
    }
}
