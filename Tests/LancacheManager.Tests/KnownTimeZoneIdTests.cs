using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the zone-name gate in front of the dashboard's hourly bucketing: one IANA spelling per
/// zone, and a refusal for names this server cannot resolve.
/// </summary>
public class KnownTimeZoneIdTests
{
    /// <summary>
    /// .NET settles "Eastern Standard Time" into a real zone, but the same zone must land on the
    /// same cache key from every OS, so the Windows spelling is normalised to the IANA one. UTC is
    /// spelled the same in both and rides on every UTC reader's request, so it must survive
    /// untouched.
    /// </summary>
    [Fact]
    public void KnownTimeZoneId_SettlesOnTheIanaSpelling()
    {
        Assert.Equal("America/New_York", DashboardBatchService.KnownTimeZoneId("Eastern Standard Time"));
        Assert.Equal("UTC", DashboardBatchService.KnownTimeZoneId("UTC"));
        Assert.Equal("Asia/Kolkata", DashboardBatchService.KnownTimeZoneId("Asia/Kolkata"));
    }

    /// <summary>
    /// A zone name this server's tzdata does not carry would leave the hourly section with no
    /// clock to bucket on, so it has to be refused here and replaced with the server's own zone.
    /// Nothing but null can signal that to the caller.
    /// </summary>
    [Fact]
    public void KnownTimeZoneId_RefusesAZoneThisServerCannotResolve()
    {
        Assert.Null(DashboardBatchService.KnownTimeZoneId("Not/AZone"));
    }
}
