namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Utility methods for formatting values for display
/// </summary>
public static class FormattingUtils
{
    /// <summary>
    /// Formats bytes into human-readable string (e.g., "4 TB", "500 GB")
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
