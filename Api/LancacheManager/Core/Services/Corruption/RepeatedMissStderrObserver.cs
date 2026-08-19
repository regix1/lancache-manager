using System.Text.RegularExpressions;

namespace LancacheManager.Core.Services;

/// <summary>Promotes only the sanitized repeated-MISS coverage summary from child stderr.</summary>
internal sealed partial class RepeatedMissStderrObserver
{
    private readonly ILogger _logger;
    private readonly string _datasourceName;

    internal RepeatedMissStderrObserver(ILogger logger, string datasourceName)
    {
        _logger = logger;
        _datasourceName = datasourceName;
    }

    internal void Observe(string line)
    {
        var match = CoverageSummaryRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        _logger.LogWarning(
            "[RepeatedMissScan:{Datasource}] Coverage gaps: malformed lines={MalformedLines}, unsupported ranges={UnsupportedRanges}, unreadable logs={UnreadableLogs}, qualified misses without a safe file={MissingSafeFiles}",
            _datasourceName,
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            match.Groups[4].Value);
    }

    [GeneratedRegex(
        @"^WARNING: repeated-MISS scan coverage gaps: malformed_lines=(\d+) unsupported_ranges=(\d+) unreadable_log_files=(\d+) qualified_without_safe_file=(\d+)$")]
    private static partial Regex CoverageSummaryRegex();
}
