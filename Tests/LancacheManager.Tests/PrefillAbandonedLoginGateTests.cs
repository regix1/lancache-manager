using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the abandoned-login gate:
/// <see cref="PrefillSessionExpiryGates.ShouldCancelAbandonedLogin"/> ends a sign-in nobody came back
/// to answer, and leaves every other session alone. The case that matters most is a challenge whose
/// <see cref="CredentialChallenge.ExpiresAt"/> was never sent: it is a non-nullable
/// <see cref="DateTime"/>, so it deserializes to <c>0001-01-01</c>, and reading that as a real
/// deadline would cancel every live sign-in the first time the sweep ran.
/// </summary>
public class PrefillAbandonedLoginGateTests
{
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromSeconds(900);

    private static DaemonSession MakeLoggingInSession(
        DateTime? loginStartedAtUtc,
        CredentialChallenge? pendingChallenge = null,
        DaemonAuthState authState = DaemonAuthState.LoggingIn,
        DaemonSessionStatus status = DaemonSessionStatus.Active)
    {
        return new DaemonSession
        {
            AuthState = authState,
            Status = status,
            LoginStartedAtUtc = loginStartedAtUtc,
            PendingLoginChallenge = pendingChallenge,
        };
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenTheChallengeCarriesNoExpiry()
    {
        // A daemon payload with no expiresAt field lands on default(DateTime), which is always in the
        // past. Reading it as a deadline would cancel every live sign-in on the first tick.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginStartedAtUtc: nowUtc - TimeSpan.FromSeconds(60),
            pendingChallenge: new CredentialChallenge { CredentialType = "password" });

        Assert.Equal(default, session.PendingLoginChallenge!.ExpiresAt);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsTrue_WhenTheChallengeExpiryHasPassed()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginStartedAtUtc: nowUtc - TimeSpan.FromSeconds(120),
            pendingChallenge: new CredentialChallenge
            {
                CredentialType = "device-code",
                ExpiresAt = nowUtc - TimeSpan.FromSeconds(1),
            });

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenTheChallengeExpiryIsStillAhead()
    {
        // The daemon's own deadline wins over the fallback, even once the fallback would have fired.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginStartedAtUtc: nowUtc - FallbackTimeout - TimeSpan.FromSeconds(60),
            pendingChallenge: new CredentialChallenge
            {
                CredentialType = "device-code",
                ExpiresAt = nowUtc + TimeSpan.FromSeconds(120),
            });

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsTrue_WhenNoChallengeArrivedAndTheFallbackElapsed()
    {
        // The daemon neither challenged nor answered, so the session sits in LoggingIn with nothing to
        // compare against but the moment the attempt started.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginStartedAtUtc: nowUtc - FallbackTimeout - TimeSpan.FromSeconds(1));

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_ExactlyAtTheFallbackBoundary()
    {
        // Exactly at the cap is not past it - the comparison must be greater-than.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(loginStartedAtUtc: nowUtc - FallbackTimeout);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenNoLoginIsBeingTracked()
    {
        // The headless self-auth path raises no card and ends its own attempt, so it never stamps a
        // start time and this sweep must leave it alone.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(loginStartedAtUtc: null);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenTheSessionIsNoLongerLoggingIn()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginStartedAtUtc: nowUtc - FallbackTimeout - TimeSpan.FromSeconds(60),
            authState: DaemonAuthState.Authenticated);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenTheSessionIsNotActive()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginStartedAtUtc: nowUtc - FallbackTimeout - TimeSpan.FromSeconds(60),
            status: DaemonSessionStatus.Terminated);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc, FallbackTimeout);

        Assert.False(result);
    }
}
