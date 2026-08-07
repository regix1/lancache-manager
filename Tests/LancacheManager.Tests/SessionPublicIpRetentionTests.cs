using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// UpdateClientInfoAsync runs on every client-info POST, including reloads where the server-side
/// public IP lookup can come back empty (rate limited, offline, or not yet resolved). A null result
/// on one of those calls must not overwrite a public IP address a previous, successful call already
/// stored.
/// </summary>
public class SessionPublicIpRetentionTests
{
    [Fact]
    public async Task UpdateClientInfoAsync_NullPublicIp_KeepsThePreviouslyStoredAddress()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session-public-ip-{Guid.NewGuid():N}")
            .Options;

        var sessionId = Guid.NewGuid();
        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.UserSessions.Add(new UserSession
            {
                Id = sessionId,
                SessionTokenHash = "test-token-hash",
                SessionType = SessionType.Admin,
                IpAddress = "192.168.1.50",
                UserAgent = "test-agent",
                PublicIpAddress = "93.184.216.34",
                IsRevoked = false,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        // Only the context factory is exercised: UpdateClientInfoAsync reads nothing else off the
        // service, and every other constructor argument is stored without being touched
        // (SessionService.cs).
        var sessionService = new SessionService(
            new InMemoryDbContextFactory(options),
            apiKeyService: null!,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            configuration: null!);

        await sessionService.UpdateClientInfoAsync(
            sessionId,
            publicIpAddress: null,
            countryCode: null,
            countryName: null,
            regionName: null,
            city: null,
            timezone: null,
            ispName: null,
            screenResolution: null,
            browserLanguage: null);

        await using var verifyContext = new AppDbContext(options);
        var persisted = await verifyContext.UserSessions.FindAsync(sessionId);
        Assert.NotNull(persisted);
        Assert.Equal("93.184.216.34", persisted!.PublicIpAddress);
    }

    /// <summary>
    /// Retention only applies to an address that is still public. A private address stored before
    /// the classifier rejected IPv4-mapped LAN ranges must be cleared by the next empty lookup,
    /// otherwise it never goes away on an install whose outbound lookup is blocked.
    /// </summary>
    [Fact]
    public async Task UpdateClientInfoAsync_NullPublicIp_ClearsAStoredPrivateAddress()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session-public-ip-private-{Guid.NewGuid():N}")
            .Options;

        var sessionId = Guid.NewGuid();
        await using (var seedContext = new AppDbContext(options))
        {
            seedContext.UserSessions.Add(new UserSession
            {
                Id = sessionId,
                SessionTokenHash = "test-token-hash",
                SessionType = SessionType.Admin,
                IpAddress = "192.168.1.50",
                UserAgent = "test-agent",
                PublicIpAddress = "::ffff:192.168.1.50",
                IsRevoked = false,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            await seedContext.SaveChangesAsync();
        }

        var sessionService = new SessionService(
            new InMemoryDbContextFactory(options),
            apiKeyService: null!,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            configuration: null!);

        await sessionService.UpdateClientInfoAsync(
            sessionId,
            publicIpAddress: null,
            countryCode: null,
            countryName: null,
            regionName: null,
            city: null,
            timezone: null,
            ispName: null,
            screenResolution: null,
            browserLanguage: null);

        await using var verifyContext = new AppDbContext(options);
        var persisted = await verifyContext.UserSessions.FindAsync(sessionId);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.PublicIpAddress);
    }

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
}
