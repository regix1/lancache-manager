using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for UserSessionActivityReconciler.ReconcileOnceAsync: the pass that closes the gap
/// SessionService's create/revoke event reports can't reach - a session already present in the database
/// before this process started never gets an individual create-time report, so without this reconciler it
/// would sit permanently absent from the activity registry despite being perfectly valid.
/// </summary>
public class UserSessionActivityReconcilerTests
{
    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    /// <summary>Inert notifier - these tests assert on the registry's own snapshot, not broadcasts.</summary>
    private sealed class NullNotifier : ISignalRNotificationService
    {
        public Task NotifyAllAsync(string eventName, object? data = null) => Task.CompletedTask;
        public void NotifyAllFireAndForget(string eventName, object? data = null) { }
        public Task NotifyOperationFailedAsync(string eventName, IOperationComplete failedEvent) => Task.CompletedTask;
        public Task NotifyPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyEpicPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifySteamHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyEpicHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyBattleNetPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToBattleNetPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyBattleNetHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyRiotPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToRiotPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyRiotHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyXboxPrefillClientAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task SendToXboxPrefillClientRawAsync(string connectionId, string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyXboxHubAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyAdminAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyGuestAsync(string eventName, object? data = null) => Task.CompletedTask;
        public Task NotifyGroupAsync(string groupName, string eventName, object? data = null) => Task.CompletedTask;
    }

    private static DbContextOptions<AppDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session_activity_reconciler_{Guid.NewGuid():N}")
            .Options;

    private static UserSession NewSession(bool revoked, DateTime expiresAtUtc)
    {
        var now = DateTime.UtcNow;
        return new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = Guid.NewGuid().ToString("N"),
            SessionType = SessionType.Guest,
            IpAddress = "127.0.0.1",
            UserAgent = "test",
            CreatedAtUtc = now,
            ExpiresAtUtc = expiresAtUtc,
            LastSeenAtUtc = now,
            IsRevoked = revoked
        };
    }

    [Fact]
    public async Task ReconcileOnceAsync_MarksOnlyValidSessionsPresent()
    {
        var options = NewInMemoryOptions();
        var factory = new InMemoryDbContextFactory(options);

        var validSession = NewSession(revoked: false, expiresAtUtc: DateTime.UtcNow.AddDays(1));
        var revokedSession = NewSession(revoked: true, expiresAtUtc: DateTime.UtcNow.AddDays(1));
        var expiredSession = NewSession(revoked: false, expiresAtUtc: DateTime.UtcNow.AddDays(-1));

        await using (var seed = factory.CreateDbContext())
        {
            seed.UserSessions.AddRange(validSession, revokedSession, expiredSession);
            await seed.SaveChangesAsync();
        }

        var registry = new ActivityRegistry(new NullNotifier(), NullLogger<ActivityRegistry>.Instance);

        await UserSessionActivityReconciler.ReconcileOnceAsync(factory, registry, CancellationToken.None);

        var snapshot = await registry.GetSnapshotAsync();
        var presentIds = snapshot.Activities
            .Where(a => a.Domain == ActivityDomains.UserSession && a.Aspect == ActivityAspects.Present)
            .Select(a => a.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(validSession.Id.ToString(), presentIds);
        Assert.DoesNotContain(revokedSession.Id.ToString(), presentIds);
        Assert.DoesNotContain(expiredSession.Id.ToString(), presentIds);
    }

    /// <summary>
    /// The exact bug this reconciler exists to close: a session that predates this process (so
    /// SessionService's create-time report never fired for it) still ends up present, because the
    /// reconciler discovers it straight from the database rather than depending on a prior report.
    /// </summary>
    [Fact]
    public async Task ReconcileOnceAsync_TracksSessionNeverIndividuallyReported()
    {
        var options = NewInMemoryOptions();
        var factory = new InMemoryDbContextFactory(options);

        var preExistingSession = NewSession(revoked: false, expiresAtUtc: DateTime.UtcNow.AddDays(1));
        await using (var seed = factory.CreateDbContext())
        {
            seed.UserSessions.Add(preExistingSession);
            await seed.SaveChangesAsync();
        }

        var registry = new ActivityRegistry(new NullNotifier(), NullLogger<ActivityRegistry>.Instance);
        // No ReportAsync call for preExistingSession.Id was ever made - simulating a session created
        // by a previous process run, before this one started.

        await UserSessionActivityReconciler.ReconcileOnceAsync(factory, registry, CancellationToken.None);

        var snapshot = await registry.GetSnapshotAsync();
        Assert.Contains(
            snapshot.Activities,
            a => a.Domain == ActivityDomains.UserSession
                && a.Key == preExistingSession.Id.ToString()
                && a.Aspect == ActivityAspects.Present);
    }

    [Fact]
    public async Task ReconcileOnceAsync_RevokedAfterFirstPass_IsRemovedOnNextPass()
    {
        var options = NewInMemoryOptions();
        var factory = new InMemoryDbContextFactory(options);

        var session = NewSession(revoked: false, expiresAtUtc: DateTime.UtcNow.AddDays(1));
        await using (var seed = factory.CreateDbContext())
        {
            seed.UserSessions.Add(session);
            await seed.SaveChangesAsync();
        }

        var registry = new ActivityRegistry(new NullNotifier(), NullLogger<ActivityRegistry>.Instance);
        await UserSessionActivityReconciler.ReconcileOnceAsync(factory, registry, CancellationToken.None);

        await using (var revoke = factory.CreateDbContext())
        {
            var tracked = await revoke.UserSessions.FindAsync(session.Id);
            Assert.NotNull(tracked);
            tracked!.IsRevoked = true;
            await revoke.SaveChangesAsync();
        }

        await UserSessionActivityReconciler.ReconcileOnceAsync(factory, registry, CancellationToken.None);

        var snapshot = await registry.GetSnapshotAsync();
        Assert.DoesNotContain(
            snapshot.Activities,
            a => a.Domain == ActivityDomains.UserSession && a.Key == session.Id.ToString());
    }
}
