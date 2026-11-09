using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

// Regression coverage for the fresh-install depot viability decision. With no depot mappings in the
// database or the JSON snapshot and no saved PICS change number, an incremental crawl has nothing to
// diff against, so viability must report a required full scan. That lets the scheduler take its
// existing graceful skip branch instead of starting a crawl that throws on boot.
public class SteamKit2ViabilityTests
{
    [Fact]
    public void HasUsableBaseline_IsFalse_WhenNoMappingsAndNoChangeNumber()
    {
        var baseline = new SteamKit2Service.DepotBaseline(0, 0, 0);

        Assert.False(baseline.HasUsableBaseline);
    }

    [Theory]
    [InlineData(1, 0, 42)]
    [InlineData(0, 1, 42)]
    [InlineData(5, 5, 100)]
    public void HasUsableBaseline_IsTrue_WhenAnyBaselineSignalPresent(int dbCount, int jsonCount, int changeNumber)
    {
        var baseline = new SteamKit2Service.DepotBaseline(dbCount, jsonCount, (uint)changeNumber);

        Assert.True(baseline.HasUsableBaseline);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 42)]
    public void IncompleteBaselineRequiresFullScan(int dbCount, int jsonCount, int changeNumber)
    {
        Assert.False(new SteamKit2Service.DepotBaseline(dbCount, jsonCount, (uint)changeNumber).HasUsableBaseline);
    }

    [Theory]
    [InlineData(1, 1, true, true)]
    [InlineData(1, 0, true, false)]
    [InlineData(0, 1, true, false)]
    [InlineData(1, 1, false, false)]
    public void CommittedBaselineRequiresMatchingCatalogs(int dbCount, int jsonCount, bool cursorMatches, bool expected)
    {
        Assert.Equal(expected, new SteamKit2Service.DepotBaseline(dbCount, jsonCount, 42, cursorMatches, true).HasUsableBaseline);
    }

    [Fact]
    public void FreshInstall_ProducesNonViableSkip_WithoutErrorOrChangeNumber()
    {
        var baseline = new SteamKit2Service.DepotBaseline(0, 0, 0);

        // A cached "viable" answer must not be reusable while the baseline is absent, so the fresh
        // install falls through to the needs-initial-data result rather than reusing a stale cache.
        Assert.False(SteamKit2Service.ShouldReuseCachedViability(cachedRequiresFullScan: false, baseline.HasUsableBaseline));

        var result = SteamKit2Service.BuildNeedsInitialDataResult();

        // These field values are exactly what routes the scheduler to its graceful skip branch: a
        // required full scan with no Error set (a non-empty Error would instead be read as a Steam
        // connection failure).
        Assert.False(result.IsViable);
        Assert.True(result.WillTriggerFullScan);
        Assert.Null(result.Error);
        Assert.Equal(0u, result.LastChangeNumber);
        Assert.Equal(0u, result.ChangeGap);
    }

    [Theory]
    [InlineData(true, false, false)]  // No cached answer is reusable after the baseline disappears.
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]   // cached viable and baseline present: reuse
    [InlineData(false, false, false)] // cached viable but baseline gone: must not reuse
    public void ShouldReuseCachedViability_GuardsAgainstStaleViableReuse(
        bool cachedRequiresFullScan,
        bool hasUsableBaseline,
        bool expected)
    {
        Assert.Equal(expected, SteamKit2Service.ShouldReuseCachedViability(cachedRequiresFullScan, hasUsableBaseline));
    }

    // The schedules list picks PendingFullScan off the service by name and type
    // (ServiceScheduleRegistry.MapConfigurableService, the same reflection route SupportsNotifications
    // takes). Rename the property or change its type and that lookup returns null instead of failing,
    // so every schedule would quietly report no pending full scan and the Steam Game Mapping card
    // would lose its way back to the prompt. Pin both here.
    [Fact]
    public void PendingFullScan_IsReachableByTheNameAndTypeTheScheduleListReads()
    {
        var property = typeof(SteamKit2Service).GetProperty(
            "PendingFullScan",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(property);
        Assert.Equal(typeof(FullScanRequirement), property!.PropertyType);
    }

    // A shutdown that lands while Steam is asked for changes must reach the scheduler as a
    // cancellation, which it reports as a stop, not as a failed depot-mapping run.
    [Fact]
    public async Task ShutdownDuringTheViabilityCheck_ReportsNoFailedRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "lcm-steam-viability", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            Directory.CreateDirectory(paths.GetPicsDirectory());
            var state = StateTestMethods.CreateStateService(root);
            state.UpdateState(saved => saved.SetupCompleted = true);
            var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, null!, paths, state);
            pics.WritePicsJsonFile("""
                {"metadata":{"lastChangeNumber":100,"version":"2.8","lastUpdated":"2026-01-01T00:00:00Z"},
                 "depotMappings":{"10":{"ownerId":2,"appIds":[2],"appNames":["Owner"],"discoveredAt":"2026-01-01T00:00:00Z"}}}
                """);
            var tracker = DispatchProxy.Create<IUnifiedOperationTracker, CallCountingTracker>();
            using var run = new CancellationTokenSource();

            var service = (ShutdownDuringCheckService)RuntimeHelpers.GetUninitializedObject(typeof(ShutdownDuringCheckService));
            service.Run = run;
            SetField(service, "_logger", NullLogger.Instance);
            SetField(service, "_initialized", true);
            SetField(service, "_isRunning", true);
            SetField(service, "_cancellationTokenSource", new CancellationTokenSource());
            SetField(service, "_baselineLock", new object());
            SetField(service, "_crawlIncrementalMode", true);
            SetField(service, "_stateService", state);
            SetField(service, "_picsDataService", pics);
            SetField(service, "_operationTracker", tracker);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunOnceAsync(run.Token));
            Assert.Equal(0, ((CallCountingTracker)(object)tracker).Calls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void SetField(object instance, string name, object value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }

    /// <summary>Never constructed: the test builds it uninitialized, so the base arguments are unused.</summary>
    private sealed class ShutdownDuringCheckService()
        : SteamKit2Service(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!)
    {
        public CancellationTokenSource Run { get; set; } = null!;

        public Task RunOnceAsync(CancellationToken stoppingToken) => ExecuteWorkAsync(stoppingToken);

        internal override Task<(uint CurrentChangeNumber, bool RequiresFullScan)> CheckChangesAsync(uint changeNumberToCheck, CancellationToken ct)
        {
            Run.Cancel();
            return Task.FromCanceled<(uint CurrentChangeNumber, bool RequiresFullScan)>(ct);
        }
    }

    /// <summary>Counts every tracker call; a run that reports anything registers an operation first.</summary>
    private class CallCountingTracker : NullReturningProxy
    {
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            return base.Invoke(targetMethod, args);
        }
    }
}
