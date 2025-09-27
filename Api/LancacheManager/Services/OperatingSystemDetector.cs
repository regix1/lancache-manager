using System.Runtime.InteropServices;

namespace LancacheManager.Services;

/// <summary>
/// Modern operating system detection service using best practices for .NET 2024
/// Uses RuntimeInformation.IsOSPlatform() which is the recommended approach
/// </summary>
public static class OperatingSystemDetector
{
    /// <summary>
    /// Checks if the current operating system is Windows
    /// </summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Checks if the current operating system is Linux
    /// </summary>
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Checks if the current operating system is macOS
    /// </summary>
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>
    /// Gets a human-readable description of the current operating system
    /// </summary>
    public static string Description => RuntimeInformation.OSDescription;

    /// <summary>
    /// Gets the current platform as an OSPlatform enum
    /// </summary>
    public static OSPlatform CurrentPlatform
    {
        get
        {
            if (IsWindows) return OSPlatform.Windows;
            if (IsLinux) return OSPlatform.Linux;
            if (IsMacOS) return OSPlatform.OSX;

            throw new PlatformNotSupportedException($"Unsupported operating system: {Description}");
        }
    }

    /// <summary>
    /// Extension method for cleaner OS checks
    /// </summary>
    public static bool IsRunning(this OSPlatform platform)
    {
        return RuntimeInformation.IsOSPlatform(platform);
    }
}