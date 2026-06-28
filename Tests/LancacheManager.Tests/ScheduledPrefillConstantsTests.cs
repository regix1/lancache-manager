using LancacheManager.Infrastructure.Services.ScheduledPrefill;

namespace LancacheManager.Tests;

public class ScheduledPrefillConstantsTests
{
    private static readonly Guid ArbitraryGuid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void DeriveSystemUserId_ReturnsSameGuid_OnRepeatedCalls()
    {
        var first = ScheduledPrefillConstants.DeriveSystemUserId();
        var second = ScheduledPrefillConstants.DeriveSystemUserId();

        Assert.Equal(first, second);
    }

    [Fact]
    public void DeriveSystemUserId_IsNotEmpty()
    {
        var systemUserId = ScheduledPrefillConstants.DeriveSystemUserId();

        Assert.NotEqual(Guid.Empty, systemUserId);
    }

    [Fact]
    public void DeriveSystemUserId_DiffersFromArbitraryGuid()
    {
        var systemUserId = ScheduledPrefillConstants.DeriveSystemUserId();

        Assert.NotEqual(ArbitraryGuid, systemUserId);
    }
}
