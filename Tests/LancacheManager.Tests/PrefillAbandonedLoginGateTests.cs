using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;

namespace LancacheManager.Tests;

/// <summary>
/// Proves the abandoned-login gate:
/// <see cref="PrefillSessionExpiryGates.ShouldCancelAbandonedLogin"/> ends a sign-in nobody came back
/// to answer, and leaves every other session alone. The session owns one absolute deadline so the
/// sweep never reconstructs a second one from elapsed time.
/// </summary>
public class PrefillAbandonedLoginGateTests
{
    private static DaemonSession MakeLoggingInSession(
        DateTime? loginExpiresAtUtc,
        CredentialChallenge? pendingChallenge = null,
        DaemonAuthState authState = DaemonAuthState.LoggingIn,
        DaemonSessionStatus status = DaemonSessionStatus.Active)
    {
        return new DaemonSession
        {
            AuthState = authState,
            Status = status,
            LoginExpiresAtUtc = loginExpiresAtUtc,
            PendingLoginChallenge = pendingChallenge,
        };
    }

    [Theory]
    [InlineData(DaemonAuthState.UsernameRequired)]
    [InlineData(DaemonAuthState.PasswordRequired)]
    [InlineData(DaemonAuthState.TwoFactorRequired)]
    [InlineData(DaemonAuthState.SteamGuardRequired)]
    [InlineData(DaemonAuthState.DeviceConfirmationRequired)]
    [InlineData(DaemonAuthState.AuthorizationUrlRequired)]
    public void ShouldCancelAbandonedLogin_ReturnsTrue_WhenAPromptWentUnansweredPastItsDeadline(DaemonAuthState prompt)
    {
        // A challenge moves the session to its prompt state, so a person who closed the tab at a
        // prompt leaves the session there, not in LoggingIn.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(loginExpiresAtUtc: nowUtc - TimeSpan.FromSeconds(1), authState: prompt);

        Assert.True(PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc));
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenNoDeadlineIsTracked()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginExpiresAtUtc: null,
            pendingChallenge: new CredentialChallenge { CredentialType = "password" });

        Assert.Equal(default, session.PendingLoginChallenge!.ExpiresAt);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsTrue_WhenTheChallengeExpiryHasPassed()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginExpiresAtUtc: nowUtc - TimeSpan.FromSeconds(1),
            pendingChallenge: new CredentialChallenge
            {
                CredentialType = "device-code",
                ExpiresAt = nowUtc - TimeSpan.FromSeconds(1),
            });

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenTheChallengeExpiryIsStillAhead()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginExpiresAtUtc: nowUtc + TimeSpan.FromSeconds(120),
            pendingChallenge: new CredentialChallenge
            {
                CredentialType = "device-code",
                ExpiresAt = nowUtc + TimeSpan.FromSeconds(120),
            });

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsTrue_WhenNoChallengeArrivedAndTheDeadlinePassed()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginExpiresAtUtc: nowUtc - TimeSpan.FromSeconds(1));

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.True(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_ExactlyAtTheDeadline()
    {
        // Exactly at the cap is not past it - the comparison must be greater-than.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(loginExpiresAtUtc: nowUtc);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenNoLoginIsBeingTracked()
    {
        // The headless self-auth path raises no card and ends its own attempt, so it never stamps a
        // start time and this sweep must leave it alone.
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(loginExpiresAtUtc: null);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenTheSessionIsNoLongerLoggingIn()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginExpiresAtUtc: nowUtc - TimeSpan.FromSeconds(60),
            authState: DaemonAuthState.Authenticated);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.False(result);
    }

    [Fact]
    public void ShouldCancelAbandonedLogin_ReturnsFalse_WhenTheSessionIsNotActive()
    {
        var nowUtc = DateTime.UtcNow;
        var session = MakeLoggingInSession(
            loginExpiresAtUtc: nowUtc - TimeSpan.FromSeconds(60),
            status: DaemonSessionStatus.Terminated);

        var result = PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(session, nowUtc);

        Assert.False(result);
    }
}
