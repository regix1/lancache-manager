using System.Security.Claims;
using LancacheManager.Controllers;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class DisabledAuthKeyGateTests : IDisposable
{
    private readonly string _root;
    private readonly string _apiKeyPath;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;
    private readonly AuthenticationHelper _authenticationHelper;

    public DisabledAuthKeyGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-disabled-auth-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _apiKeyPath = Path.Combine(_root, "api_key.txt");
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = _apiKeyPath,
                ["Security:EnableAuthentication"] = "false"
            })
            .Build();
        _apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            _configuration,
            pathResolver: null!);
        _authenticationHelper = new AuthenticationHelper(
            _apiKeyService,
            NullLogger<AuthenticationHelper>.Instance);
    }

    [Fact]
    public async Task EmbeddedSetupWithoutApiKeyIsRejectedAsync()
    {
        var controller = new SetupController(
            NullLogger<SetupController>.Instance,
            pathResolver: null!,
            dbContextFactory: null!,
            authenticationHelper: _authenticationHelper,
            configuration: _configuration,
            // Never reached: with authentication disabled the key is the only proof and the token
            // check belongs to the session case, which this configuration refuses outright.
            antiforgery: null!)
        {
            ControllerContext = new ControllerContext { HttpContext = AuthenticatedContext() }
        };

        var result = await controller.SetCredentialsAsync(new SetupCredentialsRequest
        {
            Username = "lancache",
            Password = "unused-password"
        });

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegenerationWithoutApiKeyIsRejectedBeforeKeyChangesAsync()
    {
        var originalKey = _apiKeyService.GetApiKey();
        var controller = new ApiKeysController(
            // Never reached: the account row only decides who may rotate when authentication is on,
            // and this configuration has it off.
            dbContextFactory: null!,
            _apiKeyService,
            steamKit2Service: null!,
            steamAuthStorage: null!,
            stateService: null!,
            sessionService: null!,
            identityAuditService: null!,
            configuration: _configuration,
            authenticationHelper: _authenticationHelper,
            logger: NullLogger<ApiKeysController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = AuthenticatedContext() }
        };

        var result = await controller.RegenerateApiKeyAsync();

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Equal(originalKey, _apiKeyService.GetApiKey());
        Assert.Equal(originalKey, File.ReadAllText(_apiKeyPath));
    }

    private static DefaultHttpContext AuthenticatedContext()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "Session"))
        };
        Assert.True(context.User.Identity?.IsAuthenticated);
        return context;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
