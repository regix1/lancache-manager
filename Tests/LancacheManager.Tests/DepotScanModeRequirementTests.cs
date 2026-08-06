using LancacheManager.Controllers;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the rule both scan-mode routes apply before storing a mode. It reads only facts that survive
/// at rest, because the Steam socket is closed between crawls: a connection or login flag would
/// refuse Incremental and Full on every install. The stage keys are the ones the scan mode dropdown
/// shows for the same missing requirement, so a refused save and a greyed-out option say one thing.
/// </summary>
public class DepotScanModeRequirementTests
{
    private const string SetupKey = "management.depotMapping.modes.setupRequiredHelp";
    private const string MappingsKey = "management.depotMapping.modes.mappingsRequiredHelp";
    private const string WebApiKey = "management.depotMapping.modes.fullWebApiRequiredHelp";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnfinishedSetupRefusesBothSteamModes(bool incremental)
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: false,
            RebuildRunning: false,
            DepotMappingsFound: 5000,
            WebApiAvailable: true);

        var missing = DepotScanModeRequirement.Missing(availability, incremental);

        Assert.NotNull(missing);
        Assert.Equal(SetupKey, missing!.StageKey);
        Assert.NotEmpty(missing.Error);
    }

    [Fact]
    public void IncrementalNeedsStoredMappingsToCompareAgainst()
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: true,
            RebuildRunning: false,
            DepotMappingsFound: 0,
            WebApiAvailable: true);

        var missing = DepotScanModeRequirement.Missing(availability, incremental: true);

        Assert.NotNull(missing);
        Assert.Equal(MappingsKey, missing!.StageKey);
    }

    [Fact]
    public void IncrementalIsAllowedOnceMappingsAreStored()
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: true,
            RebuildRunning: false,
            DepotMappingsFound: 1,
            WebApiAvailable: true);

        Assert.Null(DepotScanModeRequirement.Missing(availability, incremental: true));
    }

    // A full scan empties the mapping table at the start and refills it as it goes, so the count
    // reads zero for most of the run. Judging that as an absent baseline would refuse Incremental
    // for every save made while a scan is in flight.
    [Fact]
    public void IncrementalIsAllowedWhileACrawlIsRefillingTheTable()
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: true,
            RebuildRunning: true,
            DepotMappingsFound: 0,
            WebApiAvailable: true);

        Assert.Null(DepotScanModeRequirement.Missing(availability, incremental: true));
    }

    // Incremental reads the PICS changelist over the Steam client connection and never calls the
    // Steam Web API, so a missing key must not stand in its way.
    [Fact]
    public void IncrementalDoesNotNeedTheWebApiKey()
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: true,
            RebuildRunning: false,
            DepotMappingsFound: 42,
            WebApiAvailable: false);

        Assert.Null(DepotScanModeRequirement.Missing(availability, incremental: true));
    }

    [Fact]
    public void FullNeedsTheWebApiKeyToEnumerateApps()
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: true,
            RebuildRunning: false,
            DepotMappingsFound: 5000,
            WebApiAvailable: false);

        var missing = DepotScanModeRequirement.Missing(availability, incremental: false);

        Assert.NotNull(missing);
        Assert.Equal(WebApiKey, missing!.StageKey);
    }

    // A full scan builds the baseline rather than reading one, so an empty mapping table is exactly
    // the state it exists to fix.
    [Fact]
    public void FullIsAllowedWithNoStoredMappings()
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: true,
            RebuildRunning: false,
            DepotMappingsFound: 0,
            WebApiAvailable: true);

        Assert.Null(DepotScanModeRequirement.Missing(availability, incremental: false));
    }

    // WebApiAvailable is the cached probe answer, not whether a key string is stored. A key Steam
    // rejected is still stored, so judging Full on a stored key would offer a mode this refuses.
    // The browser reads the same probe answer off the progress blob and the status route, which is
    // what keeps the dropdown and this from disagreeing.
    [Fact]
    public void FullIsRefusedWhenTheWebApiProbeFailedDespiteAStoredKey()
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: true,
            RebuildRunning: false,
            DepotMappingsFound: 5000,
            WebApiAvailable: false);

        var missing = DepotScanModeRequirement.Missing(availability, incremental: false);

        Assert.NotNull(missing);
        Assert.Equal(WebApiKey, missing!.StageKey);
    }

    // Every refusal is rendered by stage key, so one that arrives without a key leaves the browser
    // with nothing to say beyond a generic failure.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryRefusalCarriesAStageKeyAndAnEnglishReason(bool incremental)
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: false,
            RebuildRunning: false,
            DepotMappingsFound: 0,
            WebApiAvailable: false);

        var missing = DepotScanModeRequirement.Missing(availability, incremental);

        Assert.NotNull(missing);
        Assert.NotEmpty(missing!.StageKey);
        Assert.NotEmpty(missing.Error);
    }
}
