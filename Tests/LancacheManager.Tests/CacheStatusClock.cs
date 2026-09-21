namespace LancacheManager.Tests;

internal sealed class CacheStatusClock(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ClockTimer> _timers = [];
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
            return _utcNow;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ClockTimer(this, callback, state);
        Change(timer, dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_sync)
        {
            _utcNow = _utcNow.Add(duration);
            foreach (var timer in _timers.ToArray())
            {
                if (!timer.Active || timer.DueAtUtc > _utcNow)
                    continue;

                callbacks.Add((timer.Callback, timer.State));
                if (timer.Period == Timeout.InfiniteTimeSpan)
                {
                    timer.Active = false;
                }
                else
                {
                    do
                    {
                        timer.DueAtUtc = timer.DueAtUtc.Add(timer.Period);
                    }
                    while (timer.DueAtUtc <= _utcNow);
                }
            }
        }

        foreach (var callback in callbacks)
            callback.Callback(callback.State);
    }

    private void Change(ClockTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_sync)
        {
            timer.Period = period;
            timer.Active = dueTime != Timeout.InfiniteTimeSpan;
            timer.DueAtUtc = timer.Active ? _utcNow.Add(dueTime) : DateTimeOffset.MaxValue;
            if (!_timers.Contains(timer))
                _timers.Add(timer);
        }
    }

    private void Remove(ClockTimer timer)
    {
        lock (_sync)
        {
            timer.Active = false;
            _timers.Remove(timer);
        }
    }

    private sealed class ClockTimer(
        CacheStatusClock clock,
        TimerCallback callback,
        object? state) : ITimer
    {
        internal TimerCallback Callback { get; } = callback;
        internal object? State { get; } = state;
        internal DateTimeOffset DueAtUtc { get; set; }
        internal TimeSpan Period { get; set; }
        internal bool Active { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Change(this, dueTime, period);
            return true;
        }

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
