using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Thrown when a scan cannot start because a client download is writing to the cache. It is a
/// <see cref="ValidationException"/> so the HTTP answer stays a 400 with the reason, and a distinct
/// type so a caller can tell this apart from the many other stated preconditions that use the base
/// type, such as the write-permission guards.
/// </summary>
public class DownloadInProgressException : ValidationException
{
    public DownloadInProgressException(string message)
        : base(message, ErrorResponse.DownloadInProgressCode) { }
}

/// <summary>
/// Answers whether a client download is writing into the cache right now, which is the one
/// condition under which a cache scan reads a directory tree that is still changing underneath it.
/// The answer comes from the live speed tracker rather than the Downloads.IsActive column: the log
/// processor sets that column true on insert and only clears it when the same client and service
/// starts a new session, so a finished download stays flagged active indefinitely. It reads the
/// unfiltered snapshot, because hiding a client is a display preference and has no bearing on
/// whether that client's bytes are landing on disk.
/// A tracker that has no answer, because it has not spawned yet or its process just died, refuses
/// scans for a short bounded window rather than reporting the cache as quiet. Once that window
/// passes it reads as idle, so a tracker that never starts cannot block scans indefinitely.
/// </summary>
public sealed class CacheScanGate
{
    // How long an empty snapshot still means "no answer yet" rather than "nothing is downloading",
    // measured from the tracker's last transition into having no answer: construction, each spawn
    // of its child, and each death of that child.
    //
    // The rule this value must satisfy: it has to clear the time to spawn the child process plus
    // the first rolling window that child has to fill before it can report anything. The
    // arithmetic today is three plus two:
    //   - three seconds of margin for spawning the child process under load
    //   - two seconds for its first window, the minimum DownloadSpeedSnapshot.WindowSeconds
    // Either of those moving means this number moves with it. The tracker's own StartupDelay is
    // NOT a term: the clock is re-armed at the spawn itself, so however long the host takes to
    // reach that point does not eat into the window.
    //
    // Once the window passes the tracker is treated as saying nothing is downloading, so a tracker
    // that never starts, or that dies for good, cannot block scans forever.
    private static readonly TimeSpan _trackerStartupWindow = TimeSpan.FromSeconds(5);

    // How often WaitForDownloadAnswerAsync re-reads the tracker. Short enough that a startup scan
    // begins promptly once the tracker publishes, which is the common case and takes about a
    // second, and coarse enough that the longest possible wait costs fifty reads.
    private static readonly TimeSpan _answerPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly RustSpeedTrackerService _speedTracker;
    private readonly ILogger<CacheScanGate> _logger;

    public CacheScanGate(RustSpeedTrackerService speedTracker, ILogger<CacheScanGate> logger)
    {
        _speedTracker = speedTracker;
        _logger = logger;

        // The tracker announces when this answer changes, but the rule for computing it lives
        // here. Handing it the method keeps one definition rather than a second copy that can
        // drift, and avoids the tracker depending on something built on top of the tracker.
        RustSpeedTrackerService.ScanBlockedAnswer = CheckDownloadInProgress;

        // The answer also changes on the clock alone, once _trackerStartupWindow passes with
        // nothing reported, so the tracker is told how long to wait before the one re-check that no
        // output of its own would trigger. The quarter second clears the boundary: a check landing
        // exactly on it would read the window as still running, announce nothing, and never ask
        // again.
        RustSpeedTrackerService.ScanBlockedRecheckDelay =
            _trackerStartupWindow + TimeSpan.FromMilliseconds(250);
    }

    /// <summary>
    /// Waits until the tracker has an answer to give, or until its silence stops counting as one,
    /// so a caller asking straight afterwards is told what the tracker saw rather than that it has
    /// not looked yet.
    /// </summary>
    /// <remarks>
    /// A startup run asks at the one instant the tracker is guaranteed not to have answered: the
    /// clock is armed when the tracker is constructed, armed again when it spawns its child, and
    /// the child cannot report until it has opened its database pool. Asked at that instant, every
    /// restart refused the startup scan and left the work until the next interval, which for game
    /// detection is six hours.
    /// The wait is bounded by the same window the refusal uses, measured from when it starts, so a
    /// tracker that never comes up costs a startup run that much delay and no more, and the answer
    /// afterwards is whatever the ordinary rule says.
    /// </remarks>
    public async Task WaitForDownloadAnswerAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _trackerStartupWindow;

        while (_speedTracker.ReadUnfilteredState().UnreportedSinceUtc is not null &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(_answerPollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Returns null when a scan may start, or the reason it may not.
    /// </summary>
    public string? CheckDownloadInProgress()
    {
        // One read for both: the tracker writes the clock and the snapshot together at every
        // transition, and asking for them separately lets a death land between the two answers.
        var (unreportedSince, snapshot) = _speedTracker.ReadUnfilteredState();

        if (unreportedSince != null && DateTime.UtcNow - unreportedSince.Value < _trackerStartupWindow)
        {
            const string starting =
                "The download tracker has not reported yet, so a scan cannot tell whether the cache is being written to. Try again in a few seconds.";
            _logger.LogDebug("[CacheScanGate] {Reason}", starting);
            return starting;
        }

        if (!snapshot.HasActiveDownloads)
        {
            return null;
        }

        const string reason = "A client download is writing to the cache right now. Try again once it finishes.";
        _logger.LogDebug("[CacheScanGate] {Reason}", reason);
        return reason;
    }
}
