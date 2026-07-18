using LancacheManager.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class DatasourceCapabilityServiceTests
{
    [Theory]
    [InlineData("access.log", CacheKeyScheme.SupportedMonolithic, true, true, true, true, true, true)]
    [InlineData("steam-access.log", CacheKeyScheme.ObservedBareMetal, true, false, true, true, false, false)]
    [InlineData("access.log,steam-access.log", CacheKeyScheme.Mixed, true, false, false, false, false, false)]
    [InlineData("", CacheKeyScheme.Unknown, false, false, false, false, true, false)]
    public void GetCapabilities_UsesCurrentLogEvidence(
        string sourceFiles,
        CacheKeyScheme expectedScheme,
        bool expectedCanIngest,
        bool expectedCanRewriteAllActiveLogs,
        bool expectedCanInspectCacheStructure,
        bool expectedCanMapLogicalObjects,
        bool expectedCanSignalLogReopen,
        bool expectedCanTrackLiveSpeed)
    {
        var (logPath, datasource, service) = CreateSubject(sourceFiles);

        try
        {
            var capabilities = service.GetCapabilities(datasource);

            Assert.Equal(expectedScheme, capabilities.CacheKeyScheme);
            Assert.Equal(expectedCanIngest, capabilities.CanIngest);
            Assert.Equal(expectedCanRewriteAllActiveLogs, capabilities.CanRewriteAllActiveLogs);
            Assert.Equal(expectedCanInspectCacheStructure, capabilities.CanInspectCacheStructure);
            Assert.True(capabilities.CanClearWholeCacheRoot);
            Assert.Equal(expectedCanMapLogicalObjects, capabilities.CanMapLogicalObjects);
            Assert.Equal(expectedCanSignalLogReopen, capabilities.CanSignalLogReopen);
            Assert.Equal(expectedCanTrackLiveSpeed, capabilities.CanTrackLiveSpeed);
        }
        finally
        {
            Directory.Delete(logPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("access.log", "monolithic")]
    [InlineData("steam-access.log", "bare_metal")]
    public void GetKeySchemeWireValue_ReturnsUnambiguousScheme(string sourceFiles, string expectedWireValue)
    {
        var (logPath, datasource, service) = CreateSubject(sourceFiles);

        try
        {
            Assert.Equal(expectedWireValue, service.GetKeySchemeWireValue(datasource));
        }
        finally
        {
            Directory.Delete(logPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("access.log,steam-access.log")]
    [InlineData("")]
    public void GetKeySchemeWireValue_RefusesAmbiguousOrUnknownEvidence(string sourceFiles)
    {
        var (logPath, datasource, service) = CreateSubject(sourceFiles);

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => service.GetKeySchemeWireValue(datasource));

            Assert.Contains("mixed or unknown cache-key evidence", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(logPath, recursive: true);
        }
    }

    private static (string LogPath, ResolvedDatasource Datasource, DatasourceCapabilityService Service) CreateSubject(
        string sourceFiles)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "lcm-capabilities", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logPath);

        foreach (var sourceFile in sourceFiles.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            File.WriteAllText(Path.Combine(logPath, sourceFile), string.Empty);
        }

        var datasource = new ResolvedDatasource
        {
            Name = "test",
            LogPath = logPath,
            ConfiguredLogPath = logPath
        };
        var service = new DatasourceCapabilityService(
            datasourceService: null!,
            NullLogger<DatasourceCapabilityService>.Instance);

        return (logPath, datasource, service);
    }
}
