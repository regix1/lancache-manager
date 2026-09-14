using System.Net;
using System.Text.Json;
using LancacheManager.Core.Services.EpicMapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class EpicApiDirectClientTests
{
    [Theory]
    [InlineData("2026-09-01T12:30:00Z")]
    [InlineData("2026-09-01T14:30:00+02:00")]
    [InlineData("2026-09-01T12:30:00.0000000Z")]
    public async Task TokenExchanges_PreserveAbsoluteExpiry(string expiry)
    {
        using var handler = new TokenHandler(expiry, 3600);
        using var http = new HttpClient(handler);
        var client = new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance);
        var before = DateTime.UtcNow;
        EpicOAuthTokens[] tokens = [
            await client.ExchangeAuthCodeAsync("code"),
            await client.RefreshTokenAsync("refresh"),
            await client.ExchangeCodeAsync("exchange")
        ];
        var after = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            Assert.Equal(new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc), token.ExpiresAt);
            Assert.Equal(DateTimeKind.Utc, token.ExpiresAt.Kind);
            Assert.InRange(token.RefreshExpiresAt, before.AddHours(1), after.AddHours(1));
        }
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData("", 3600)]
    [InlineData("invalid", 3600)]
    [InlineData("2026-09-01T12:30:00", 3600)]
    [InlineData("99999-09-01T12:30:00Z", 3600)]
    [InlineData("2026-09-01T12:30:00Z", 0)]
    [InlineData("2026-09-01T12:30:00Z", -1)]
    public async Task TokenExchanges_RejectInvalidExpiry(string? expiry, int refreshSeconds)
    {
        using var handler = new TokenHandler(expiry, refreshSeconds);
        using var http = new HttpClient(handler);
        var client = new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExchangeAuthCodeAsync("code"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RefreshTokenAsync("refresh"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExchangeCodeAsync("exchange"));
    }

    private sealed class TokenHandler(string? expiry, int refreshSeconds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = new Dictionary<string, object?>
            {
                ["access_token"] = "access",
                ["refresh_token"] = "refresh",
                ["expires_at"] = expiry,
                ["expires_in"] = 999999,
                ["refresh_expires"] = refreshSeconds
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(fields))
            });
        }
    }
}
