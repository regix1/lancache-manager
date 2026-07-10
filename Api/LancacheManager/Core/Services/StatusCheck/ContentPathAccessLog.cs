using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

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

internal sealed record ContentPathScanResult(
    string Availability,
    bool ScanTruncated,
    long ScannedBytes,
    IReadOnlyList<ContentPathSample> Samples);

/// <summary>Mirrors the established LANCache/Rust five-quoted-tail-field access-log grammar.</summary>
internal static class ContentPathAccessLogParser
{
    private const int MaxLineLength = 16 * 1024;

    private static readonly Regex _linePattern = new(
        @"^\[(?<service>[^\]\r\n]{1,128})\]\s+(?<client>\S+)\s+/\s+-\s+-\s+-\s+\[(?<time>[^\]\r\n]+)\]\s+""(?<method>[A-Z]+)\s+(?<target>\S+)(?:\s+HTTP/(?<version>[^""\s]+))?""\s+(?<status>\d{3})\s+(?<bytes>-|\d+)\s+""(?<referer>(?:\\.|[^""\r\n])*)""\s+""(?<userAgent>(?:\\.|[^""\r\n])*)""\s+""(?<cache>(?:\\.|[^""\r\n])*)""\s+""(?<host>(?:\\.|[^""\r\n])*)""\s+""(?<range>(?:\\.|[^""\r\n])*)""\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    internal static bool TryParseCandidate(
        string line,
        IReadOnlySet<string> knownServices,
        DateTimeOffset now,
        out ContentPathSample? sample)
    {
        sample = null;
        if (string.IsNullOrEmpty(line) || line.Length > MaxLineLength)
        {
            return false;
        }

        Match match;
        try
        {
            match = _linePattern.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success)
        {
            return false;
        }

        var service = match.Groups["service"].Value.Trim().ToLowerInvariant();
        var method = match.Groups["method"].Value;
        var target = match.Groups["target"].Value;
        var userAgent = match.Groups["userAgent"].Value;
        var cacheOutcome = match.Groups["cache"].Value;

        if (!knownServices.Contains(service) || method != "GET" ||
            userAgent.Contains("lancache-manager-status-check", StringComparison.OrdinalIgnoreCase) ||
            ContentPathTargetSafety.IsExcludedEndpoint(target) ||
            !ContentPathTargetSafety.IsSafe(target) ||
            !int.TryParse(match.Groups["status"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode) ||
            !long.TryParse(match.Groups["bytes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ||
            !IsPositiveCacheEvidence(statusCode, bytes, cacheOutcome) ||
            !TryNormalizeHost(match.Groups["host"].Value, out var host) ||
            !TryParseTimestamp(match.Groups["time"].Value, out var observedAt) ||
            observedAt > now.AddMinutes(5))
        {
            return false;
        }

        sample = new ContentPathSample(
            service,
            host,
            target,
            observedAt,
            cacheOutcome.ToLowerInvariant(),
            statusCode,
            bytes);
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
        var normalized = value.Trim();
        if (normalized.Length >= 5)
        {
            var offsetStart = normalized.Length - 5;
            if ((normalized[offsetStart] == '+' || normalized[offsetStart] == '-') &&
                normalized.AsSpan(offsetStart + 1).IndexOf(':') < 0)
            {
                normalized = normalized.Insert(offsetStart + 3, ":");
            }
        }

        if (DateTimeOffset.TryParseExact(
                normalized,
                "dd/MMM/yyyy:HH:mm:ss zzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp))
        {
            timestamp = timestamp.ToUniversalTime();
            return true;
        }

        if (DateTime.TryParseExact(
                normalized,
                new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" },
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utc))
        {
            timestamp = new DateTimeOffset(utc, TimeSpan.Zero);
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

/// <summary>Read-only bounded tail scanner. It never shares offsets with the live monitor.</summary>
internal sealed class ContentPathLogScanner
{
    internal const int DefaultMaxTailBytes = 32 * 1024 * 1024;
    private readonly int _maxTailBytes;

    internal ContentPathLogScanner(int maxTailBytes = DefaultMaxTailBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTailBytes, 1);
        _maxTailBytes = maxTailBytes;
    }

    internal async Task<ContentPathScanResult> ScanAsync(
        IEnumerable<string> paths,
        IReadOnlySet<string> knownServices,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var samples = new List<ContentPathSample>();
        var scannedBytes = 0L;
        var truncated = false;
        var anyReadable = false;
        var anyUnreadable = false;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var read = await ReadTailAsync(path, cancellationToken);
                anyReadable = true;
                scannedBytes += read.BytesRead;
                truncated |= read.Truncated;

                foreach (var line in read.Lines)
                {
                    if (ContentPathAccessLogParser.TryParseCandidate(line, knownServices, now, out var sample))
                    {
                        samples.Add(sample!);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A rotated/not-yet-created log is a typed missing state, not a sweep failure.
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or NotSupportedException)
            {
                anyUnreadable = true;
            }
        }

        var availability = anyReadable ? "available" : anyUnreadable ? "unreadable" : "logMissing";
        return new ContentPathScanResult(
            availability,
            truncated,
            scannedBytes,
            ContentPathSampleSelector.Select(samples, now));
    }

    private async Task<(IReadOnlyList<string> Lines, int BytesRead, bool Truncated)> ReadTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var length = stream.Length;
        var start = Math.Max(0, length - _maxTailBytes);
        var count = checked((int)Math.Min(_maxTailBytes, length - start));
        var buffer = new byte[count];
        stream.Seek(start, SeekOrigin.Begin);

        var bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(bytesRead), cancellationToken);
            if (read == 0)
            {
                break;
            }
            bytesRead += read;
        }

        ReadOnlySpan<byte> slice = buffer.AsSpan(0, bytesRead);
        if (start > 0)
        {
            var firstNewline = slice.IndexOf((byte)'\n');
            slice = firstNewline < 0 ? ReadOnlySpan<byte>.Empty : slice[(firstNewline + 1)..];
        }

        if (slice.Length > 0 && slice[^1] != (byte)'\n')
        {
            var lastNewline = slice.LastIndexOf((byte)'\n');
            slice = lastNewline < 0 ? ReadOnlySpan<byte>.Empty : slice[..(lastNewline + 1)];
        }

        var text = Encoding.UTF8.GetString(slice);
        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.TrimEnd('\r'))
            .ToList();
        return (lines, bytesRead, start > 0);
    }
}
