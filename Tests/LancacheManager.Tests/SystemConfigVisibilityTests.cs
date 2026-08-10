using System.Reflection;
using System.Security.Claims;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// What <c>GET /api/system/config</c> tells a caller who has not signed in.
///
/// The app fetches this response on its very first paint, above the auth gate and before any
/// session can exist, and refuses to render anything at all until it arrives. So the route has to
/// stay open to a caller with no identity. What it does not have to do is hand that caller the
/// cache directory, the logs directory, the data directory and the writability of each one, which
/// together describe the host's filesystem layout and answer nothing the sign-in screen asks.
///
/// The two halves are tested separately: the layout is gone for a caller with no identity, the
/// fields the first paint actually reads are not, and a signed-in caller still gets the paths, so
/// this stays a split rather than a deletion.
///
/// The controller is exercised directly rather than through the pipeline, so the returned
/// <see cref="ObjectResult"/> carries the same body in every hosting environment.
/// </summary>
public sealed class SystemConfigVisibilityTests : IDisposable
{
    private readonly string _root;
    private readonly SystemController _controller;

    public SystemConfigVisibilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-system-config-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(_root, "alpha-cache");
        var logPath = Path.Combine(_root, "alpha-logs");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(logPath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = cachePath,
                ["LanCache:DataSources:0:LogPath"] = logPath,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["NginxLogRotation:Enabled"] = "false"
            })
            .Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        var stateService = CreateStateService(_root);
        var datasourceService = new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);
        var capabilityService = new DatasourceCapabilityService(datasourceService);
        var nginxLogRotationService = new NginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            configuration,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            pathResolver);
        var cacheClearingService = new CacheClearingService(
            NullLogger<CacheClearingService>.Instance,
            notifications: null!,
            configuration,
            pathResolver,
            stateService,
            rustProcessHelper: null!,
            datasourceService,
            operationTracker: null!,
            dbContextFactory: null!,
            gameCacheDetectionService: null!);
        var cacheManagementService = new CacheManagementService(
            configuration,
            NullLogger<CacheManagementService>.Instance,
            pathResolver,
            rustProcessHelper: null!,
            nginxLogRotationService,
            datasourceService,
            stateService,
            dbContextFactory: null!,
            gameCacheDetectionService: null!,
            operationTracker: null!,
            notifications: null!,
            envFileReader: null!,
            conflictChecker: null!,
            capabilityService);

        _controller = new SystemController(
            stateService,
            configuration,
            NullLogger<SystemController>.Instance,
            pathResolver,
            cacheClearingService,
            steamKit2Service: null!,
            datasourceService,
            notifications: null!,
            userPreferencesService: null!,
            capabilityService,
            nginxLogRotationService,
            cacheManagementService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnAnonymousCaller_IsNotToldWhereTheCacheLivesAsync()
    {
        var body = await ReadConfigAsync();

        Assert.Equal(string.Empty, body.CachePath);
        Assert.Equal(string.Empty, body.LogsPath);
        Assert.Equal(string.Empty, body.DataPath);
        Assert.False(body.CacheWritable);
        Assert.False(body.LogsWritable);

        var datasource = Assert.Single(body.DataSources);
        Assert.Equal(string.Empty, datasource.CachePath);
        Assert.Equal(string.Empty, datasource.LogsPath);
        Assert.False(datasource.CacheWritable);
        Assert.False(datasource.LogsWritable);
    }

    [Fact]
    public async Task AnAnonymousCaller_StillReceivesWhatTheFirstPaintReadsAsync()
    {
        var body = await ReadConfigAsync();

        // The one field the app reads from this response before a session exists, plus the
        // datasource list itself, which stays present with its paths blanked rather than dropped.
        Assert.False(string.IsNullOrEmpty(body.TimeZone));
        var datasource = Assert.Single(body.DataSources);
        Assert.Equal("alpha", datasource.Name);
        Assert.True(datasource.Enabled);
    }

    [Fact]
    public async Task ASignedInCaller_StillReceivesTheCachePathsAsync()
    {
        _controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Session"));
        Assert.True(_controller.HttpContext.User.Identity?.IsAuthenticated);

        var body = await ReadConfigAsync();

        Assert.False(string.IsNullOrEmpty(body.CachePath));
        Assert.False(string.IsNullOrEmpty(body.LogsPath));
        Assert.False(string.IsNullOrEmpty(body.DataPath));

        var datasource = Assert.Single(body.DataSources);
        Assert.False(string.IsNullOrEmpty(datasource.CachePath));
        Assert.False(string.IsNullOrEmpty(datasource.LogsPath));
    }

    private async Task<SystemConfigResponse> ReadConfigAsync()
    {
        var result = await _controller.GetConfigAsync();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<SystemConfigResponse>(ok.Value);
    }
}
