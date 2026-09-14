using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Services.Xbox;

namespace LancacheManager.Tests;

public class DeadlineContractTests
{
    [Fact]
    public void EffectiveRelogin_PreservesIndependentManagerAndTokenBounds()
    {
        var expiry = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        Assert.Equal(expiry, PersistentPrefillController.ComputeEffectiveRelogin(expiry, null));
        Assert.Equal(expiry, PersistentPrefillController.ComputeEffectiveRelogin(expiry, new DateTimeOffset(expiry.AddHours(1))));
        Assert.Equal(expiry.AddHours(-1), PersistentPrefillController.ComputeEffectiveRelogin(expiry, new DateTimeOffset(expiry.AddHours(-1))));
    }

    [Fact]
    public void XboxChallenge_PublishesAbsoluteExpiryAndPollInterval()
    {
        var expiry = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        var challenge = new XboxDeviceCodeChallenge { ExpiresAtUtc = expiry, Interval = 5 };
        var json = JsonSerializer.SerializeToElement(challenge, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expiry, json.GetProperty("expiresAtUtc").GetDateTime());
        Assert.Equal(5, json.GetProperty("interval").GetInt32());
        Assert.False(json.TryGetProperty("expiresIn", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Session_PreservesExpiryAndReloginState(bool persistent)
    {
        var expiry = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        var session = new DaemonSession { ExpiresAt = expiry, IsPersistent = persistent, NeedsRelogin = persistent };
        var json = JsonSerializer.SerializeToElement(DaemonSessionDto.FromSession(session), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expiry, json.GetProperty("expiresAt").GetDateTime());
        Assert.Equal(persistent, json.GetProperty("needsRelogin").GetBoolean());
        Assert.False(json.TryGetProperty("timeRemainingSeconds", out _));
    }

    [Fact]
    public void PersistentSession_PublishesEffectiveExpiryWithoutRemainingSeconds()
    {
        var expiry = new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc);
        var session = new PersistentPrefillSessionDto
        {
            SessionId = "session",
            Service = default,
            IsRunning = true,
            IsAuthenticated = true,
            AuthExpiresAtUtc = expiry,
            CreatedAtUtc = expiry.AddDays(-1),
            NeedsRelogin = false
        };
        var json = JsonSerializer.SerializeToElement(session, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expiry, json.GetProperty("authExpiresAtUtc").GetDateTime());
        Assert.False(json.TryGetProperty("authTimeRemainingSeconds", out _));
    }
}
