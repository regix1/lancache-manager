using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the stall-watchdog invariant: <see cref="PrefillDaemonServiceBase.IsPrefillStalled"/>
/// correctly identifies actively-prefilling sessions whose byte count has not advanced for longer
/// than the configured threshold, while leaving progressing or idle sessions unaffected.
/// </summary>
public class PrefillStallWatchdogTests
{
    private static DaemonSession MakeActivePrefillSession(
        bool isPrefilling = true,
        PrefillState prefillState = PrefillState.Downloading,
        DateTime? lastProgressAt = null,
        long lastProgressBytes = 0)
    {
        var progressAt = lastProgressAt.HasValue ? lastProgressAt.Value : DateTime.UtcNow;
        return new DaemonSession
        {
            IsPrefilling = isPrefilling,
            PrefillState = prefillState,
            LastProgressBytes = lastProgressBytes,
            LastProgressTicksUtc = progressAt.Ticks,
        };
    }

    [Fact]
    public void IsPrefillStalled_ReturnsTrue_WhenProgressUnchangedPastThreshold()
    {
        var nowUtc = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(180);
        var session = MakeActivePrefillSession(
            isPrefilling: true,
            prefillState: PrefillState.Downloading,
            lastProgressAt: nowUtc - TimeSpan.FromSeconds(200)); // 200s ago > 180s threshold

        var result = PrefillDaemonServiceBase.IsPrefillStalled(session, nowUtc, threshold);

        Assert.True(result);
    }

    [Fact]
    public void IsPrefillStalled_ReturnsTrue_WhenInStartedStateAndNoProgressPastThreshold()
    {
        var nowUtc = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(180);
        var session = MakeActivePrefillSession(
            isPrefilling: true,
            prefillState: PrefillState.Started,
            lastProgressAt: nowUtc - TimeSpan.FromSeconds(181)); // just past threshold

        var result = PrefillDaemonServiceBase.IsPrefillStalled(session, nowUtc, threshold);

        Assert.True(result);
    }

    [Fact]
    public void IsPrefillStalled_ReturnsFalse_WhenProgressRecentlyAdvanced()
    {
        var nowUtc = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(180);
        var session = MakeActivePrefillSession(
            isPrefilling: true,
            prefillState: PrefillState.Downloading,
            lastProgressAt: nowUtc - TimeSpan.FromSeconds(30)); // 30s ago < 180s threshold

        var result = PrefillDaemonServiceBase.IsPrefillStalled(session, nowUtc, threshold);

        Assert.False(result);
    }

    [Fact]
    public void IsPrefillStalled_ReturnsFalse_WhenNotPrefilling()
    {
        var nowUtc = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(180);
        // Session is idle (IsPrefilling=false) — e.g. between runs
        var session = MakeActivePrefillSession(
            isPrefilling: false,
            prefillState: PrefillState.Idle,
            lastProgressAt: nowUtc - TimeSpan.FromSeconds(300)); // stale but not active

        var result = PrefillDaemonServiceBase.IsPrefillStalled(session, nowUtc, threshold);

        Assert.False(result);
    }

    [Fact]
    public void IsPrefillStalled_ReturnsFalse_WhenNoProgressClockSet()
    {
        // After TransitionToTerminalAsync resets the clock to 0, the session must not re-stall.
        var nowUtc = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(180);
        var session = new DaemonSession
        {
            IsPrefilling = false,
            PrefillState = PrefillState.Failed,
            LastProgressTicksUtc = 0L, // reset by terminal funnel
            LastProgressBytes = 1024,
        };

        var result = PrefillDaemonServiceBase.IsPrefillStalled(session, nowUtc, threshold);

        Assert.False(result);
    }

    [Fact]
    public void IsPrefillStalled_ReturnsFalse_ExactlyAtThresholdBoundary()
    {
        var nowUtc = DateTime.UtcNow;
        var threshold = TimeSpan.FromSeconds(180);
        // Exactly at the threshold — should NOT be considered stalled (must be GREATER THAN)
        var session = MakeActivePrefillSession(
            isPrefilling: true,
            prefillState: PrefillState.Downloading,
            lastProgressAt: nowUtc - threshold);

        var result = PrefillDaemonServiceBase.IsPrefillStalled(session, nowUtc, threshold);

        Assert.False(result);
    }
}
