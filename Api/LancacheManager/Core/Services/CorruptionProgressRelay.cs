using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>Captures every distinct corruption checkpoint and independently gates broadcasts.</summary>
internal sealed class CorruptionProgressRelay
{
    private readonly object _sync = new();
    private readonly CorruptionDetectionMetrics _metrics;
    private readonly ProgressEmitGate _emitGate = new();
    private readonly int _datasourceIndex;
    private readonly int _datasourceCount;
    private readonly IReadOnlyDictionary<string, object?> _hostContext;
    private double _lastOverallPercent;

    internal CorruptionProgressRelay(
        CorruptionDetectionMetrics metrics,
        CorruptionDetectionMethod detectionMethod,
        string datasourceName,
        int datasourceIndex,
        int datasourceCount)
    {
        _metrics = metrics;
        _datasourceIndex = datasourceIndex;
        _datasourceCount = datasourceCount;
        _lastOverallPercent = CorruptionDetectionService.CalculateOverallProgress(
            datasourceIndex,
            datasourceCount,
            0);
        _hostContext = new Dictionary<string, object?>
        {
            ["detectionMethod"] = detectionMethod.ToWireString(),
            ["datasourceName"] = datasourceName,
            ["datasourceIndex"] = datasourceIndex + 1,
            ["datasourceCount"] = datasourceCount
        };
    }

    internal CorruptionRelayDecision Capture(
        string stageKey,
        double datasourcePercent,
        IReadOnlyDictionary<string, object?>? sourceContext,
        long filesProcessed,
        long totalFiles,
        long? nowTicks = null)
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
            var shouldEmit = nowTicks.HasValue
                ? _emitGate.ShouldEmit(snapshot.StageKey, snapshot.Revision, nowTicks.Value)
                : _emitGate.ShouldEmit(snapshot.StageKey, snapshot.Revision);
            return new CorruptionRelayDecision(snapshot, isNew, shouldEmit);
        }
    }
}

internal sealed record CorruptionRelayDecision(
    OperationProgressSnapshot Snapshot,
    bool IsNew,
    bool ShouldEmit);
