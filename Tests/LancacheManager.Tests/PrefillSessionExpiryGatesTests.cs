using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;

namespace LancacheManager.Tests;

public class PrefillSessionExpiryGatesTests
{
    private static readonly DateTime NowUtc = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static DaemonSession MakeSession(
        bool isPersistent,
        DateTime expiresAt,
        DaemonSessionStatus status = DaemonSessionStatus.Active,
        bool needsRelogin = false)
    {
        return new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = status,
            IsPersistent = isPersistent,
            NeedsRelogin = needsRelogin,
            CreatedAt = NowUtc.AddDays(-1),
            ExpiresAt = expiresAt
        };
    }

    [Fact]
    public void IsExpired_ReturnsTrue_WhenNowIsPastExpiresAt()
    {
        var expiresAt = NowUtc.AddMinutes(-1);
        Assert.True(PrefillSessionExpiryGates.IsExpired(expiresAt, NowUtc));
    }

    [Fact]
    public void IsExpired_ReturnsFalse_WhenNowEqualsExpiresAt()
    {
        // Strictly-greater-than boundary: "now == expiresAt" has not yet expired.
        Assert.False(PrefillSessionExpiryGates.IsExpired(NowUtc, NowUtc));
    }

    [Fact]
    public void IsExpired_ReturnsFalse_WhenNowIsBeforeExpiresAt()
    {
        var expiresAt = NowUtc.AddMinutes(1);
        Assert.False(PrefillSessionExpiryGates.IsExpired(expiresAt, NowUtc));
    }

    [Fact]
    public void ShouldFlagNeedsRelogin_ReturnsTrue_ForExpiredPersistentActiveSession_NotYetFlagged()
    {
        var session = MakeSession(isPersistent: true, expiresAt: NowUtc.AddMinutes(-1));
        Assert.True(PrefillSessionExpiryGates.ShouldFlagNeedsRelogin(session, NowUtc));
    }

    [Fact]
    public void ShouldFlagNeedsRelogin_ReturnsFalse_WhenSessionIsNotPersistent()
    {
        var session = MakeSession(isPersistent: false, expiresAt: NowUtc.AddMinutes(-1));
        Assert.False(PrefillSessionExpiryGates.ShouldFlagNeedsRelogin(session, NowUtc));
    }

    [Fact]
    public void ShouldFlagNeedsRelogin_ReturnsFalse_WhenNotYetExpired()
    {
        var session = MakeSession(isPersistent: true, expiresAt: NowUtc.AddMinutes(1));
        Assert.False(PrefillSessionExpiryGates.ShouldFlagNeedsRelogin(session, NowUtc));
    }

    [Fact]
    public void ShouldFlagNeedsRelogin_ReturnsFalse_WhenSessionIsNotActive()
    {
        var session = MakeSession(isPersistent: true, expiresAt: NowUtc.AddMinutes(-1), status: DaemonSessionStatus.Terminated);
        Assert.False(PrefillSessionExpiryGates.ShouldFlagNeedsRelogin(session, NowUtc));
    }

    [Fact]
    public void ShouldFlagNeedsRelogin_ReturnsFalse_WhenAlreadyFlagged_IdempotentGuard()
    {
        var session = MakeSession(isPersistent: true, expiresAt: NowUtc.AddMinutes(-1), needsRelogin: true);
        Assert.False(PrefillSessionExpiryGates.ShouldFlagNeedsRelogin(session, NowUtc));
    }

    [Fact]
    public void ShouldTerminate_ReturnsTrue_ForExpiredNonPersistentActiveSession()
    {
        var session = MakeSession(isPersistent: false, expiresAt: NowUtc.AddMinutes(-1));
        Assert.True(PrefillSessionExpiryGates.ShouldTerminate(session, NowUtc));
    }

    [Fact]
    public void ShouldTerminate_ReturnsFalse_WhenSessionIsPersistent()
    {
        // Persistent sessions are flagged, never terminated, by ProcessSessionExpiryAsync.
        var session = MakeSession(isPersistent: true, expiresAt: NowUtc.AddMinutes(-1));
        Assert.False(PrefillSessionExpiryGates.ShouldTerminate(session, NowUtc));
    }

    [Fact]
    public void ShouldTerminate_ReturnsFalse_WhenNotYetExpired()
    {
        var session = MakeSession(isPersistent: false, expiresAt: NowUtc.AddMinutes(1));
        Assert.False(PrefillSessionExpiryGates.ShouldTerminate(session, NowUtc));
    }

    [Fact]
    public void ShouldTerminate_ReturnsFalse_WhenSessionIsNotActive()
    {
        var session = MakeSession(isPersistent: false, expiresAt: NowUtc.AddMinutes(-1), status: DaemonSessionStatus.Error);
        Assert.False(PrefillSessionExpiryGates.ShouldTerminate(session, NowUtc));
    }
}
