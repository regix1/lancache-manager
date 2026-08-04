using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Who is allowed to write the external PostgreSQL connection through
/// <c>POST /api/setup/external</c>.
///
/// The endpoint persists postgres-credentials.json, and the .NET app, every Rust binary and
/// entrypoint.sh all rebuild their database settings from that file on the next start. An
/// unauthenticated caller who can write it therefore repoints the whole installation at a database
/// of their choosing, which is why the endpoint accepts a session or the API key and nothing else.
///
/// The screen that calls it appears only while the database is unreachable, so no session can exist
/// there and the API key is the credential that has to keep working. The last test is the one that
/// proves the gate did not close that door: with a valid key the request reaches the endpoint's own
/// connection check instead of being turned away at the front.
///
/// The last three cover what a caller who is past the credential check is still not allowed to
/// write, since the username and password in that file are read back by entrypoint.sh and by every
/// Rust binary. Those rules live in one place now and both setup endpoints call them, so these also
/// stand as the check that the shared copy still says what the embedded endpoint used to say alone.
///
/// The controller is exercised directly rather than through the pipeline, so the returned
/// <see cref="ObjectResult"/> carries the same status and body in every hosting environment.
/// </summary>
public class ExternalDatabaseSetupGateTests : IDisposable
{
    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly SetupController _controller;

    public ExternalDatabaseSetupGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lm-external-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt")
            })
            .Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        _apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            pathResolver);

        var authenticationHelper = new AuthenticationHelper(
            _apiKeyService,
            NullLogger<AuthenticationHelper>.Instance);

        _controller = new SetupController(
            NullLogger<SetupController>.Instance,
            pathResolver,
            dbContextFactory: null!,
            authenticationHelper)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task NoSessionAndNoApiKey_IsRejectedBeforeAnythingIsWritten()
    {
        var result = await _controller.SetExternalCredentialsAsync(ConnectionRequest());

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Equal("API key required", ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    [Fact]
    public async Task NoSessionAndAWrongApiKey_IsRejectedBeforeAnythingIsWritten()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = "not-the-key";

        var result = await _controller.SetExternalCredentialsAsync(ConnectionRequest());

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.Equal("Invalid API key", ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    [Fact]
    public async Task NoSessionButTheRealApiKey_ReachesTheConnectionCheck()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();

        var result = await PostInExternalModeAsync(ConnectionRequest());

        // Port 1 on loopback refuses the connection, so this is the endpoint's own reply about the
        // server it was asked to reach, which it can only produce once the caller is past the
        // credential check.
        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.StartsWith("Could not connect to 127.0.0.1:1/lancache", ErrorOf(response));
    }

    /// <summary>
    /// entrypoint.sh reads this username back out of postgres-credentials.json on the next start and
    /// interpolates it into a shell command and an ALTER ROLE statement that runs with superuser
    /// rights, so a name carrying shell or SQL syntax must never reach the file.
    /// </summary>
    [Fact]
    public async Task AUsernameCarryingShellSyntax_IsRejectedBeforeAnythingIsWritten()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        var request = ConnectionRequest();
        request.Username = "lancache\"; $(id); \"";

        var result = await PostInExternalModeAsync(request);

        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            "Username may only contain letters, numbers, and underscores",
            ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    [Fact]
    public async Task APasswordShorterThanTheEmbeddedMinimum_IsRejected()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        var request = ConnectionRequest();
        request.Password = "short";

        var result = await PostInExternalModeAsync(request);

        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Password must be at least 8 characters", ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    [Fact]
    public async Task APasswordOnTheEmbeddedBlockedList_IsRejected()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        var request = ConnectionRequest();
        request.Password = "PassWord";

        var result = await PostInExternalModeAsync(request);

        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            "This password is too common. Please choose a more secure password.",
            ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    /// <summary>
    /// The endpoint refuses outright unless POSTGRES_MODE says external, so every test that needs to
    /// get past that line runs inside this, which puts the variable back afterwards.
    /// </summary>
    private async Task<IActionResult> PostInExternalModeAsync(SetExternalDbCredentialsRequest request)
    {
        var previousMode = Environment.GetEnvironmentVariable("POSTGRES_MODE");
        Environment.SetEnvironmentVariable("POSTGRES_MODE", "external");
        try
        {
            return await _controller.SetExternalCredentialsAsync(request);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POSTGRES_MODE", previousMode);
        }
    }

    private static SetExternalDbCredentialsRequest ConnectionRequest() => new()
    {
        Host = "127.0.0.1",
        Port = 1,
        Database = "lancache",
        Username = "lancache",
        Password = "a-password-nobody-guesses"
    };

    private static string ErrorOf(ObjectResult response) =>
        Assert.IsType<ErrorResponse>(response.Value).Error;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private class PathResolverProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IPathResolver.GetSecurityDirectory))
            {
                return Root;
            }

            if (targetMethod.Name == nameof(IPathResolver.GetPostgresCredentialsPath))
            {
                return Path.Combine(Root, "postgres-credentials.json");
            }

            throw new NotSupportedException(
                $"{targetMethod.Name} is not part of what this endpoint touches.");
        }
    }
}
