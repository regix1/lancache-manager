using LancacheManager.Models;

namespace LancacheManager.Core.Constants;

public static class DownloadKindConstants
{
    public const string PrefillToken = "prefill";

    /// <summary>
    /// In-memory (non-EF-translatable) check for whether an already-fetched Download is
    /// prefill-daemon traffic. Case-insensitive on both ClientIp and Datasource. For query-time
    /// filtering use DownloadQueryExtensions.ApplyPrefillFilter instead, which EF can translate to SQL.
    /// </summary>
    public static bool IsPrefillDownload(Download download) =>
        string.Equals(download.ClientIp, PrefillToken, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(download.Datasource, PrefillToken, StringComparison.OrdinalIgnoreCase);
}
