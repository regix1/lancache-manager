using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace LancacheManager.Tests;

public sealed class DatasourceValidationTests
{
    [Fact]
    public void ExplicitDatasource_MissingRootsRemainAbsent()
    {
        using var fixture = new Fixture();
        var cachePath = Path.Combine(fixture.Root, "missing-cache");
        var logPath = Path.Combine(fixture.Root, "missing-logs");

        var service = fixture.Build(
            Datasource("Primary", cachePath, logPath, enabled: true));

        var datasource = Assert.Single(service.GetDatasources());
        Assert.Equal(DatasourceOrigin.Explicit, datasource.Origin);
        Assert.False(Directory.Exists(cachePath));
        Assert.False(Directory.Exists(logPath));
        Assert.False(Directory.Exists(Path.Combine(logPath, "http")));
        Assert.False(datasource.CacheWritable);
        Assert.False(datasource.LogsWritable);
    }

    [Fact]
    public void ExplicitDatasource_AllDisabledLoadsNoDatasourceAndCreatesNoRoots()
    {
        using var fixture = new Fixture();
        var cachePath = Path.Combine(fixture.Root, "disabled-cache");
        var logPath = Path.Combine(fixture.Root, "disabled-logs");

        var service = fixture.Build(
            Datasource("Disabled", cachePath, logPath, enabled: false));

        Assert.Equal(DatasourceOrigin.Explicit, service.Origin);
        Assert.Empty(service.GetDatasources());
        Assert.False(Directory.Exists(cachePath));
        Assert.False(Directory.Exists(logPath));
    }

    [Fact]
    public void LegacyDatasource_MissingRootsAreCreatedAtStartup()
    {
        using var fixture = new Fixture();
        var cachePath = Path.Combine(fixture.Root, "legacy-cache");
        var logPath = Path.Combine(fixture.Root, "legacy-logs");

        var service = fixture.BuildSettings(new Dictionary<string, string?>
        {
            ["LanCache:CachePath"] = cachePath,
            ["LanCache:LogPath"] = logPath,
            ["LanCache:AutoDiscoverDatasources"] = "false"
        });

        var datasource = Assert.Single(service.GetDatasources());
        Assert.Equal(DatasourceOrigin.Legacy, datasource.Origin);
        Assert.True(Directory.Exists(cachePath));
        Assert.True(Directory.Exists(logPath));
    }

    [Fact]
    public void ExplicitDatasource_CaseDuplicateNamesAreRejected()
    {
        using var fixture = new Fixture();

        var exception = Assert.Throws<ArgumentException>(() => fixture.Build(
            Datasource("Primary", Path.Combine(fixture.Root, "cache-a"), Path.Combine(fixture.Root, "logs-a"), true),
            Datasource("primary", Path.Combine(fixture.Root, "cache-b"), Path.Combine(fixture.Root, "logs-b"), true)));

        Assert.Contains("Primary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("primary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDatasource_NestedCacheRootsAreRejected()
    {
        using var fixture = new Fixture();
        var parent = Path.Combine(fixture.Root, "cache");

        var exception = Assert.Throws<ArgumentException>(() => fixture.Build(
            Datasource("Primary", parent, Path.Combine(fixture.Root, "logs-a"), true),
            Datasource("Secondary", Path.Combine(parent, "nested"), Path.Combine(fixture.Root, "logs-b"), true)));

        Assert.Contains("Primary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Secondary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDatasource_EqualLogRootsAreRejected()
    {
        using var fixture = new Fixture();
        var logs = Path.Combine(fixture.Root, "logs");

        var exception = Assert.Throws<ArgumentException>(() => fixture.Build(
            Datasource("Primary", Path.Combine(fixture.Root, "cache-a"), logs, true),
            Datasource("Secondary", Path.Combine(fixture.Root, "cache-b"), logs, true)));

        Assert.Contains("Primary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Secondary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDatasource_FilesystemRootAndNestedCacheAreRejected()
    {
        using var fixture = new Fixture();
        var filesystemRoot = Path.GetPathRoot(fixture.Root)!;

        var exception = Assert.Throws<ArgumentException>(() => fixture.Build(
            Datasource("Primary", filesystemRoot, Path.Combine(fixture.Root, "logs-a"), true),
            Datasource("Secondary", Path.Combine(fixture.Root, "cache-b"), Path.Combine(fixture.Root, "logs-b"), true)));

        Assert.Contains("Primary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Secondary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDatasource_DistinctRootsAreAccepted()
    {
        using var fixture = new Fixture();

        var service = fixture.Build(
            Datasource("Primary", Path.Combine(fixture.Root, "cache-a"), Path.Combine(fixture.Root, "logs-a"), true),
            Datasource("Secondary", Path.Combine(fixture.Root, "cache-b"), Path.Combine(fixture.Root, "logs-b"), true));

        Assert.Equal(2, service.DatasourceCount);
    }

    private static Dictionary<string, string?> Datasource(
        string name,
        string cachePath,
        string logPath,
        bool enabled)
    {
        return new Dictionary<string, string?>
        {
            ["Name"] = name,
            ["CachePath"] = cachePath,
            ["LogPath"] = logPath,
            ["Enabled"] = enabled.ToString()
        };
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), $"ds-impl-b-validation-{Guid.NewGuid():N}"))
                .FullName;
        }

        public string Root { get; }

        public DatasourceService Build(params Dictionary<string, string?>[] datasources)
        {
            var settings = new Dictionary<string, string?>();
            for (var index = 0; index < datasources.Length; index++)
            {
                foreach (var setting in datasources[index])
                {
                    settings[$"LanCache:DataSources:{index}:{setting.Key}"] = setting.Value;
                }
            }

            return BuildSettings(settings);
        }

        public DatasourceService BuildSettings(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();
            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = Root;
            return new DatasourceService(
                configuration,
                pathResolver,
                NullLogger<DatasourceService>.Instance);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
