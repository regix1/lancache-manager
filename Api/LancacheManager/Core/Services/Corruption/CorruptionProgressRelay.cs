using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>Captures every distinct corruption checkpoint.</summary>
internal sealed class CorruptionProgressRelay
{
    private readonly object _sync = new();
    private readonly CorruptionDetectionMetrics _metrics;
    private readonly int _datasourceIndex;
    private readonly int _datasourceCount;
    private readonly IReadOnlyDictionary<string, object?> _hostContext;
    private double _lastOverallPercent;

    internal CorruptionProgressRelay(
        CorruptionDetectionMetrics metrics,
        CorruptionDetectionMethod detectionMethod,
        string datasourceName,
        int datasourceIndex,
        int datasourceCount,
        StructuralScanMode? scanMode = null)
    {
        _metrics = metrics;
        _datasourceIndex = datasourceIndex;
        _datasourceCount = datasourceCount;
        _lastOverallPercent = CorruptionDetectionService.CalculateOverallProgress(
            datasourceIndex,
            datasourceCount,
            0);
        var hostContext = new Dictionary<string, object?>
        {
            ["detectionMethod"] = detectionMethod.ToWireString(),
            ["datasourceName"] = datasourceName,
            ["datasourceIndex"] = datasourceIndex + 1,
            ["datasourceCount"] = datasourceCount
        };
        if (scanMode.HasValue)
        {
            hostContext["scanMode"] = scanMode.Value.ToWireString();
        }
        _hostContext = hostContext;
    }

    internal CorruptionRelayDecision Capture(
        string stageKey,
        double datasourcePercent,
        IReadOnlyDictionary<string, object?>? sourceContext,
        long filesProcessed,
        long totalFiles)
    {
        lock (_sync)
        {
            var overallPercent = Math.Max(
                _lastOverallPercent,
                CorruptionDetectionService.CalculateOverallProgress(
                    _datasourceIndex,
                    _datasourceCount,
                    datasourcePercent));
            _lastOverallPercent = overallPercent;

            var additions = new Dictionary<string, object?>(_hostContext)
            {
                ["filesProcessed"] = filesProcessed,
                ["totalFiles"] = totalFiles
            };
            var candidate = OperationProgressSnapshot.Create(
                stageKey,
                overallPercent,
                sourceContext,
                revision: 0,
                authoritativeContext: additions);

            var current = _metrics.CurrentProgress;
            var isNew = current == null
                || !current.HasSameProgress(candidate.StageKey, candidate.PercentComplete, candidate.Context);
            var snapshot = isNew
                ? _metrics.CaptureProgress(stageKey, overallPercent, sourceContext, additions)
                : current!;
            return new CorruptionRelayDecision(snapshot, isNew);
        }
    }
}
