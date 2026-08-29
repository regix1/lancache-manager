using System.Net;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Normalizes a user-supplied list of client IP addresses the same way everywhere a request
/// carries one (client-group membership, stats exclusions).
/// </summary>
public static class ClientIpList
{
    /// <summary>
    /// Trims, parses and de-duplicates a requested address list, collecting the entries that are
    /// not addresses into <paramref name="invalidIps"/> rather than discarding them. Blank entries
    /// are dropped silently - an empty row in the payload is not something to report back.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? ips, out List<string> invalidIps)
    {
        invalidIps = new List<string>();
        var normalized = new List<string>();

        if (ips == null)
        {
            return normalized;
        }

        foreach (var rawIp in ips)
        {
            var trimmed = rawIp?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!TryNormalize(trimmed, out var normalizedIp))
            {
                invalidIps.Add(trimmed);
                continue;
            }

            if (!normalized.Contains(normalizedIp, StringComparer.Ordinal))
            {
                normalized.Add(normalizedIp);
            }
        }

        return normalized;
    }

    /// <summary>
    /// Parses a single already-trimmed, non-blank address into its canonical string form.
    /// Shared by <see cref="Normalize"/> and any caller normalizing one address at a time.
    /// </summary>
    public static bool TryNormalize(string raw, out string normalized)
    {
        if (IPAddress.TryParse(raw, out var parsed))
        {
            normalized = parsed.ToString();
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
