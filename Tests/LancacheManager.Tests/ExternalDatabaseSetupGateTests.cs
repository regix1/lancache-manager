using System.Reflection;
using System.Security.Claims;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
/// there and the API key is the credential that has to keep working. NoSessionButTheRealApiKey proves
/// the gate did not close that door: with a valid key the request reaches the endpoint's own
/// connection check instead of being turned away at the front.
///
/// The username and password tests cover what a caller who is past the credential check is still not
/// allowed to write, since both are read back out of that file by entrypoint.sh and by every Rust
/// binary. Connecting to an existing external server still uses CheckPassword (eight characters).
/// Setting a new embedded role password is stricter and is covered separately.
///
/// The two AnAuthenticatedCaller tests cover which principals count as proof. A session is accepted
/// only while authentication is enabled, because turning that flag off makes every request arrive
/// looking authenticated and would otherwise open this endpoint to anyone who can reach the port.
/// A session that is accepted is still only cookies, which a browser attaches to whatever caused the
/// request, so that caller is then asked for the antiforgery token; the key caller is not, which is
/// what keeps the repair path open. AntiforgeryTokenTests covers the same pair over the real pipeline,
/// where a token can actually be issued and sent back.
///
/// The controller is exercised directly rather than through the pipeline, so the returned
/// <see cref="ObjectResult"/> carries the same status and body in every hosting environment.
/// </summary>
public class ExternalDatabaseSetupGateTests : IDisposable
{
    /// <summary>
    /// The framework's own antiforgery service, so the session case is checked the way the app checks
    /// it. A request built here carries no token, which is the state the check exists to refuse.
    /// </summary>
    private static readonly IAntiforgery _antiforgery = new ServiceCollection()
        .AddLogging()
        .AddDataProtection().Services
        .AddAntiforgery()
        .BuildServiceProvider()
        .GetRequiredService<IAntiforgery>();

    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly IPathResolver _pathResolver;
    private readonly AuthenticationHelper _authenticationHelper;
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

