using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A session its own user signed out of is stored as signed out, so the session list can tell it apart from
/// one an admin revoked. The end state is recorded once: revoking the row again later keeps it as it was.
/// </summary>
public sealed class SessionSignOutTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;

    public SessionSignOutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-session-sign-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            })
            .Build();
        _apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            _configuration,
            pathResolver: null!);
    }

    [Fact]
    public async Task SignOutIsStoredApartFromRevokeAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database);
        var signedOut = await NewSessionAsync(service);
        var revoked = await NewSessionAsync(service);

        Assert.True(await service.RevokeSessionAsync(signedOut, signedOut: true));
        Assert.True(await service.RevokeSessionAsync(revoked));

        await using var context = database.Factory.CreateDbContext();
        var signedOutRow = await context.UserSessions.SingleAsync(s => s.Id == signedOut);
        Assert.True(signedOutRow.IsRevoked);
        Assert.True(signedOutRow.SignedOut);
        var revokedRow = await context.UserSessions.SingleAsync(s => s.Id == revoked);
        Assert.True(revokedRow.IsRevoked);
        Assert.False(revokedRow.SignedOut);
    }

    /// <summary>
    /// An admin revoking from a stale list, or "Clear all guests" retrying a guest whose prefill cleanup
    /// failed, revokes a row that already ended. That must not turn "signed out" into "revoked" or move its time.
    /// </summary>
    [Fact]
    public async Task RevokingASignedOutSessionAgainKeepsItSignedOutAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database);
        var sessionId = await NewSessionAsync(service);

        Assert.True(await service.RevokeSessionAsync(sessionId, signedOut: true));
        DateTime? firstEndedAt;
        await using (var context = database.Factory.CreateDbContext())
        {
            firstEndedAt = (await context.UserSessions.SingleAsync(s => s.Id == sessionId)).RevokedAtUtc;
        }
        Assert.NotNull(firstEndedAt);

        Assert.True(await service.RevokeSessionAsync(sessionId));

        await using (var context = database.Factory.CreateDbContext())
        {
            var stored = await context.UserSessions.SingleAsync(s => s.Id == sessionId);
            Assert.True(stored.IsRevoked);
            Assert.True(stored.SignedOut);
            Assert.Equal(firstEndedAt, stored.RevokedAtUtc);
        }
    }

    /// <summary>
    /// The page reads these two names off the session list: it matches sessions to accounts by
    /// <c>accountId</c> and shows "signed out" from <c>signedOut</c>.
    /// </summary>
    [Fact]
    public void SessionRecordSendsAccountIdAndSignedOut()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var accountId = Guid.NewGuid();

        var withAccount = JsonSerializer.Serialize(
            new SessionRecord { Id = Guid.NewGuid(), AccountId = accountId, SignedOut = true }, options);
        var withoutAccount = JsonSerializer.Serialize(new SessionRecord { Id = Guid.NewGuid() }, options);

        Assert.Contains($"\"accountId\":\"{accountId}\"", withAccount);
        Assert.Contains("\"signedOut\":true", withAccount);
        Assert.DoesNotContain("accountId", withoutAccount);
    }

    private async Task<Guid> NewSessionAsync(SessionService service)
    {
        var created = await service.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);
        return created!.Value.Session.Id;
    }

    private SessionService NewSessionService(TestDatabase database) =>
        new(
            database.Factory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            _configuration);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
