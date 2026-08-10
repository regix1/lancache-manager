using System.Reflection;
using System.Security.Claims;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// What <c>GET /api/system/setup</c> tells a caller who has not signed in.
///
/// The endpoint answers two different questions in one response. Whether first-run setup is still
/// outstanding has to be answerable before a session exists, because the app asks it on the very
/// first paint to decide between the wizard and the sign-in screen. Where the database lives, which
/// port it listens on, which database name and which username, answers none of that, and sending it
/// to anyone who can reach the port hands out the shape of the deployment for free.
///
/// So the two halves are tested separately: the connection target is gone for a caller with no
/// identity, the completion answer is not, and a signed-in caller still gets both. The third test is
/// the one that keeps this a split rather than a deletion, since the wizard's confirmation screen
/// reads all four fields.
///
/// The controller is exercised directly rather than through the pipeline, so the returned
/// <see cref="ObjectResult"/> carries the same body in every hosting environment.
/// </summary>
public sealed class SetupStatusVisibilityTests : IDisposable
{
    private readonly string _root;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly SystemController _controller;

    public SetupStatusVisibilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-setup-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)_pathResolver).Root = _root;

        // A credentials file is what makes the connection target resolvable at all, in either
        // postgres mode, so it stands in for a configured installation.
        File.WriteAllText(
            _pathResolver.GetPostgresCredentialsPath(),
            """{"host":"db.lan","port":5433,"database":"lancache","username":"lancache"}""");

        _stateService = CreateStateService(_root);
        _controller = new SystemController(
            _stateService,
            new ConfigurationBuilder().Build(),
            NullLogger<SystemController>.Instance,
            _pathResolver,
            cacheClearingService: null!,
            steamKit2Service: null!,
            datasourceService: null!,
            notifications: null!,
            userPreferencesService: null!,
            capabilityService: null!,
            nginxLogRotationService: null!,
            cacheManagementService: null!)
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
    public void AnAnonymousCaller_IsNotToldWhereTheDatabaseIs()
    {
        var body = ReadSetupStatus();

        Assert.Null(body.PostgresHost);
        Assert.Null(body.PostgresPort);
        Assert.Null(body.PostgresDatabase);
        Assert.Null(body.PostgresUser);
    }

    [Fact]
    public void AnAnonymousCaller_StillLearnsWhetherSetupIsComplete()
    {
        _stateService.SetSetupCompleted(true);

        var body = ReadSetupStatus();

        Assert.True(body.IsCompleted);
        Assert.True(body.SetupCompleted);
    }

    [Fact]
    public void ASignedInCaller_StillReceivesTheConnectionTarget()
    {
        _controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Session"));
        Assert.True(_controller.HttpContext.User.Identity?.IsAuthenticated);

        var body = ReadSetupStatus();

        Assert.NotNull(body.PostgresHost);
        Assert.NotNull(body.PostgresDatabase);
        Assert.NotNull(body.PostgresUser);
    }

    private SetupStatusResponse ReadSetupStatus()
    {
        var result = _controller.GetSetupStatus();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<SetupStatusResponse>(ok.Value);
    }
}
