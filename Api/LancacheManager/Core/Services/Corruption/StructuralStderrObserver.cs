using System.Text.RegularExpressions;

namespace LancacheManager.Core.Services;

/// <summary>Allowlisted, sanitized live observer for structural-scan stderr only.</summary>
internal sealed partial class StructuralStderrObserver
{
    private const long WarningIntervalMs = 30_000;
    private readonly ILogger _logger;
    private readonly string _datasourceName;
    private readonly Func<long> _tickProvider;
    private long _warningCount;
    private long _lastReportedWarningCount;
    private long _lastWarningTicks;

    internal StructuralStderrObserver(
        ILogger logger,
        string datasourceName,
        Func<long>? tickProvider = null)
    {
        _logger = logger;
        _datasourceName = datasourceName;
        _tickProvider = tickProvider ?? (() => Environment.TickCount64);
    }

    internal void Observe(string line)
    {
        if (InspectionWarningSampleRegex().IsMatch(line))
        {
            ObserveWarning();
            return;
        }

        var warningSummary = InspectionWarningSummaryRegex().Match(line);
        if (warningSummary.Success
            && long.TryParse(warningSummary.Groups[1].Value, out var totalWarnings))
        {
            ObserveWarningTotal(totalWarnings);
            return;
        }

        var sanitized = SanitizeMilestone(line);
        if (sanitized != null)
        {
            _logger.LogInformation(
                "[StructuralScan:{Datasource}] {Milestone}",
                _datasourceName,
                sanitized);
        }
    }

    internal void Complete()
    {
        if (_warningCount == 0)
        {
            return;
        }

        _logger.LogWarning(
            "[StructuralScan:{Datasource}] Structural scan skipped {Total} file(s); raw paths were retained only in the bounded child stderr tail",
            _datasourceName,
            _warningCount);
        _lastReportedWarningCount = _warningCount;
    }

    private void ObserveWarning()
    {
        ObserveWarningTotal(Interlocked.Increment(ref _warningCount));
    }

    private void ObserveWarningTotal(long total)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _warningCount);
            if (current >= total)
            {
                total = current;
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _warningCount, total, current) != current);

        var now = _tickProvider();
        var elapsed = unchecked((ulong)(now - _lastWarningTicks));
        if (total != 1 && elapsed < WarningIntervalMs)
        {
            return;
        }

        var delta = total - _lastReportedWarningCount;
        _lastReportedWarningCount = total;
        _lastWarningTicks = now;
        _logger.LogWarning(
            "[StructuralScan:{Datasource}] Structural scan skipped {Delta} additional file(s) ({Total} total); path details suppressed",
            _datasourceName,
            delta,
            total);
    }

    private static string? SanitizeMilestone(string line)
    {
        if (line == "[structural] scan starting")
        {
            return "scan starting";
        }

        if (line == "[structural] enumerating cache files (this pass computes the total)...")
        {
            return "enumerating cache files";
        }

        var match = EnumeratedRegex().Match(line);
        if (match.Success)
        {
            return $"enumerated {match.Groups[1].Value} eligible cache files";
        }

        match = EnumerationCompleteRegex().Match(line);
        if (match.Success)
        {
            return $"enumeration complete: {match.Groups[1].Value} eligible files in {match.Groups[2].Value}s (cancelled={match.Groups[3].Value})";
        }

        match = InspectingRegex().Match(line);
        if (match.Success)
        {
            return $"inspection starting: total={match.Groups[1].Value}, workers={match.Groups[2].Value}, task_capacity={match.Groups[3].Value}, result_capacity={match.Groups[4].Value}";
        }

        match = InspectedRegex().Match(line);
        if (match.Success)
        {
            return $"inspection progress: processed={match.Groups[1].Value}/{match.Groups[2].Value}, suspects={match.Groups[3].Value}, files_per_second={match.Groups[4].Value}, eta_seconds={match.Groups[5].Value}";
        }

        match = FinishedRegex().Match(line);
        if (match.Success)
        {
            return $"scan finished in {match.Groups[1].Value}s: checked={match.Groups[2].Value}, suspects={match.Groups[3].Value}, io_errors={match.Groups[4].Value}, cancelled={match.Groups[5].Value}";
        }

        return null;
    }

    [GeneratedRegex(@"^\[structural\] enumeration progress eligible_files=(\d+)$")]
    private static partial Regex EnumeratedRegex();

    [GeneratedRegex(@"^\[structural\] enumeration complete: (\d+) eligible cache files in ([0-9.]+)s \(cancelled=(true|false)\)$")]
    private static partial Regex EnumerationCompleteRegex();

    [GeneratedRegex(@"^\[structural\] inspection starting total=(\d+) workers=(\d+) task_capacity=(\d+) result_capacity=(\d+)$")]
    private static partial Regex InspectingRegex();

    [GeneratedRegex(@"^\[structural\] inspection progress processed=(\d+)/(\d+) suspects=(\d+) files_per_second=([0-9]+(?:\.[0-9]+)?) eta_seconds=(unknown|\d+)$")]
    private static partial Regex InspectedRegex();

    [GeneratedRegex(@"^\[structural\] scan finished in ([0-9.]+)s: files_seen=\d+ files_checked=(\d+) consistent=\d+ suspects=(\d+) sparse=\d+ io_errors=(\d+) bytes_read=\d+ skipped_by_reason=.* \(cancelled=(true|false)\)$")]
    private static partial Regex FinishedRegex();

    [GeneratedRegex(@"^WARNING: structural inspection I/O error \(sample \d+/5\); path details suppressed$")]
    private static partial Regex InspectionWarningSampleRegex();

    [GeneratedRegex(@"^WARNING: structural inspection I/O errors suppressed after 5 samples; total=(\d+)$")]
    private static partial Regex InspectionWarningSummaryRegex();
}
