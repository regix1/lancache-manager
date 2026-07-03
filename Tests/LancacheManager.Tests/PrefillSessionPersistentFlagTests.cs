using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the Phase 2 "persistent" flag on prefill sessions: the entity column and its
/// derivation at insert/reactivate time from the deterministic container-naming convention
/// (PrefillDaemonServiceBase never touched - containerName is already a parameter PrefillSessionService
/// receives), the admin-terminate guard that must never tear down a persistent container, and the
/// history DTO mapping that carries the flag to the frontend. Runs against the EF Core InMemory
/// provider (mirrors ServiceEvictionGateTests) so the real PrefillSessionService code path executes.
/// </summary>
public class PrefillSessionPersistentFlagTests
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

    private static DbContextOptions<AppDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_persistent_flag_{Guid.NewGuid():N}")
            .Options;

    private static PrefillSessionService NewSessionService(DbContextOptions<AppDbContext> options)
        => new PrefillSessionService(
            new InMemoryDbContextFactory(options),
            NullLogger<PrefillSessionService>.Instance);

    private static DaemonSession MakeDaemonSession(string id, bool isPersistent, bool needsRelogin = false, bool isTemporary = false)
        => new DaemonSession
        {
            Id = id,
            UserId = Guid.NewGuid(),
            ContainerName = isPersistent ? "steam-daemon-persistent" : $"steam-daemon-{id}",
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            IsPersistent = isPersistent,
            IsTemporary = isTemporary,
            NeedsRelogin = needsRelogin,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };

    // === (a) DaemonSessionDto.FromSession propagates the three flags - site 13, verify-only ===

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void FromSession_PropagatesPersistentTemporaryAndReloginFlags(bool isPersistent, bool isTemporary, bool needsRelogin)
    {
        var session = MakeDaemonSession("abc123", isPersistent, needsRelogin, isTemporary);

        var dto = DaemonSessionDto.FromSession(session);

        Assert.Equal(isPersistent, dto.IsPersistent);
        Assert.Equal(isTemporary, dto.IsTemporary);
        Assert.Equal(needsRelogin, dto.NeedsRelogin);
    }

    // === (b) admin-terminate guard: persistent sessions are never terminatable ===

    [Fact]
    public void IsTerminatableByAdmin_ReturnsFalse_ForPersistentSession()
    {
        var session = MakeDaemonSession("persist-1", isPersistent: true);
        Assert.False(PrefillSessionService.IsTerminatableByAdmin(session));
    }

    [Fact]
    public void IsTerminatableByAdmin_ReturnsTrue_ForGuestSession()
    {
        var session = MakeDaemonSession("guest-1", isPersistent: false);
        Assert.True(PrefillSessionService.IsTerminatableByAdmin(session));
    }

    [Fact]
    public void IsTerminatableByAdmin_ReturnsFalse_ForPersistentContainerName_EvenWhenFlagIsFalse()
    {
        // Defense-in-depth (Cursor #1.3): the container-name convention is checked directly too, so a
        // session whose IsPersistent flag wasn't set correctly still can't be torn down if its name
        // matches the deterministic persistent-container convention.
        var session = MakeDaemonSession("persist-2", isPersistent: false);
        session.ContainerName = "steam-daemon-persistent";

        Assert.False(PrefillSessionService.IsTerminatableByAdmin(session));
    }

    [Fact]
    public void TerminateAllFilter_ExcludesOnlyPersistentSessions_FromAMixedList()
    {
        // Regression guard for PrefillAdminController.TerminateAllAsync: before the guard, this
        // filter did not exist and terminate-all iterated every live session, persistent included.
        var sessions = new List<DaemonSession>
        {
            MakeDaemonSession("guest-1", isPersistent: false),
            MakeDaemonSession("persist-1", isPersistent: true),
            MakeDaemonSession("guest-2", isPersistent: false)
        };

        var terminatable = sessions.Where(PrefillSessionService.IsTerminatableByAdmin).ToList();

        Assert.Equal(2, terminatable.Count);
        Assert.DoesNotContain(terminatable, s => s.IsPersistent);
        Assert.Contains(terminatable, s => s.Id == "guest-1");
        Assert.Contains(terminatable, s => s.Id == "guest-2");
    }

    // === (c) history mapping carries IsPersistent; wire name confirmed camelCase `isPersistent`
    // (PrefillSessionDto has no snake_case JsonPropertyName attributes - global camelCase applies) ===

    [Fact]
    public void PrefillSessionDto_FromEntity_CarriesIsPersistent_WhenSetOnEntity()
    {
        var entity = new PrefillSession
        {
            SessionId = "sess-1",
            CreatedBySessionId = Guid.NewGuid(),
            ContainerName = "steam-daemon-persistent",
            Status = PrefillSessionStatus.Terminated,
            IsPersistent = true,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        var dto = PrefillSessionDto.FromEntity(entity, liveSession: null);

        Assert.True(dto.IsPersistent);
    }

    [Fact]
    public void PrefillSessionDto_FromEntity_CarriesIsPersistent_FromLiveSession_WhenEntityFlagIsStale()
    {
        // A live session wins over a stale/legacy DB row: if the container is currently known to be
        // persistent, the history card must say so even if the persisted row predates the column
        // (defaults false).
        var entity = new PrefillSession
        {
            SessionId = "sess-2",
            CreatedBySessionId = Guid.NewGuid(),
            Status = PrefillSessionStatus.Active,
            IsPersistent = false,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        var liveSession = MakeDaemonSession("sess-2", isPersistent: true);

        var dto = PrefillSessionDto.FromEntity(entity, liveSession);

        Assert.True(dto.IsPersistent);
    }

    [Fact]
    public void PrefillSessionDto_FromEntity_IsPersistentFalse_ForLegacyGuestRow()
    {
        // Legacy/guest rows with no live match and IsPersistent=false must not be mislabeled.
        var entity = new PrefillSession
        {
            SessionId = "sess-3",
            CreatedBySessionId = Guid.NewGuid(),
            ContainerName = "steam-daemon-a1b2c3d4e5f6a1b2",
            Status = PrefillSessionStatus.Terminated,
            IsPersistent = false,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };

        var dto = PrefillSessionDto.FromEntity(entity, liveSession: null);

        Assert.False(dto.IsPersistent);
    }

    // === Insert path: CreateSessionAsync/ReactivateSessionAsync derive IsPersistent from container
    // name (the reliable signal post-Phase-1; PrefillDaemonServiceBase:1089 is the untouched caller
    // that already names persistent containers "{ContainerPrefix}persistent") ===

    [Fact]
    public async Task CreateSessionAsync_SetsIsPersistentTrue_ForDeterministicPersistentContainerName()
    {
        var options = NewInMemoryOptions();
        var service = NewSessionService(options);

        var created = await service.CreateSessionAsync(
            sessionId: "sess-create-persist",
            createdBySessionId: Guid.NewGuid(),
            containerId: "container-1",
            containerName: "steam-daemon-persistent",
            expiresAt: DateTime.UtcNow.AddHours(1),
            platform: "Steam");

        Assert.True(created.IsPersistent);

        await using var verify = new AppDbContext(options);
        var stored = await verify.PrefillSessions.FirstAsync(s => s.SessionId == "sess-create-persist");
        Assert.True(stored.IsPersistent);
    }

    [Fact]
    public async Task CreateSessionAsync_SetsIsPersistentFalse_ForGuestContainerName()
    {
        var options = NewInMemoryOptions();
        var service = NewSessionService(options);

        var created = await service.CreateSessionAsync(
            sessionId: "sess-create-guest",
            createdBySessionId: Guid.NewGuid(),
            containerId: "container-2",
            containerName: "steam-daemon-a1b2c3d4e5f6a1b2",
            expiresAt: DateTime.UtcNow.AddHours(1),
            platform: "Steam");

        Assert.False(created.IsPersistent);
    }

    [Fact]
    public async Task ReactivateSessionAsync_SetsIsPersistentTrue_ForDeterministicPersistentContainerName_OnFreshInsert()
    {
        var options = NewInMemoryOptions();
        var service = NewSessionService(options);

        var reactivated = await service.ReactivateSessionAsync(
            sessionId: "sess-reactivate-persist",
            createdBySessionId: Guid.NewGuid(),
            containerId: "container-3",
            containerName: "xbox-daemon-persistent",
            expiresAt: DateTime.UtcNow.AddHours(1),
            platform: "Xbox");

        Assert.True(reactivated.IsPersistent);
    }

    [Fact]
    public async Task ReactivateSessionAsync_RefreshesIsPersistent_OnExistingOrphanedRow()
    {
        var options = NewInMemoryOptions();
        var service = NewSessionService(options);

        // Seed an orphaned row the way MarkOrphansAsync would leave it: created as a guest session,
        // now being re-adopted under the persistent container name after a manager restart.
        await using (var seed = new AppDbContext(options))
        {
            seed.PrefillSessions.Add(new PrefillSession
            {
                SessionId = "sess-reactivate-existing",
                CreatedBySessionId = Guid.NewGuid(),
                ContainerName = "epic-daemon-oldrandomsuffix",
                Status = PrefillSessionStatus.Orphaned,
                IsPersistent = false,
                CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
                ExpiresAtUtc = DateTime.UtcNow.AddHours(-1)
            });
            await seed.SaveChangesAsync();
        }

        var reactivated = await service.ReactivateSessionAsync(
            sessionId: "sess-reactivate-existing",
            createdBySessionId: Guid.NewGuid(),
            containerId: "container-4",
            containerName: "epic-daemon-persistent",
            expiresAt: DateTime.UtcNow.AddHours(1),
            platform: "Epic");

        Assert.Equal(PrefillSessionStatus.Active, reactivated.Status);
        Assert.True(reactivated.IsPersistent);
    }
}
