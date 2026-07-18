namespace LancacheManager.Tests;

public sealed class KeySchemeLaunchFreshnessContractTests
{
    [Fact]
    public void SharedRemovalRunnerBuildsArgumentsAfterExecutionTimeRevalidation()
    {
        var source = ReadSource("CacheManagementService.cs");
        var runner = source.IndexOf("private async Task<TReport> RunRustRemovalProcessAsync", StringComparison.Ordinal);
        var revalidation = source.IndexOf("_capabilityService.CheckAllCanMapLogicalObjects()", runner, StringComparison.Ordinal);
        var factoryInvocation = source.IndexOf("var startInfo = createStartInfo()", revalidation, StringComparison.Ordinal);
        var launch = source.IndexOf("ExecuteTrackedProcessWithProgressEventsAsync", factoryInvocation, StringComparison.Ordinal);

        Assert.True(runner >= 0, "shared removal runner is missing");
        Assert.True(revalidation > runner, "shared removal runner must revalidate capabilities");
        Assert.True(factoryInvocation > revalidation, "process arguments must be built after revalidation");
        Assert.True(launch > factoryInvocation, "freshly built process arguments must be used for launch");
    }

    [Theory]
    [InlineData("CacheManagementService.SteamRemoval.cs")]
    [InlineData("CacheManagementService.EpicRemoval.cs")]
    [InlineData("CacheManagementService.NamedRemoval.cs")]
    [InlineData("CacheManagementService.ServiceRemoval.cs")]
    public void RemovalFlavorsResolveKeySchemeInsideDeferredLaunchFactory(string fileName)
    {
        var source = ReadSource(fileName);
        var runnerCall = source.IndexOf("RunRustRemovalProcessAsync", StringComparison.Ordinal);
        var factory = source.IndexOf("() =>", runnerCall, StringComparison.Ordinal);
        var scheme = source.IndexOf("_capabilityService.GetKeySchemeWireValue(datasource)", factory, StringComparison.Ordinal);

        Assert.True(runnerCall >= 0, $"{fileName} does not use the shared removal runner");
        Assert.True(factory > runnerCall, $"{fileName} must pass a deferred ProcessStartInfo factory");
        Assert.True(scheme > factory, $"{fileName} must resolve --key-scheme inside the deferred factory");
    }

    [Fact]
    public void CorruptionDetectionResolvesSchemeAfterAwaitedStartingNotification()
    {
        var source = ReadSource("CorruptionDetectionService.cs");
        var reportMethod = source.IndexOf("private async Task<DatasourceCorruptionReport> GetReportForDatasourceAsync", StringComparison.Ordinal);
        var startingNotification = source.IndexOf("await RelayProgressAsync", reportMethod, StringComparison.Ordinal);
        var scheme = source.IndexOf("_capabilityService.GetKeySchemeWireValue(datasource)", startingNotification, StringComparison.Ordinal);
        var startInfo = source.IndexOf("var startInfo = detectionMethod switch", scheme, StringComparison.Ordinal);
        var launch = source.IndexOf("ExecuteTrackedProcessWithProgressAsync", startInfo, StringComparison.Ordinal);

        Assert.True(reportMethod >= 0, "corruption datasource launch method is missing");
        Assert.True(startingNotification > reportMethod, "corruption detection must emit its starting notification");
        Assert.True(scheme > startingNotification, "scheme must be resolved after the awaited notification");
        Assert.True(startInfo > scheme, "corruption ProcessStartInfo must use the fresh scheme");
        Assert.True(launch > startInfo, "corruption process must launch from the fresh ProcessStartInfo");
    }

    [Fact]
    public void GameDetectionResolvesSchemeWithoutAwaitBeforeLaunch()
    {
        var source = ReadSource("GameCacheDetectionService.cs");
        var datasourceLoop = source.IndexOf("foreach (var datasource in datasources)", StringComparison.Ordinal);
        var scheme = source.IndexOf("var keyScheme = _capabilityService.GetKeySchemeWireValue(datasource)", datasourceLoop, StringComparison.Ordinal);
        var startInfo = source.IndexOf("var startInfo = _rustProcessHelper.CreateProcessStartInfo", scheme, StringComparison.Ordinal);
        var launchStatement = source.IndexOf("var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync", startInfo, StringComparison.Ordinal);

        Assert.True(datasourceLoop >= 0, "game detection datasource loop is missing");
        Assert.True(scheme > datasourceLoop, "game detection must resolve each datasource's current scheme");
        Assert.True(startInfo > scheme, "game detection ProcessStartInfo must use the fresh scheme");
        Assert.True(launchStatement > startInfo, "game detection process launch is missing");
        Assert.DoesNotContain("await ", source[scheme..launchStatement], StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine(root, "Api", "LancacheManager", "Core", "Services", fileName));
    }
}