        _pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)_pathResolver).Root = _root;

        _apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            _pathResolver);

        _authenticationHelper = new AuthenticationHelper(
            _apiKeyService,
            NullLogger<AuthenticationHelper>.Instance);

        _controller = CreateController(authenticationEnabled: true);
    }

    private SetupController CreateController(bool authenticationEnabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = authenticationEnabled ? "true" : "false"
            })
            .Build();

        return new SetupController(
            NullLogger<SetupController>.Instance,
            _pathResolver,
            dbContextFactory: null!,
            _authenticationHelper,
            configuration,
            _antiforgery)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task NoSessionAndNoApiKey_IsRejectedBeforeAnythingIsWritten()
    {
        var result = await _controller.SetExternalCredentialsAsync(ConnectionRequest());

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Equal("API key required", ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    [Fact]
    public async Task NoSessionAndAWrongApiKey_IsRejectedBeforeAnythingIsWritten()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = "not-the-key";

        var result = await _controller.SetExternalCredentialsAsync(ConnectionRequest());

        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, response.StatusCode);
        Assert.Equal("Invalid API key", ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    [Fact]
    public async Task NoSessionButTheRealApiKey_ReachesTheConnectionCheck()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();

        var result = await PostInExternalModeAsync(_controller, ConnectionRequest());

        // Port 1 on loopback refuses the connection, so this is the endpoint's own reply about the
        // server it was asked to reach, which it can only produce once the caller is past the
        // credential check.
        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.StartsWith("Could not connect to 127.0.0.1:1/lancache", ErrorOf(response));
    }

    /// <summary>
    /// The connection check has to prove the one right the schema needs, not only that the
    /// credentials open a connection. The schema installs the citext extension, and PostgreSQL only
    /// lets a role install it when that role holds CREATE on the database itself, so a role granted
    /// CREATE on schema public alone connects here happily and then fails database initialization on
    /// the next start, before the web server opens a port.
    ///
    /// There is no PostgreSQL server in this fixture to ask, and port 1 above refuses the connection
    /// before any statement is sent, so this reads the statement the endpoint sends instead. It fails
    /// if the check goes back to proving only that the connection opens.
    /// </summary>
    [Fact]
    public void TheConnectionCheckAsksWhetherTheRoleCanCreate()
    {
        // Walked from the test binary rather than the current directory, which another collection's
        // host moves to a temporary root that carries Api and Web directories of its own.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null
            && !(Directory.Exists(Path.Combine(directory.FullName, "Api"))
                && Directory.Exists(Path.Combine(directory.FullName, "Web"))))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var source = File.ReadAllText(Path.Combine(
            directory!.FullName, "Api", "LancacheManager", "Controllers", "SetupController.cs"));

        Assert.Contains(
            "SELECT has_database_privilege(current_user, current_database(), 'CREATE')",
            source,
            StringComparison.Ordinal);
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

        var result = await PostInExternalModeAsync(_controller, request);

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

        var result = await PostInExternalModeAsync(_controller, request);

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

        var result = await PostInExternalModeAsync(_controller, request);

        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(
            "This password is too common. Please choose a more secure password.",
            ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    [Fact]
    public async Task ANewEmbeddedPasswordShorterThanTwelve_IsRejected()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        var previous = Environment.GetEnvironmentVariable("POSTGRES_MODE");
        Environment.SetEnvironmentVariable("POSTGRES_MODE", "embedded");
        try
        {
            var result = await _controller.SetCredentialsAsync(new SetupCredentialsRequest
            {
                Username = "lancache",
                Password = "Longer1!"
            });

            var response = Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal("Password must be at least 12 characters", ErrorOf(response));
        }
        finally
        {
            Environment.SetEnvironmentVariable("POSTGRES_MODE", previous);
        }
    }

    [Fact]
    public async Task ANewEmbeddedPasswordWithoutThreeClasses_IsRejected()
    {
        _controller.HttpContext.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        var previous = Environment.GetEnvironmentVariable("POSTGRES_MODE");
        Environment.SetEnvironmentVariable("POSTGRES_MODE", "embedded");
        try
        {
            var result = await _controller.SetCredentialsAsync(new SetupCredentialsRequest
            {
                Username = "lancache",
                Password = "alllowercase1"
            });

            var response = Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(
                "Password must use at least three of: lowercase letters, uppercase letters, digits, and other characters",
                ErrorOf(response));
        }
        finally
        {
            Environment.SetEnvironmentVariable("POSTGRES_MODE", previous);
        }
    }

    /// <summary>
    /// Turning Security:EnableAuthentication off opens every authorization policy and gives each
    /// request an admin session, so a caller who presented nothing at all arrives here looking
    /// authenticated. Accepting that would hand the connection file to anyone who can reach the port,
    /// which is the whole reason the endpoint is gated, so the key is the only proof in that state.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedCallerWithNoApiKeyWhileAuthenticationIsDisabled_IsRejected()
    {
        var controller = CreateController(authenticationEnabled: false);
        controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "admin") }, "Session"));
        Assert.True(controller.HttpContext.User.Identity?.IsAuthenticated);

        var result = await PostInExternalModeAsync(controller, ConnectionRequest());

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, response.StatusCode);
        Assert.Equal("API key required", ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    /// <summary>
    /// An account holder with authentication left on is the session case the endpoint is meant to
    /// accept, so the rule above turns away the disabled-auth principal and nothing else. It gets as
    /// far as the antiforgery check and no further: a session is a cookie the browser attaches to a
    /// request another origin caused, so this caller has to prove it can read this origin's cookies
    /// before anything is written. The key caller above never sees this check, which is what keeps
    /// the repair path open.
    /// </summary>
    [Fact]
    public async Task AnAuthenticatedCallerWithNoApiKeyWhileAuthenticationIsEnabled_IsAskedForTheAntiforgeryToken()
    {
        // The session the authentication handler publishes, which is what the endpoint reads rather
        // than the principal: a guest's principal is authenticated too and must not be admitted.
        _controller.HttpContext.Items["Session"] = new UserSession { SessionType = SessionType.Admin };

        var result = await PostInExternalModeAsync(_controller, ConnectionRequest());

        var response = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(AntiforgeryToken.MissingTokenMessage, ErrorOf(response));
        Assert.False(File.Exists(Path.Combine(_root, "postgres-credentials.json")));
    }

    /// <summary>
    /// The endpoint refuses outright unless POSTGRES_MODE says external, so every test that needs to
    /// get past that line runs inside this, which puts the variable back afterwards.
    /// </summary>
    private static async Task<IActionResult> PostInExternalModeAsync(
        SetupController controller,
        SetExternalDbCredentialsRequest request)
    {
        var previousMode = Environment.GetEnvironmentVariable("POSTGRES_MODE");
        Environment.SetEnvironmentVariable("POSTGRES_MODE", "external");
        try
        {
            var response = await controller.SetExternalCredentialsAsync(request);
            // Every branch of SetExternalCredentialsAsync returns via Ok()/BadRequest()/StatusCode(),
            // never a bare value, so .Result is always populated.
            return response.Result!;
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
