namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Thread-safe stage/revision gate for live progress. Recovery state is captured independently;
/// this class controls only the SignalR cadence.
/// </summary>
internal sealed class ProgressEmitGate
{
    private readonly object _sync = new();
    private readonly long _minimumIntervalMs;
    private string? _lastEmittedStageKey;
    private long _lastEmittedRevision = -1;
    private long _lastEmitTicks;
    private bool _hasEmitted;

    internal ProgressEmitGate(long minimumIntervalMs = RustProcessHelper.ProgressEmitMinIntervalMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumIntervalMs);
        _minimumIntervalMs = minimumIntervalMs;
    }

    public bool ShouldEmit(string stageKey, long revision) =>
        ShouldEmit(stageKey, revision, Environment.TickCount64);

    internal bool ShouldEmit(string stageKey, long revision, long nowTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);

        lock (_sync)
        {
            if (_hasEmitted
                && string.Equals(stageKey, _lastEmittedStageKey, StringComparison.Ordinal)
                && revision == _lastEmittedRevision)
            {
                return false;
            }

            var stageChanged = !_hasEmitted
                || !string.Equals(stageKey, _lastEmittedStageKey, StringComparison.Ordinal);
            var elapsed = unchecked((ulong)(nowTicks - _lastEmitTicks));
            if (!stageChanged && elapsed < (ulong)_minimumIntervalMs)
            {
                return false;
            }

            _hasEmitted = true;
            _lastEmittedStageKey = stageKey;
            _lastEmittedRevision = revision;
            _lastEmitTicks = nowTicks;
            return true;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _lastEmittedStageKey = null;
            _lastEmittedRevision = -1;
            _lastEmitTicks = 0;
            _hasEmitted = false;
        }
    }
}
