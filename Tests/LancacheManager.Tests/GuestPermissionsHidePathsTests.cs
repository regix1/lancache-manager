using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Where the cache and the logs live is told to an account holder and to nobody else, the rule
/// <c>GET /api/system/config</c> already keeps for a guest. Every guest's page also reads
/// <c>GET /api/system/permissions</c> for whether the Docker socket is there
/// (<c>Web/src/contexts/DockerSocketContext.tsx</c>), so that answer keeps the socket and leaves out the
/// folder paths for a guest.
/// </summary>
public sealed class GuestPermissionsHidePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-guest-permissions-{Guid.NewGuid():N}");

    [Fact]
    public void AGuest_IsToldWhetherTheDockerSocketIsThereButNotWhereTheCacheOrLogsLive()
    {
        var cachePath = Path.Combine(_root, "cache");
        var logPath = Path.Combine(_root, "logs");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(logPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "default",
                ["LanCache:DataSources:0:CachePath"] = cachePath,
                ["LanCache:DataSources:0:LogPath"] = logPath,
                ["LanCache:DataSources:0:Enabled"] = "true"
            })
            .Build();
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = _root;
        var controller = new SystemController(
            stateService: null!,
            configuration,
            NullLogger<SystemController>.Instance,
            paths,
            cacheClearingService: null!,
            steamKit2Service: null!,
            new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance),
            notifications: null!,
            userPreferencesService: null!,
            capabilityService: null!,
            nginxLogRotationService: null!,
            cacheManagementService: null!,
            dbContextFactory: null!,
            claimWindow: null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items =
                    {
                        ["Session"] = new UserSession
                        {
                            Id = Guid.NewGuid(),
                            SessionTokenHash = Guid.NewGuid().ToString("N"),
                            SessionType = SessionType.Guest,
                            CreatedAtUtc = DateTime.UtcNow,
                            ExpiresAtUtc = DateTime.UtcNow.AddHours(6),
                            LastSeenAtUtc = DateTime.UtcNow
                        }
                    }
                }
            }
        };

        var answer = Assert.IsType<SystemPermissionsResponse>(Assert.IsType<OkObjectResult>(controller.GetPermissions().Result).Value);

        var wrong = new List<string>();
        if (!string.IsNullOrEmpty(answer.Cache.Path))
            wrong.Add($"the cache folder {answer.Cache.Path}");
        if (!string.IsNullOrEmpty(answer.Logs.Path))
            wrong.Add($"the logs folder {answer.Logs.Path}");
        Assert.True(wrong.Count == 0, "a guest's GET /api/system/permissions tells it " + string.Join(" and ", wrong));
        Assert.Equal(paths.IsDockerSocketAvailable(), answer.DockerSocket.Available);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
