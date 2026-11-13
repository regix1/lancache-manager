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
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Func<Task>? _heldSend;
    private long _heldRevision;
    private bool _heldSendScheduled;

    internal ProgressEmitGate(long minimumIntervalMs = RustProcessHelper.ProgressEmitMinIntervalMs)
    {
        _minimumIntervalMs = minimumIntervalMs;
    }

    /// <summary>
    /// Sends the update now when <see cref="ShouldEmit(string, long)"/> lets it through. Otherwise holds it in place of
    /// any update held before it and sends it once the interval has passed, so a quiet stretch never leaves the live
    /// line one report behind. Sends go out one at a time, in the order they were let through.
    /// </summary>
    public async Task EmitAsync(string stageKey, long revision, Func<Task> send)
    {
        long delayMs;
        await _sendLock.WaitAsync();
        try
        {
            var nowTicks = Environment.TickCount64;
            if (ShouldEmit(stageKey, revision, nowTicks))
            {
                // This update is newer than any held one, so the held one is never sent.
                _heldSend = null;
                await send();
                return;
            }

            lock (_sync)
            {
                // The live line already shows the last sent update, so holding a repeat of it would only send it
                // again, and once the interval has passed its wait would come out negative.
                if (string.Equals(stageKey, _lastEmittedStageKey, StringComparison.Ordinal)
                    && revision == _lastEmittedRevision)
                {
                    return;
                }
            }

            _heldSend = send;
            _heldRevision = revision;
            if (_heldSendScheduled) return;
            _heldSendScheduled = true;
            lock (_sync) delayMs = _minimumIntervalMs - (nowTicks - _lastEmitTicks);
        }
        finally
        {
            _sendLock.Release();
        }

        _ = SendHeldAfterAsync(delayMs);
    }

    /// <summary>
    /// Sends the held update now and waits for a send in progress, so the caller's own final send follows every
    /// progress send.
    /// </summary>
    public async Task FlushAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            await SendHeldAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendHeldAfterAsync(long delayMs)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs));
            await _sendLock.WaitAsync();
            try
            {
                // A live send since the timer was armed restarts the window, so wait out what is left of it.
                lock (_sync) delayMs = _minimumIntervalMs - (Environment.TickCount64 - _lastEmitTicks);
                if (_heldSend != null && delayMs > 0) continue;

                _heldSendScheduled = false;
                await SendHeldAsync();
                return;
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    private async Task SendHeldAsync()
    {
        if (_heldSend is not { } send) return;
        _heldSend = null;
        lock (_sync)
        {
            _lastEmittedRevision = _heldRevision;
            _lastEmitTicks = Environment.TickCount64;
        }

        await send();
    }

    public bool ShouldEmit(string stageKey, long revision) =>
        ShouldEmit(stageKey, revision, Environment.TickCount64);

    internal bool ShouldEmit(string stageKey, long revision, long nowTicks)
    {
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
