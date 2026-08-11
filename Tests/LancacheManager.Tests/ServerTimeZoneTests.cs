using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Scheduling;
using Microsoft.Extensions.Configuration;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the id the server reports as its own zone. A schedule stores the zone it was saved with and the
/// editor decides whether it overrides the server by comparing that string to this one, so renaming the
/// server's id makes schedules that name the very same zone read as overriding it.
/// </summary>
public sealed class ServerTimeZoneTests
{
    // T-1: a container started with TZ=UTC reports "UTC". That is the spelling every schedule saved so far
    // carries, and the two are compared by name.
    [Fact]
    public void ConfiguredUtc_IsReportedAsWritten()
    {
        Assert.Equal("UTC", ServerTimeZone.IanaId(ConfigurationFor("UTC")));
    }

    // T-2: a Windows zone id is still translated, which is what the translation is there for.
    [Fact]
    public void ConfiguredWindowsZone_IsTranslatedToIana()
    {
        Assert.Equal("America/New_York", ServerTimeZone.IanaId(ConfigurationFor("Eastern Standard Time")));
    }

    // T-3: an id that is already IANA is kept exactly as written.
    [Fact]
    public void ConfiguredIanaZone_IsKeptAsWritten()
    {
        Assert.Equal("Europe/Berlin", ServerTimeZone.IanaId(ConfigurationFor("Europe/Berlin")));
    }

    // T-4: the translation both branches share keeps UTC spelled "UTC" whichever branch asked, so the
    // configured zone and the machine's own zone can never end up naming the same zone differently.
    // The fallback argument is deliberately a value neither branch would accept, so the assertion can
    // only pass by way of the UTC rule itself.
    [Fact]
    public void UtcKeepsItsName_WhicheverBranchAsks()
    {
        Assert.Equal("UTC", ServerTimeZone.IanaId("UTC", "should-not-be-reached"));
        Assert.Equal("UTC", ServerTimeZone.IanaId("utc", "should-not-be-reached"));
    }

    // T-5: pins the runtime behaviour the rule above exists for. The zone database renames UTC to
    // "Etc/UTC" on translation, and a schedule saved as "UTC" is compared to the server's id by name.
    // If a future runtime stops renaming it, this test says so.
    [Fact]
    public void TranslatingUtcRenamesIt()
    {
        Assert.True(TimeZoneInfo.TryConvertWindowsIdToIanaId("UTC", out var translated));
        Assert.Equal("Etc/UTC", translated);
    }

    // T-6: a zone this runtime cannot translate comes back as the caller's own fallback, which is what
    // lets the two branches keep different last resorts while sharing the UTC rule.
    [Fact]
    public void UntranslatableZone_FallsBackToTheCallersChoice()
    {
        Assert.Equal("Europe/Berlin", ServerTimeZone.IanaId("Europe/Berlin", "Europe/Berlin"));
        Assert.Equal("UTC", ServerTimeZone.IanaId("Not A Real Zone", "UTC"));
    }

    // T-7: the TimeZone setting is read when TZ is absent, so a non-Docker install that names its zone in
    // appsettings is answered the same way as a container.
    [Fact]
    public void ConfiguredTimeZoneSetting_IsUsedWhenTzIsAbsent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TimeZone"] = "Europe/Berlin" })
            .Build();

        Assert.Equal("Europe/Berlin", ServerTimeZone.IanaId(configuration));
    }

    // T-8: with nothing configured the machine's own zone is reported, and it is a zone this runtime can
    // actually resolve. The flat "UTC" answer this replaced told a bare-metal install in New York that its
    // server ran on UTC.
    [Fact]
    public void NothingConfigured_ReportsAZoneThisMachineCanResolve()
    {
        var reported = ServerTimeZone.IanaId(new ConfigurationBuilder().Build());

        Assert.False(string.IsNullOrWhiteSpace(reported));
        Assert.NotNull(ScheduleTiming.ResolveTimeZone(reported));
    }

    // T-9: a configured id no runtime can resolve is not passed on. Kept as written it reaches every reader
    // that formats a time and throws there instead, once per formatted instant, and a schedule preview walks
    // 3,700 days. The answer is the machine's own zone, which is what an install that configured nothing
    // gets, and it must be resolvable.
    [Fact]
    public void UnresolvableConfiguredZone_FallsBackToAZoneThisMachineHas()
    {
        var reported = ServerTimeZone.IanaId(ConfigurationFor("Middle/Earth"));

        Assert.NotEqual("Middle/Earth", reported);
        Assert.Equal(ServerTimeZone.IanaId(new ConfigurationBuilder().Build()), reported);
        Assert.NotNull(ScheduleTiming.ResolveTimeZone(reported));
    }

    // T-10: the decision is settled once per distinct id rather than re-derived per call. An unresolvable id
    // costs a thrown TimeZoneNotFoundException every time it is settled, and this is asked once per reader
    // per formatted instant. Repeat calls must agree and must stay cheap; a wall-clock budget would be
    // flaky, so the observable contract is that the answer is stable and resolvable every time.
    [Fact]
    public void UnresolvableConfiguredZone_AnswersTheSameWayEveryTime()
    {
        var configuration = ConfigurationFor("Middle/Earth");
        var first = ServerTimeZone.IanaId(configuration);

        for (var i = 0; i < 2000; i++)
        {
            Assert.Equal(first, ServerTimeZone.IanaId(configuration));
        }
    }

    // T-11: two differently configured readers are answered independently. The cache is keyed on the
    // configured text, so caching one server's zone cannot hand it to a caller that configured another.
    [Fact]
    public void DifferentConfiguredZones_AreAnsweredIndependently()
    {
        Assert.Equal("Europe/Berlin", ServerTimeZone.IanaId(ConfigurationFor("Europe/Berlin")));
        Assert.Equal("America/New_York", ServerTimeZone.IanaId(ConfigurationFor("Eastern Standard Time")));
        Assert.Equal("Europe/Berlin", ServerTimeZone.IanaId(ConfigurationFor("Europe/Berlin")));
    }

    private static IConfiguration ConfigurationFor(string timeZone) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TZ"] = timeZone })
            .Build();
}
