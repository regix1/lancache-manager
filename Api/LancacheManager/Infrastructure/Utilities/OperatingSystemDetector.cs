using System.Runtime.InteropServices;

namespace LancacheManager.Infrastructure.Utilities;

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
    /// Gets a human-readable description of the current operating system
    /// </summary>
    public static string Description => RuntimeInformation.OSDescription;
}