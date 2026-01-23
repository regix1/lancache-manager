namespace LancacheManager.Infrastructure.Extensions;

/// <summary>
/// Extension methods for file system operations
/// </summary>
public static class FileSystemExtensions
{
    /// <summary>
    /// Ensures the directory for a file path exists, creating it if necessary.
    /// </summary>
    public static void EnsureDirectoryExists(this string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// Safely reads all text from a file, returning null if file doesn't exist.
    /// </summary>
    public static string? SafeReadAllText(this string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Safely writes text to a file, creating directory if needed.
    /// </summary>
    public static bool SafeWriteAllText(this string filePath, string content)
    {
        try
        {
            filePath.EnsureDirectoryExists();
            File.WriteAllText(filePath, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Safely deletes a file if it exists.
    /// </summary>
    public static bool SafeDelete(this string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the file size in bytes, or null if file doesn't exist.
    /// </summary>
    public static long? GetFileSizeOrNull(this string filePath)
    {
        try
        {
            return File.Exists(filePath) ? new FileInfo(filePath).Length : null;
        }
        catch
        {
            return null;
        }
    }
}
