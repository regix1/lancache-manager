using System.Globalization;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>Short-lived exact access-log candidate. <see cref="Target"/> never enters a wire DTO.</summary>
internal sealed record ContentPathSample(
    string Service,
    string Host,
    string Target,
    DateTimeOffset ObservedAtUtc,
    string CacheOutcome,
    int StatusCode,
    long Bytes);

/// <summary>
/// Aggregated Rust content-scan result across a datasource set, before the security filters and
/// sample selection the check service applies in C#. <see cref="Availability"/> is one of
/// "available", "unreadable", or "logMissing".
/// </summary>
internal sealed record ContentPathRawScan(
    string Availability,
    bool ScanTruncated,
    long ScannedBytes,
    IReadOnlyList<RustContentSample> Records);

/// <summary>
/// Turns a Rust-produced <see cref="RustContentSample"/> into a validated <see cref="ContentPathSample"/>.
/// The Rust scan already reused the canonical log grammar and applied the cheap positive-cache gate;
/// this is the SECURITY boundary that stays in C#: path/SSRF safety, host DNS normalization, a
/// not-future timestamp, and a known-service gate. The positive-cache rule is re-checked as
/// defense-in-depth so a malformed record can never reach a network probe.
/// </summary>
internal static class ContentPathRecordFilter
{
    private const string ProbeUserAgentMarker = "lancache-manager-status-check";

    internal static bool TryMap(
        RustContentSample record,
        IReadOnlySet<string> knownServices,
        DateTimeOffset now,
        out ContentPathSample? sample)
    {
        sample = null;

        var service = record.Service.Trim().ToLowerInvariant();
        if (!knownServices.Contains(service) ||
            !string.Equals(record.Method, "GET", StringComparison.Ordinal) ||
            record.UserAgent.Contains(ProbeUserAgentMarker, StringComparison.OrdinalIgnoreCase) ||
            ContentPathTargetSafety.IsExcludedEndpoint(record.Target) ||
            !ContentPathTargetSafety.IsSafe(record.Target) ||
            !IsPositiveCacheEvidence(record.StatusCode, record.Bytes, record.CacheStatus) ||
            !TryNormalizeHost(record.Host, out var host) ||
            !TryParseTimestamp(record.Timestamp, out var observedAt) ||
            observedAt > now.AddMinutes(5))
        {
            return false;
        }

        sample = new ContentPathSample(
            service,
            host,
            record.Target,
            observedAt,
            record.CacheStatus.ToLowerInvariant(),
            record.StatusCode,
            record.Bytes);
        return true;
    }

    internal static bool IsPositiveCacheEvidence(int statusCode, long bytes, string cacheOutcome) =>
        statusCode is 200 or 206 &&
        bytes > 0 &&
        (cacheOutcome.Equals("HIT", StringComparison.OrdinalIgnoreCase) ||
         cacheOutcome.Equals("MISS", StringComparison.OrdinalIgnoreCase));

    private static bool TryNormalizeHost(string value, out string host)
    {
        host = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length is 0 or > 253 || !host.Contains('.') || host.Any(static ch => ch > 127) ||
            Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            host = string.Empty;
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(static ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            {
                host = string.Empty;
                return false;
            }
        }

        return true;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp)
    {
        // Rust emits the record instant as RFC3339 UTC (e.g. 2026-07-10T19:55:00+00:00).
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out timestamp))
        {
            timestamp = timestamp.ToUniversalTime();
            return true;
        }

        timestamp = default;
        return false;
    }
}

internal static class ContentPathTargetSafety
{
    private const int MaxTargetLength = 2048;
    private const int MaxDisplayLength = 256;

