using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A forced cache file scan with every datasource disabled has nothing to walk. The waiting run's start
/// must refuse at once with the reason, so the waiting card fails in the reader's language instead of
/// parking as busy and ending skipped half a minute later.
/// </summary>
public sealed class CacheSizeScanRefusalTests
{
    [Fact]
    public async Task AForcedScanWithNoEnabledDatasource_FailsAtOnceWithTheReasonKeyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-scan-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "alpha",
                    ["LanCache:DataSources:0:CachePath"] = "cache",
                    ["LanCache:DataSources:0:LogPath"] = "logs",
                    ["LanCache:DataSources:0:Enabled"] = "false"
                })
                .Build();
            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = root;
            var datasources = new DatasourceService(
                configuration, pathResolver, NullLogger<DatasourceService>.Instance);
            var service = CacheFileCountContractTests.NewServiceForCountState(
                pathResolver: pathResolver,
                datasourceService: datasources);

            var start = service.StartCacheSizeScanInBackgroundAsync(
                new RunNotice(NotificationMode.All, RunTrigger.Manual));

            var refusal = await Assert.ThrowsAsync<ValidationException>(
                () => start.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("management.datasources.noActiveDatasources", refusal.StageKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
