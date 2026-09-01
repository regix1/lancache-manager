using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamKit2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the PICS depot graph being let go once its cache entry expires. The parsed graph runs to
/// hundreds of megabytes, so keeping the expired one until some later reader happens to replace it
/// leaves a stale copy resident for as long as nothing asks for the mappings.
/// </summary>
public sealed class PicsDataServiceCacheTests : IDisposable
{
    private const string MappingFileName = "pics_depot_mappings.json";

    private readonly string _root;
    private readonly IPathResolver _pathResolver;
    private readonly string _mappingFile;

    public PicsDataServiceCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-pics-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)_pathResolver).Root = _root;

        var picsDirectory = _pathResolver.GetPicsDirectory();
        Directory.CreateDirectory(picsDirectory);
        _mappingFile = Path.Combine(picsDirectory, MappingFileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadFromJsonAsync_AfterExpiry_ReturnsANewlyParsedGraph()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);

        var first = await service.LoadFromJsonAsync();
        var whileFresh = await service.LoadFromJsonAsync();

        Assert.NotNull(first);
        Assert.Same(first, whileFresh);

        clock.Advance(TimeSpan.FromMinutes(6));
        var afterExpiry = await service.LoadFromJsonAsync();

        Assert.NotNull(afterExpiry);
        Assert.NotSame(first, afterExpiry);
        Assert.Equal(first!.DepotMappings!.Count, afterExpiry!.DepotMappings!.Count);
    }

    [Fact]
    public async Task LoadFromJsonAsync_WhenTheExpiredEntryCannotBeReplaced_StillLetsTheGraphGo()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);

        Assert.NotNull(await service.LoadFromJsonAsync());

        // A depot scan truncates the mapping file while it rewrites it, so the reload that follows
        // the expiry has nothing to cache. The expired graph must be gone even then, which is what
        // separates releasing it up front from overwriting it once a replacement has been parsed.
        File.WriteAllText(_mappingFile, string.Empty);
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await service.LoadFromJsonAsync());
        Assert.Null(typeof(PicsDataService)
            .GetField("_cachedPicsData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service));
    }

    private PicsDataService CreateService(TimeProvider timeProvider)
    {
        File.WriteAllText(
            _mappingFile,
            """
            {
              "metadata": {
                "lastUpdated": "2026-01-01T00:00:00Z",
                "totalMappings": 2,
                "version": "1.0",
                "nextUpdateDue": "2026-01-02T00:00:00Z",
                "lastChangeNumber": 42
              },
              "depotMappings": {
                "731": {
                  "ownerId": 730,
                  "depotName": "Counter-Strike 2 Content",
                  "appIds": [730],
                  "appNames": ["Counter-Strike 2"],
                  "source": "SteamKit2-PICS",
                  "discoveredAt": "2026-01-01T00:00:00Z"
                },
                "441": {
                  "ownerId": 440,
                  "depotName": "Team Fortress 2 Content",
                  "appIds": [440],
                  "appNames": ["Team Fortress 2"],
                  "source": "SteamKit2-PICS",
                  "discoveredAt": "2026-01-01T00:00:00Z"
                }
              }
            }
            """);

        return new PicsDataService(
            NullLogger<PicsDataService>.Instance,
            DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>(),
            _pathResolver,
            StateTestMethods.CreateStateService(_root),
            timeProvider);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