    internal static bool IsExcludedEndpoint(string target) =>
        target.Contains("/lancache-heartbeat", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("/health", StringComparison.OrdinalIgnoreCase) ||
        target.Contains("/ping", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSafe(string target)
    {
        if (target.Length is 0 or > MaxTargetLength || target[0] != '/' || target.StartsWith("//", StringComparison.Ordinal) ||
            target.Contains('?') || target.Contains('#') || target.Contains('@') || target.Contains('\\') ||
            target.Any(static ch => char.IsControl(ch) || char.IsWhiteSpace(ch)))
        {
            return false;
        }

        for (var index = 0; index < target.Length; index++)
        {
            if (target[index] == '%' &&
                (index + 2 >= target.Length || !Uri.IsHexDigit(target[index + 1]) || !Uri.IsHexDigit(target[index + 2])))
            {
                return false;
            }
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (decoded.Contains('\\') || decoded.Contains('?') || decoded.Contains('#') || decoded.Contains('@') ||
            decoded.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            decoded.Any(static ch => char.IsControl(ch) || char.IsWhiteSpace(ch)))
        {
            return false;
        }

        foreach (var segment in decoded.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.Length > 255 || IsUnsafeOpaqueSegment(segment))
            {
                return false;
            }
        }

        return true;
    }

    internal static string ToDisplayPath(string target) =>
        target.Length <= MaxDisplayLength ? target : target[..(MaxDisplayLength - 1)] + "…";

    private static bool IsUnsafeOpaqueSegment(string segment)
    {
        if (segment.Length < 48 || segment.Contains('.') || Guid.TryParse(segment, out _) ||
            segment.All(char.IsDigit) || segment.All(Uri.IsHexDigit))
        {
            return false;
        }

        var base64Like = segment.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
        return base64Like &&
               (segment.Length >= 80 ||
                (segment.Any(char.IsAsciiLetterLower) &&
                 segment.Any(char.IsAsciiLetterUpper) &&
                 segment.Any(char.IsDigit)));
    }
}

internal static class ContentPathSampleSelector
{
    private const int MaxHostsPerService = 3;
    private static readonly TimeSpan _maxSampleAge = TimeSpan.FromDays(30);

    internal static IReadOnlyList<ContentPathSample> Select(
        IEnumerable<ContentPathSample> samples,
        DateTimeOffset now)
    {
        var newestExactTargets = samples
            .Where(sample => sample.ObservedAtUtc <= now.AddMinutes(5) && now - sample.ObservedAtUtc <= _maxSampleAge)
            .GroupBy(
                sample => (sample.Service, sample.Host, sample.Target),
                StringTupleComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(sample => sample.ObservedAtUtc)
                .ThenBy(sample => sample.CacheOutcome, StringComparer.Ordinal)
                .First());

        return newestExactTargets
            .GroupBy(sample => sample.Service, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .SelectMany(group => group
                .GroupBy(sample => sample.Host, StringComparer.OrdinalIgnoreCase)
                .Select(hostGroup => hostGroup
                    .OrderByDescending(sample => sample.ObservedAtUtc)
                    .ThenBy(sample => sample.Target, StringComparer.Ordinal)
                    .First())
                .OrderByDescending(sample => sample.ObservedAtUtc)
                .ThenBy(sample => sample.Host, StringComparer.Ordinal)
                .ThenBy(sample => sample.Target, StringComparer.Ordinal)
                .Take(MaxHostsPerService))
            .ToList();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Service, string Host, string Target)>
    {
        internal static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals(
            (string Service, string Host, string Target) x,
            (string Service, string Host, string Target) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Service, y.Service) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Host, y.Host) &&
            StringComparer.Ordinal.Equals(x.Target, y.Target);

        public int GetHashCode((string Service, string Host, string Target) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Service),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Host),
            StringComparer.Ordinal.GetHashCode(value.Target));
    }
}

/// <summary>
/// Aggregates the read-only Rust content scan across every enabled datasource log directory. Each
/// directory is scanned once (the Rust side discovers all its sources and reuses both line
/// parsers); availability is combined with the same precedence the retired in-process scanner used
/// (any readable -> available, else any unreadable -> unreadable, else logMissing). The per-directory
/// scan is injected so the aggregation is testable without a real Rust process.
/// </summary>
internal sealed class RustContentPathScanner
{
    private readonly Func<string, CancellationToken, Task<RustContentScanResult>> _scanDirectory;

    internal RustContentPathScanner(Func<string, CancellationToken, Task<RustContentScanResult>> scanDirectory)
    {
        _scanDirectory = scanDirectory;
    }

    internal async Task<ContentPathRawScan> ScanAsync(
        IReadOnlyList<string> logDirectories,
        CancellationToken cancellationToken)
    {
        var records = new List<RustContentSample>();
        var scannedBytes = 0L;
        var truncated = false;
        var anyReadable = false;
        var anyUnreadable = false;

        foreach (var directory in logDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _scanDirectory(directory, cancellationToken);
            scannedBytes += result.ScannedBytes;
            truncated |= result.Truncated;
            records.AddRange(result.Records);

            switch (result.Availability)
            {
                case "available":
                    anyReadable = true;
                    break;
                case "unreadable":
                    anyUnreadable = true;
                    break;
                // "logMissing" contributes neither readability nor an unreadable signal.
            }
        }

        var availability = anyReadable ? "available" : anyUnreadable ? "unreadable" : "logMissing";
        return new ContentPathRawScan(availability, truncated, scannedBytes, records);
    }
}
