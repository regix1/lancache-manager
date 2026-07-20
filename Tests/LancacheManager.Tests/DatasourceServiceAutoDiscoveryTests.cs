using System.Globalization;
using System.Reflection;
using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class DatasourceServiceAutoDiscoveryTests
{
    [Fact]
    public void DiscoverDatasources_RootValidWithNamedChildren_ProducesDefaultThenAlphabeticalChildren()
    {
        using var fixture = new Fixture();
        CreateAccessLog(fixture.LogPath);
        CreateHashBucket(fixture.CachePath, "00");
        CreatePair(fixture, "steam", "steam");
        CreatePair(fixture, "epic", "epic");

        var service = fixture.BuildService();

        AssertDatasources(
            service,
            ("Default", fixture.CachePath, fixture.LogPath),
            ("Epic", Path.Combine(fixture.CachePath, "epic"), Path.Combine(fixture.LogPath, "epic")),
            ("Steam", Path.Combine(fixture.CachePath, "steam"), Path.Combine(fixture.LogPath, "steam")));
    }

    [Fact]
    public void DiscoverDatasources_EmptyRoot_FallsBackToLegacyDefault()
    {
        using var fixture = new Fixture();

        var service = fixture.BuildService();

        AssertDatasources(service, ("default", fixture.CachePath, fixture.LogPath));
    }

    [Fact]
    public void DiscoverDatasources_Depth1ValidPair_UsesCacheLeafName()
    {
        using var fixture = new Fixture();
        CreatePair(fixture, "steam", "steam");

        var service = fixture.BuildService();

        AssertDatasources(
            service,
            ("Steam", Path.Combine(fixture.CachePath, "steam"), Path.Combine(fixture.LogPath, "steam")));
    }

    [Fact]
    public void DiscoverDatasources_Depth2ValidPairUnderEmptyWrapper_UsesCacheLeafName()
    {
        using var fixture = new Fixture();
        var cacheLeaf = Path.Combine(fixture.CachePath, "env", "steam");
        var logsLeaf = Path.Combine(fixture.LogPath, "env", "steam");
        CreateHashBucket(cacheLeaf, "00");
        CreateAccessLog(logsLeaf);

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", cacheLeaf, logsLeaf));
    }

    [Fact]
    public void DiscoverDatasources_Depth3ValidPairUnderNestedWrappers_UsesCacheLeafName()
    {
        using var fixture = new Fixture();
        var cacheLeaf = Path.Combine(fixture.CachePath, "env", "region", "steam");
        var logsLeaf = Path.Combine(fixture.LogPath, "env", "region", "steam");
        CreateHashBucket(cacheLeaf, "00");
        CreateAccessLog(logsLeaf);

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", cacheLeaf, logsLeaf));
    }

    [Fact]
    public void DiscoverDatasources_Depth4ValidPair_IsNotDiscovered()
    {
        using var fixture = new Fixture();
        var cacheLeaf = Path.Combine(fixture.CachePath, "a", "b", "c", "steam");
        var logsLeaf = Path.Combine(fixture.LogPath, "a", "b", "c", "steam");
        CreateHashBucket(cacheLeaf, "00");
        CreateAccessLog(logsLeaf);

        var service = fixture.BuildService();

        AssertDatasources(service, ("default", fixture.CachePath, fixture.LogPath));
    }

    [Theory]
    [InlineData("steam", "steam")]
    [InlineData("steam", "Steam")]
    [InlineData("steam", "steams")]
    public void DiscoverDatasources_Depth1Pairing_MatchesByExactCaseInsensitiveAndNormalizedName(
        string cacheLeaf, string logsLeaf)
    {
        using var fixture = new Fixture();
        CreatePair(fixture, cacheLeaf, logsLeaf);

        var service = fixture.BuildService();

        AssertDatasources(
            service,
            ("Steam", Path.Combine(fixture.CachePath, cacheLeaf), Path.Combine(fixture.LogPath, logsLeaf)));
    }

    [Theory]
    [InlineData("env", "env", "steam", "steam")]
    [InlineData("env", "ENV", "steam", "Steam")]
    [InlineData("env", "env", "steam", "steams")]
    public void DiscoverDatasources_NestedPairing_MatchesByExactCaseInsensitiveAndNormalizedNameAtEveryLevel(
        string cacheWrapper, string logsWrapper, string cacheLeaf, string logsLeaf)
    {
        using var fixture = new Fixture();
        var cacheLeafPath = Path.Combine(fixture.CachePath, cacheWrapper, cacheLeaf);
        var logsLeafPath = Path.Combine(fixture.LogPath, logsWrapper, logsLeaf);
        CreateHashBucket(cacheLeafPath, "00");
        CreateAccessLog(logsLeafPath);

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", cacheLeafPath, logsLeafPath));
    }

    [Fact]
    public void DiscoverDatasources_SingletonWrapperWithNoOwnContent_IsFollowedDespiteDifferentNames()
    {
        using var fixture = new Fixture();
        var cacheLeaf = Path.Combine(fixture.CachePath, "region", "steam");
        var logsLeaf = Path.Combine(fixture.LogPath, "environment", "steam");
        CreateHashBucket(cacheLeaf, "00");
        CreateAccessLog(logsLeaf);

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", cacheLeaf, logsLeaf));
    }

    [Fact]
    public void DiscoverDatasources_DifferentlyNamedContentReadyLeaves_AreNotPaired()
    {
        using var fixture = new Fixture();
        CreateHashBucket(Path.Combine(fixture.CachePath, "steam"), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, "epic"));

        var service = fixture.BuildService();

        AssertDatasources(service, ("default", fixture.CachePath, fixture.LogPath));
    }

    [Fact]
    public void DiscoverDatasources_MultipleUnmatchedWrapperBranches_AreNotPaired()
    {
        using var fixture = new Fixture();
        CreateHashBucket(Path.Combine(fixture.CachePath, "region1", "steam"), "00");
        CreateHashBucket(Path.Combine(fixture.CachePath, "region2", "gaming"), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, "areaA", "steam"));
        CreateAccessLog(Path.Combine(fixture.LogPath, "areaB", "gaming"));

        var service = fixture.BuildService();

        AssertDatasources(service, ("default", fixture.CachePath, fixture.LogPath));
    }

    [Fact]
    public void DiscoverDatasources_OneSidedPair_IsNotEmittedButDescendantsAreStillSearched()
    {
        using var fixture = new Fixture();
        CreateHashBucket(Path.Combine(fixture.CachePath, "mid"), "00");
        Directory.CreateDirectory(Path.Combine(fixture.LogPath, "mid"));
        var cacheLeaf = Path.Combine(fixture.CachePath, "mid", "steam");
        var logsLeaf = Path.Combine(fixture.LogPath, "mid", "steam");
        CreateHashBucket(cacheLeaf, "00");
        CreateAccessLog(logsLeaf);

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", cacheLeaf, logsLeaf));
    }

    [Fact]
    public void DiscoverDatasources_HiddenUnderscoreAndHashDirectories_AreFilteredAtEveryDepth()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.CachePath, "env"));
        Directory.CreateDirectory(Path.Combine(fixture.LogPath, "env"));
        CreateHashBucket(Path.Combine(fixture.CachePath, "env", ".hidden"), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, "env", ".hidden"));
        CreateHashBucket(Path.Combine(fixture.CachePath, "env", "_temp"), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, "env", "_temp"));
        CreateHashBucket(Path.Combine(fixture.CachePath, "env", "ab"), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, "env", "ab"));
        var cacheLeaf = Path.Combine(fixture.CachePath, "env", "steam");
        var logsLeaf = Path.Combine(fixture.LogPath, "env", "steam");
        CreateHashBucket(cacheLeaf, "00");
        CreateAccessLog(logsLeaf);

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", cacheLeaf, logsLeaf));
    }

    [Fact]
    public void DiscoverDatasources_DuplicateNameAtDeeperDepth_ShallowerCandidateWins()
    {
        using var fixture = new Fixture();
        var shallowCache = Path.Combine(fixture.CachePath, "steam");
        var shallowLogs = Path.Combine(fixture.LogPath, "steam");
        CreateHashBucket(shallowCache, "00");
        CreateAccessLog(shallowLogs);
        CreateHashBucket(Path.Combine(fixture.CachePath, "other", "steam"), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, "other", "steam"));

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", shallowCache, shallowLogs));
    }

    [Fact]
    public void DiscoverDatasources_NestedPairNamedDefault_DoesNotDuplicateRoot()
    {
        using var fixture = new Fixture();
        CreateAccessLog(fixture.LogPath);
        CreateHashBucket(fixture.CachePath, "00");
        CreateHashBucket(Path.Combine(fixture.CachePath, "default"), "01");
        CreateAccessLog(Path.Combine(fixture.LogPath, "default"));

        var service = fixture.BuildService();

        AssertDatasources(service, ("Default", fixture.CachePath, fixture.LogPath));
    }

    [Fact]
    public void DiscoverDatasources_DuplicateNameAtSameDepth_LexicallyEarlierRelativePathWins()
    {
        using var fixture = new Fixture();
        var alphaCache = Path.Combine(fixture.CachePath, "alpha", "steam");
        var alphaLogs = Path.Combine(fixture.LogPath, "alpha", "steam");
        CreateHashBucket(alphaCache, "00");
        CreateAccessLog(alphaLogs);
        CreateHashBucket(Path.Combine(fixture.CachePath, "beta", "steam"), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, "beta", "steam"));

        var service = fixture.BuildService();

        AssertDatasources(service, ("Steam", alphaCache, alphaLogs));
    }

    [Fact]
    public void DiscoverDatasources_MultipleValidCandidates_OrderIsDefaultFirstThenAlphabetical()
    {
        using var fixture = new Fixture();
        CreateAccessLog(fixture.LogPath);
        CreateHashBucket(fixture.CachePath, "00");
        CreatePair(fixture, "zeta", "zeta");
        CreatePair(fixture, "alpha", "alpha");
        CreatePair(fixture, "mid", "mid");

        var service = fixture.BuildService();

        AssertDatasources(
            service,
            ("Default", fixture.CachePath, fixture.LogPath),
            ("Alpha", Path.Combine(fixture.CachePath, "alpha"), Path.Combine(fixture.LogPath, "alpha")),
            ("Mid", Path.Combine(fixture.CachePath, "mid"), Path.Combine(fixture.LogPath, "mid")),
            ("Zeta", Path.Combine(fixture.CachePath, "zeta"), Path.Combine(fixture.LogPath, "zeta")));
    }

    [Fact]
    public void DiscoverDatasources_ExplicitDataSourcesConfigured_TakesPriorityOverValidNestedStructure()
    {
        using var fixture = new Fixture();
        CreateAccessLog(fixture.LogPath);
        CreateHashBucket(fixture.CachePath, "00");
        CreatePair(fixture, "steam", "steam");
        var alphaCache = Path.Combine(fixture.Root, "alpha-cache");
        var alphaLogs = Path.Combine(fixture.Root, "alpha-logs");
        Directory.CreateDirectory(alphaCache);
        Directory.CreateDirectory(alphaLogs);

        var service = fixture.BuildService(extra: new Dictionary<string, string?>
        {
            ["LanCache:DataSources:0:Name"] = "alpha",
            ["LanCache:DataSources:0:CachePath"] = alphaCache,
            ["LanCache:DataSources:0:LogPath"] = alphaLogs,
            ["LanCache:DataSources:0:Enabled"] = "true"
        });

        AssertDatasources(service, ("alpha", alphaCache, alphaLogs));
        Assert.Equal(DatasourceSchemeOverride.Auto, Assert.Single(service.GetDatasources()).SchemeOverride);
        Assert.Equal("auto", Assert.Single(service.GetDatasourceInfos()).SchemeOverride);
    }

    [Fact]
    public void ExplicitDatasource_SchemeOverrideFlowsIntoResolvedDatasourceAndApiInfo()
    {
        using var fixture = new Fixture();

        var service = fixture.BuildService(extra: new Dictionary<string, string?>
        {
            ["LanCache:DataSources:0:Name"] = "custom",
            ["LanCache:DataSources:0:CachePath"] = fixture.CachePath,
            ["LanCache:DataSources:0:LogPath"] = fixture.LogPath,
            ["LanCache:DataSources:0:Enabled"] = "true",
            ["LanCache:DataSources:0:SchemeOverride"] = "bare_metal"
        });

        var resolved = Assert.Single(service.GetDatasources());
        var info = Assert.Single(service.GetDatasourceInfos());
        Assert.Equal(DatasourceSchemeOverride.BareMetal, resolved.SchemeOverride);
        Assert.Equal("bare_metal", info.SchemeOverride);
    }

    [Fact]
    public void ExplicitDatasource_InvalidSchemeOverrideIsRejected()
    {
        using var fixture = new Fixture();

        var exception = Assert.Throws<ArgumentException>(() => fixture.BuildService(extra: new Dictionary<string, string?>
        {
            ["LanCache:DataSources:0:Name"] = "custom",
            ["LanCache:DataSources:0:CachePath"] = fixture.CachePath,
            ["LanCache:DataSources:0:LogPath"] = fixture.LogPath,
            ["LanCache:DataSources:0:Enabled"] = "true",
            ["LanCache:DataSources:0:SchemeOverride"] = "guess"
        }));

        Assert.Contains("auto, monolithic, bare_metal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverDatasources_AutoDiscoveryDisabled_FallsBackToLegacyIgnoringNestedStructure()
    {
        using var fixture = new Fixture();
        CreatePair(fixture, "steam", "steam");

        var service = fixture.BuildService(autoDiscover: false);

        AssertDatasources(service, ("default", fixture.CachePath, fixture.LogPath));
    }

    [Fact]
    public void DiscoverDatasources_TurkishCultureWithInternalLeaf_UsesInvariantTitleCasedName()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            using var fixture = new Fixture();
            CreatePair(fixture, "internal", "internal");

            var service = fixture.BuildService();

            AssertDatasources(
                service,
                ("Internal", Path.Combine(fixture.CachePath, "internal"), Path.Combine(fixture.LogPath, "internal")));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void DiscoverDatasources_PairedLeafWithDisallowedCharacterInName_IsSkippedAndLegacyDefaultRemains()
    {
        using var fixture = new Fixture();
        CreatePair(fixture, "steam gaming", "steam gaming");

        var service = fixture.BuildService();

        AssertDatasources(service, ("default", fixture.CachePath, fixture.LogPath));
    }

    private static void AssertDatasources(
        DatasourceService service,
        params (string Name, string CachePath, string LogsPath)[] expected)
    {
        var actual = service.GetDatasourceInfos();

        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.Equal(expected[i].CachePath, actual[i].CachePath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expected[i].LogsPath, actual[i].LogsPath, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void CreatePair(Fixture fixture, string cacheLeaf, string logsLeaf)
    {
        CreateHashBucket(Path.Combine(fixture.CachePath, cacheLeaf), "00");
        CreateAccessLog(Path.Combine(fixture.LogPath, logsLeaf));
    }

    private static void CreateAccessLog(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "access.log"), "log line");
    }

    private static void CreateHashBucket(string dir, string hex)
    {
        Directory.CreateDirectory(Path.Combine(dir, hex));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"datasource-autodiscovery-{Guid.NewGuid():N}");
            CachePath = Path.Combine(Root, "cache");
            LogPath = Path.Combine(Root, "logs");
            Directory.CreateDirectory(CachePath);
            Directory.CreateDirectory(LogPath);
        }

        public string Root { get; }
        public string CachePath { get; }
        public string LogPath { get; }

        public DatasourceService BuildService(bool autoDiscover = true, IDictionary<string, string?>? extra = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["LanCache:CachePath"] = CachePath,
                ["LanCache:LogPath"] = LogPath,
                ["LanCache:AutoDiscoverDatasources"] = autoDiscover ? "true" : "false"
            };

            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    settings[kv.Key] = kv.Value;
                }
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = Root;

            return new DatasourceService(configuration, pathResolver, NullLogger<DatasourceService>.Instance);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private class PathResolverProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IPathResolver.ResolvePath))
            {
                var path = Assert.IsType<string>(args![0]);
                return Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            }

            if (targetMethod.Name == nameof(IPathResolver.NormalizePath))
            {
                return Assert.IsType<string>(args![0]);
            }

            if (targetMethod.ReturnType == typeof(string))
            {
                return Path.Combine(Root, targetMethod.Name);
            }

            if (targetMethod.ReturnType == typeof(bool))
            {
                return true;
            }

            if (targetMethod.ReturnType == typeof(int))
            {
                return 0;
            }

            return null;
        }
    }
}
