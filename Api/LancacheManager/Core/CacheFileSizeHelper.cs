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

        // Cache paths from the Rust scans are already canonical ("/cache/cache/ab/cd/<hash>").
        // Path.GetFullPath returns a string-equal result for those but allocates a fresh string
        // every call - and the disk-summary refresh normalizes millions of paths several times
        // each. Only shapes the fast-path predicate can't vouch for fall through to GetFullPath.
        if (IsAlreadyNormalized(path))
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

    /// <summary>
    /// True only for rooted forward-slash paths with no empty, ".", or ".." segments and no
    /// trailing separator - exactly the shapes for which Path.GetFullPath is the identity.
    /// Windows-style paths (drive letters, backslashes) always return false and take the
    /// GetFullPath route unchanged.
    /// </summary>
    private static bool IsAlreadyNormalized(string path)
    {
        if (path.Length < 2 || path[0] != '/' || path[^1] == '/')
        {
            return false;
        }

        // The loop below validates the character AFTER each '/' starting at i=1, which never
        // covers the segment beginning at index 1 (right after the leading slash). Reject a
        // leading dot there ("/.", "/..", "/./x"); "/.hidden" is also sent to GetFullPath,
        // which returns it unchanged - conservative but correct.
        if (path[1] == '.')
        {
            return false;
        }

        for (var i = 1; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '\\')
            {
                return false;
            }

            // Trailing '/' was rejected above, so i + 1 is always in range here.
            if (c == '/' && (path[i + 1] == '/' || path[i + 1] == '.'))
            {
                return false;
            }
        }

        return true;
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
