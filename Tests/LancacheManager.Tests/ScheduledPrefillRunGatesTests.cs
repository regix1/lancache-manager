using LancacheManager.Controllers;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;

namespace LancacheManager.Tests;

public class ScheduledPrefillRunGatesTests
{
    private static readonly Guid SystemUserId = ScheduledPrefillConstants.DeriveSystemUserId();
    private static readonly Guid RealUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenOnlyPersistentSystemSessionAuthenticated()
    {
        var sessions = new[]
        {
            MakeSession(SystemUserId, isPersistent: true)
        };

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(sessions, SystemUserId, out var message);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsTrue_WhenRealUserSessionActive()
    {
        var sessions = new[]
        {
            MakeSession(RealUserId, isPersistent: false)
        };

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(sessions, SystemUserId, out var message);

        Assert.True(shouldSkip);
        Assert.Equal("A manual prefill session is active", message);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsTrue_WhenSystemUserSessionIsPrefilling()
    {
        var session = MakeSession(SystemUserId, isPersistent: true, isPrefilling: true);

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions([session], SystemUserId, out var message);

        Assert.True(shouldSkip);
        Assert.Equal("A prefill is already in progress", message);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenOnlyTerminatedSessions()
    {
        var sessions = new[]
        {
            MakeSession(RealUserId, isPersistent: false, status: DaemonSessionStatus.Terminated),
            MakeSession(SystemUserId, isPersistent: true, status: DaemonSessionStatus.Terminated)
        };

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(sessions, SystemUserId, out var message);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenOnlyErrorSessions()
    {
        var sessions = new[]
        {
            MakeSession(RealUserId, isPersistent: false, status: DaemonSessionStatus.Error),
            MakeSession(SystemUserId, isPersistent: true, status: DaemonSessionStatus.Error)
        };

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(sessions, SystemUserId, out var message);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsTrue_AndSessionId_WhenPersistentSessionHealthy()
    {
        // Reuse contract: a running, authenticated persistent container is prefilled on its OWN
        // session id. The scheduler never creates a new (guest) session for this.
        var session = MakeSession(SystemUserId, isPersistent: true, authState: DaemonAuthState.Authenticated);

        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(session, out var sessionId, out var reason);

        Assert.True(runnable);
        Assert.Equal(session.Id, sessionId);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsFalse_WithReason_WhenNoPersistentSession()
    {
        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(null, out var sessionId, out var reason);

        Assert.False(runnable);
        Assert.Equal(string.Empty, sessionId);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsFalse_WhenSessionNotAuthenticated()
    {
        var session = MakeSession(SystemUserId, isPersistent: true, authState: DaemonAuthState.NotAuthenticated);

        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(session, out var sessionId, out var reason);

        Assert.False(runnable);
        Assert.Equal(string.Empty, sessionId);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsFalse_WhenSessionNeedsRelogin()
    {
        var session = MakeSession(
            SystemUserId,
            isPersistent: true,
            authState: DaemonAuthState.Authenticated,
            needsRelogin: true);

        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(session, out var sessionId, out var reason);

        Assert.False(runnable);
        Assert.Equal(string.Empty, sessionId);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void GuestPrefillValidation_AcceptsDurationHoursOneThroughThree()
    {
        foreach (var hours in new[] { 1, 2, 3 })
        {
            var isValid = GuestPrefillValidation.TryValidateDurationHours(hours, out var error);

            Assert.True(isValid);
            Assert.Equal(string.Empty, error);
        }
    }

    [Fact]
    public void GuestPrefillValidation_RejectsDurationHoursOutsideOneThroughThree()
    {
        foreach (var hours in new[] { 0, 4, -1 })
        {
            var isValid = GuestPrefillValidation.TryValidateDurationHours(hours, out var error);

            Assert.False(isValid);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    private static DaemonSession MakeSession(
        Guid userId,
        bool isPersistent,
        DaemonSessionStatus status = DaemonSessionStatus.Active,
        bool isPrefilling = false,
        DaemonAuthState authState = DaemonAuthState.Authenticated,
        bool needsRelogin = false)
    {
        return new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = userId,
            Status = status,
            IsPersistent = isPersistent,
            IsPrefilling = isPrefilling,
            AuthState = authState,
            NeedsRelogin = needsRelogin,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
    }
}
