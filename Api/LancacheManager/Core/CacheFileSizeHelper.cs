namespace LancacheManager.Core;

/// <summary>
/// Reads on-disk cache file sizes for deduplicated cache totals.
/// </summary>
public static class CacheFileSizeHelper
{
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    public static ulong TryGetFileSize(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            return (ulong)new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }
}
