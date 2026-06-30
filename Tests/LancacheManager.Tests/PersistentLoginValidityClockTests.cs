using LancacheManager.Controllers;
using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Pure (no-Docker) tests for the persistent-login validity clock: the anchor formula that re-stamps a
/// running session's expiry on a validity change, and the display-time cap that keeps the shown re-login
/// date from outliving the daemon's real token.
/// </summary>
public class PersistentLoginValidityClockTests
{
    private static readonly DateTime CreatedAt = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(90)]
    [InlineData(365)]
    public void ComputePersistentExpiry_IsCreatedAtPlusValidityDays(int days)
    {
        var expiry = PrefillDaemonServiceBase.ComputePersistentExpiry(CreatedAt, days);

        Assert.Equal(CreatedAt.AddDays(days), expiry);
    }

    [Fact]
    public void ComputePersistentExpiry_IsIdempotent_ForSameInputs()
    {
        var first = PrefillDaemonServiceBase.ComputePersistentExpiry(CreatedAt, 30);
        var second = PrefillDaemonServiceBase.ComputePersistentExpiry(CreatedAt, 30);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeEffectiveRelogin_ReturnsToken_WhenTokenIsEarlier()
    {
        var expiresAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTimeOffset token = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var effective = PersistentPrefillController.ComputeEffectiveRelogin(expiresAt, token);

        Assert.Equal(token.UtcDateTime, effective);
    }

    [Fact]
    public void ComputeEffectiveRelogin_ReturnsExpiresAt_WhenTokenIsLater()
    {
        var expiresAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTimeOffset token = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        var effective = PersistentPrefillController.ComputeEffectiveRelogin(expiresAt, token);

        Assert.Equal(expiresAt, effective);
    }

    [Fact]
    public void ComputeEffectiveRelogin_ReturnsExpiresAt_WhenTokenIsNull()
    {
        var expiresAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var effective = PersistentPrefillController.ComputeEffectiveRelogin(expiresAt, null);

        Assert.Equal(expiresAt, effective);
    }
}
