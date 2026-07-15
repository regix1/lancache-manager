using LancacheManager.Core.Services.SteamPrefill;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

/// <summary>
/// Unit coverage for the bounded event-drain tracker behind SocketDaemonClient/TcpDaemonClient's
/// DrainEventsAsync: it waits for in-flight fire-and-forget event tasks, ATOMICALLY rejects new ones once
/// draining (the callback never starts), and is strictly bounded (logs a warning and returns rather than
/// hanging shutdown).
/// </summary>
public class DaemonEventDrainTrackerTests
{
    [Fact]
    public async Task DrainAsync_CompletesImmediately_WhenNoInFlightTasks()
    {
        var tracker = new DaemonEventDrainTracker(null);

        var drain = tracker.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.True(drain.IsCompleted);
        await drain;
        Assert.True(tracker.IsDraining);
    }

    [Fact]
    public async Task DrainAsync_WaitsForTrackedTask_ThenCompletesWhenItFinishes()
    {
        var tracker = new DaemonEventDrainTracker(null);
        var gate = new TaskCompletionSource();
        Assert.True(tracker.TryTrack(() => gate.Task)); // admitted before draining

        var drain = tracker.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted); // still waiting on the in-flight event task
        Assert.True(tracker.IsDraining); // new events are rejected from now on

        gate.SetResult();

        await drain; // completes promptly once the tracked task finishes, well within the 5s bound
    }

    [Fact]
    public async Task DrainAsync_TimesOut_AndLogsWarning_WhenTaskNeverCompletes()
    {
        var logger = new CapturingLogger();
        var tracker = new DaemonEventDrainTracker(logger);
        Assert.True(tracker.TryTrack(() => new TaskCompletionSource().Task)); // never completes

        var start = DateTime.UtcNow;
        await tracker.DrainAsync(TimeSpan.FromMilliseconds(150));
        var elapsed = DateTime.UtcNow - start;

        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"drain must be bounded; took {elapsed}");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("draining"));
    }

    [Fact]
    public async Task TryTrack_ReturnsTrue_ButDoesNotTrackAnAlreadyCompletedTask()
    {
        var tracker = new DaemonEventDrainTracker(null);
        Assert.True(tracker.TryTrack(() => Task.CompletedTask)); // admitted, but nothing to drain

        var drain = tracker.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.True(drain.IsCompleted);
        await drain;
    }

    [Fact]
    public async Task TryTrack_RejectsAdmission_AndNeverStartsTheCallback_OnceDrainingBegins()
    {
        var tracker = new DaemonEventDrainTracker(null);
        await tracker.DrainAsync(TimeSpan.FromSeconds(5)); // flips draining (no in-flight tasks -> immediate)

        var callbackStarted = false;
        var admitted = tracker.TryTrack(() =>
        {
            callbackStarted = true;
            return Task.CompletedTask;
        });

        Assert.False(admitted);        // rejected because draining has begun
        Assert.False(callbackStarted); // and the factory was never invoked, so the callback never ran
    }

    [Fact]
    public async Task TryTrack_AdmittedTaskIsAwaitedByADrainThatStartsWhileItIsInFlight()
    {
        // Admission-before-drain-start is atomic: a task admitted while not draining is in the snapshot
        // DrainAsync takes, so the drain waits for it. (The post-draining rejection case is the sibling
        // test above; together they bracket the atomic TryTrack/DrainAsync contract.)
        var tracker = new DaemonEventDrainTracker(null);
        var gate = new TaskCompletionSource();
        Assert.True(tracker.TryTrack(() => gate.Task));

        var drain = tracker.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted);

        gate.SetResult();
        await drain;
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_sync) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
