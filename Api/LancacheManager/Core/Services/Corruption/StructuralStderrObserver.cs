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

    [GeneratedRegex(@"^WARNING: structural inspection I/O error \(sample \d+/5\); path details suppressed$")]
    private static partial Regex InspectionWarningSampleRegex();

    [GeneratedRegex(@"^WARNING: structural inspection I/O errors suppressed after 5 samples; total=(\d+)$")]
    private static partial Regex InspectionWarningSummaryRegex();
}
