using System.Diagnostics;

namespace LancacheManager.Services;

/// <summary>
/// Helper methods for configuring Rust process execution
/// </summary>
public static class RustProcessHelper
{
    /// <summary>
    /// Configure common environment variables for Rust processes
    /// Includes timezone (TZ) and memory limit (RUST_MAX_MEMORY_MB)
    /// </summary>
    public static void ConfigureEnvironmentVariables(this ProcessStartInfo startInfo, ILogger? logger = null)
    {
        // Pass TZ environment variable to Rust processor so it uses the correct timezone
        var tz = Environment.GetEnvironmentVariable("TZ");
        if (!string.IsNullOrEmpty(tz))
        {
            startInfo.EnvironmentVariables["TZ"] = tz;
            logger?.LogDebug($"Passing TZ={tz} to Rust processor");
        }

        // Pass memory limit environment variable to Rust processor
        var maxMemoryMb = Environment.GetEnvironmentVariable("RUST_MAX_MEMORY_MB");
        if (!string.IsNullOrEmpty(maxMemoryMb))
        {
            startInfo.EnvironmentVariables["RUST_MAX_MEMORY_MB"] = maxMemoryMb;
            logger?.LogDebug($"Passing RUST_MAX_MEMORY_MB={maxMemoryMb} to Rust processor");
        }
    }
}
