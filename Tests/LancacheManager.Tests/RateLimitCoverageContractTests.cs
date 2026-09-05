using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class RateLimitCoverageContractTests
{
    /// <summary>
    /// Every route that checks a secret, or that any caller can reach without holding one, together
    /// with the limiter policy it runs on. This is the list the coverage rule is written against:
    /// a route added to the application that belongs here and is missing fails the test below.
    /// </summary>
    private static readonly Dictionary<string, string> ThrottledActions = new(StringComparer.Ordinal)
    {
        ["AuthController.Login"] = "auth",
        ["AuthController.ChangePassword"] = "auth",
        ["AuthController.StartGuest"] = "auth",
        ["SetupController.SetCredentials"] = "auth",
        ["SetupController.SetExternalCredentials"] = "auth",
        ["ApiKeysController.RegenerateApiKey"] = "auth",
        ["SteamAuthController.Login"] = "steam-auth",
        ["AccountSetupController.CreateFirstAdmin"] = "auth",
        ["AccountSetupController.SetMainAdminPassword"] = "auth",
        ["AccountSetupController.OpenMainAdminRecovery"] = "auth",
        ["AccountSetupController.RecoverMainAdminPassword"] = "auth",
        ["AccessController.SetUp"] = "auth",
        ["AccessController.StartOidc"] = "auth",
        ["AccessController.StartLogin"] = "auth",
        ["AccessController.RemoveLogin"] = "auth",
        ["AccountsController.CreateAccount"] = "accounts",
        ["AccountsController.EditAccount"] = "accounts"
    };

    /// <summary>
    /// Anonymous writes that check no secret and hand out no access, so repeating one gains a caller
    /// nothing. Logout revokes the cookie the caller already sent.
    /// </summary>
    private static readonly HashSet<string> UnthrottledAnonymousWrites = new(StringComparer.Ordinal)
    {
        "AuthController.Logout"
    };

    /// <summary>
    /// Five wrong keys from one address must not stop a different address signing in. A window with
    /// no partition key is one bucket for the whole installation, so any caller can hold the login
    /// endpoint shut for everybody else.
    /// </summary>
    [Fact]
    public async Task OneAddressFailingLoginDoesNotThrottleAnother()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();
        var (username, password) = await host.NewAccountAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await SendAsync(
                host.Application.Server, "10.0.0.1", "POST", "/api/auth/login", body: """{"apiKey":"not-the-key"}""");

            Assert.Equal(StatusCodes.Status401Unauthorized, refused);
        }

        var accepted = await SendAsync(
            host.Application.Server,
            "10.0.0.2",
            "POST",
            "/api/auth/login",
            body: $$"""{"apiKey":"{{apiKey}}","username":"{{username}}","password":"{{password}}"}""");

        Assert.Equal(StatusCodes.Status200OK, accepted);
    }

    /// <summary>
    /// Each guest session writes a session row and up to five prefill grants, so the endpoint is a
    /// row-insertion primitive for anyone who can reach the port.
    /// </summary>
    [Fact]
    public async Task GuestSessionsAreThrottledPerCallerAddress()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var allowed = await SendAsync(host.Application.Server, "10.0.1.1", "POST", "/api/auth/guest");

            Assert.NotEqual(StatusCodes.Status429TooManyRequests, allowed);
        }

        var throttled = await SendAsync(host.Application.Server, "10.0.1.1", "POST", "/api/auth/guest");
        var other = await SendAsync(host.Application.Server, "10.0.1.2", "POST", "/api/auth/guest");

        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, other);
    }

    /// <summary>
    /// The wizard is the only screen an unfinished installation serves and a guest cannot complete
    /// it, so a guest session handed out here strands the caller in a wizard that refuses every save
    /// and cannot be dismissed.
    /// </summary>
    [Fact]
    public async Task GuestSessionsAreRefusedWhileSetupIsIncomplete()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var refused = await SendAsync(host.Application.Server, "10.0.2.1", "POST", "/api/auth/guest");

        Assert.Equal(StatusCodes.Status403Forbidden, refused);
    }

    /// <summary>
    /// The key is checked on the documentation routes, which no limiter attribute covers because they
    /// are mapped outside a controller. Guessing 32 bytes of entropy is infeasible either way; what
    /// this buys is that a flood is bounded and the failures are counted.
    /// </summary>
    [Fact]
    public async Task InvalidApiKeysAreThrottledPerCallerAddress()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var refused = await SendAsync(
                host.Application.Server, "10.0.3.1", "GET", "/openapi/v1.json", apiKey: "not-the-key");

            Assert.Equal(StatusCodes.Status401Unauthorized, refused);
        }

        var throttled = await SendAsync(
            host.Application.Server, "10.0.3.1", "GET", "/openapi/v1.json", apiKey: "not-the-key");
        var other = await SendAsync(
            host.Application.Server, "10.0.3.2", "GET", "/openapi/v1.json", apiKey: "not-the-key");

        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled);
        Assert.Equal(StatusCodes.Status401Unauthorized, other);
    }

    /// <summary>
    /// Creating the first account is anonymous and takes the API key, and the claim window it sits
    /// behind is measured in minutes. Without a limiter one address can spend that whole window
    /// guessing, so the window is not on its own the defence.
    /// </summary>
    [Fact]
    public async Task FirstAdminCreationIsThrottledPerCallerAddress()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        const string body = """{"apiKey":"not-the-key","username":"operator","password":"Correct-Horse-9"}""";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await SendAsync(
                host.Application.Server, "10.0.4.1", "POST", "/api/account-setup/first-admin", body: body);

            Assert.Equal(StatusCodes.Status401Unauthorized, refused);
        }

        var throttled = await SendAsync(
            host.Application.Server, "10.0.4.1", "POST", "/api/account-setup/first-admin", body: body);
        var other = await SendAsync(
            host.Application.Server, "10.0.4.2", "POST", "/api/account-setup/first-admin", body: body);

        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled);
        Assert.Equal(StatusCodes.Status401Unauthorized, other);
    }

    /// <summary>
    /// The account writes have a budget of their own. Every window is partitioned on the caller
    /// address and nothing else, so while these two sat on the sign-in policy an administrator adding
    /// half a dozen accounts inside a minute spent the sign-in permits for that address, and the next
    /// sign-in from that machine was refused for the rest of the minute.
    /// </summary>
    [Fact]
    public async Task AccountWritesDoNotSpendTheSignInBudget()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        const string account = """{"username":"minted","password":"Correct-Horse-9","role":"user"}""";

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var written = await SendAsync(
                host.Application.Server, "10.0.7.1", "POST", "/api/accounts", body: account);

            Assert.NotEqual(StatusCodes.Status429TooManyRequests, written);
        }

        // The eleventh is refused, so the writes moved onto a second budget rather than off a limiter.
        var throttled = await SendAsync(
            host.Application.Server, "10.0.7.1", "POST", "/api/accounts", body: account);

        var signIn = await SendAsync(
            host.Application.Server,
            "10.0.7.1",
            "POST",
            "/api/auth/login",
            body: """{"apiKey":"not-the-key"}""");

        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled);
        Assert.Equal(StatusCodes.Status401Unauthorized, signIn);
    }

    /// <summary>
    /// A refused request answers with the same JSON shape as every other refusal in this API. The
    /// limiter writes no body on its own, so a caller that reads the response as JSON reads a parse
    /// failure where the reason should be.
    /// </summary>
    [Fact]
    public async Task ThrottledRequestsAnswerWithAJsonBody()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await SendAsync(host.Application.Server, "10.0.5.1", "POST", "/api/auth/guest");
        }

        var throttled = await SendAndReadAsync(host.Application.Server, "10.0.5.1", "/api/auth/guest");

        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        Assert.StartsWith("application/json", throttled.ContentType ?? string.Empty, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(throttled.Body);

        Assert.Equal(
            "errors.rateLimit.tooManyRequests",
            document.RootElement.GetProperty("stageKey").GetString());

        Assert.NotEmpty(document.RootElement.GetProperty("error").GetString() ?? string.Empty);

        // The fixed window reports the time left before it reopens, so the caller is told how long
        // to wait instead of retrying into the same refusal.
        Assert.NotEmpty(throttled.RetryAfter);
        Assert.InRange(int.Parse(throttled.RetryAfter, CultureInfo.InvariantCulture), 1, 60);
    }

    /// <summary>
    /// The body is written once on the limiter options rather than on a policy, so the steam-auth
    /// policy answers exactly as the auth policy does and a policy added later inherits it without
    /// anyone remembering to wire it up.
    /// </summary>
    [Fact]
    public async Task EveryLimiterPolicyAnswersWithTheSameBody()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await SendAsync(host.Application.Server, "10.0.6.1", "POST", "/api/steam-auth/login");
        }

        var throttled = await SendAndReadAsync(host.Application.Server, "10.0.6.1", "/api/steam-auth/login");

        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);

        using var document = JsonDocument.Parse(throttled.Body);

        Assert.Equal(
            "errors.rateLimit.tooManyRequests",
            document.RootElement.GetProperty("stageKey").GetString());
    }

    /// <summary>
    /// The writer sits on the limiter options rather than on a policy, and a limiter that reports no
    /// retry time leaves the header off instead of sending a guess the caller would wait on. Both
    /// fixed windows registered today report one, so this is the only way to reach that path.
    /// </summary>
    [Fact]
    public async Task ARefusalWithNoRetryTimeSendsNoRetryAfterHeader()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var onRejected = host.Application.Services
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value.OnRejected;

        Assert.NotNull(onRejected);

        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = host.Application.Services };
        context.Response.Body = body;

        await onRejected(
            new OnRejectedContext { HttpContext = context, Lease = new LeaseWithoutRetryTime() },
            CancellationToken.None);

        Assert.False(context.Response.Headers.ContainsKey("Retry-After"));

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));

        Assert.Equal(
            "errors.rateLimit.tooManyRequests",
            document.RootElement.GetProperty("stageKey").GetString());
    }

    /// <summary>
    /// A refused lease from a limiter that publishes no retry time. A fixed window always publishes
    /// one; a concurrency limiter, which a later policy could use, publishes none.
    /// </summary>
    private sealed class LeaseWithoutRetryTime : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => Array.Empty<string>();

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    /// <summary>
    /// The coverage rule, as one test rather than one check per route: a route that takes a password
    /// or that any caller can reach without a session has to sit behind a per-address limiter, and
    /// the routes already there have to stay there.
    /// </summary>
    [Fact]
    public async Task EveryRouteThatTakesASecretIsThrottled()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var routes = Routes(host.Application.Services);

        Assert.NotEmpty(routes);

        var unlimited = ThrottledActions
            .Where(entry => !routes.Any(route =>
                string.Equals(route.Action, entry.Key, StringComparison.Ordinal)
                && string.Equals(route.LimiterPolicy, entry.Value, StringComparison.Ordinal)))
            .Select(entry => $"{entry.Key} (expected the '{entry.Value}' limiter)")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unlimited.Length == 0,
            $"These routes take a secret and no longer carry their limiter: {string.Join(", ", unlimited)}. "
            + "Put [EnableRateLimiting] back on the action, or remove it from ThrottledActions if the route no longer takes one.");

        var unregistered = routes
            .Where(TakesASecret)
            .Where(route => route.Action == null
                || (!ThrottledActions.ContainsKey(route.Action) && !UnthrottledAnonymousWrites.Contains(route.Action)))
            .Select(route => $"{route.Action ?? route.Route} ({route.Method} /{route.Route})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unregistered.Length == 0,
            $"These routes take a secret and are not on a per-address limiter: {string.Join(", ", unregistered)}. "
            + "Add [EnableRateLimiting(\"auth\")] and the action to ThrottledActions.");
    }

    /// <summary>
    /// A route takes a secret when it is handed a password, or when any caller can reach it without a
    /// session and change something: those are the two shapes every credential-checking route in this
    /// application has.
    /// </summary>
    private static bool TakesASecret(RouteRateLimit route)
    {
        var write = !HttpMethods.IsGet(route.Method)
            && !HttpMethods.IsHead(route.Method)
            && !HttpMethods.IsOptions(route.Method);

        return route.TakesAPassword || (route.Anonymous && write);
    }

    private static RouteRateLimit[] Routes(IServiceProvider services)
    {
        return services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Select(description => new RouteRateLimit(
                description.HttpMethod ?? "ANY",
                description.RelativePath ?? string.Empty,
                // Null for the routes mapped outside a controller: /health, /api/version, /metrics and
                // the documentation routes. None of them takes a secret.
                description.ActionDescriptor is ControllerActionDescriptor controllerAction
                    ? $"{controllerAction.ControllerName}Controller.{controllerAction.ActionName}"
                    : null,
                description.ActionDescriptor.EndpointMetadata.Any(item => item is IAllowAnonymous),
                description.ActionDescriptor.EndpointMetadata
                    .OfType<EnableRateLimitingAttribute>()
                    .Select(attribute => attribute.PolicyName)
                    .FirstOrDefault(),
                TakesAPassword(description.ActionDescriptor)))
            .ToArray();
    }

    private static bool TakesAPassword(ActionDescriptor action)
    {
        return action.Parameters.Any(parameter =>
            parameter.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || parameter.ParameterType.GetProperties().Any(property =>
                property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Sends one request through the whole pipeline with the caller's address set, which the test
    /// host leaves null and the limiter partitions on.
    /// </summary>
    /// <summary>
    /// The antiforgery cookies and token the status endpoint hands out, which is where the page gets
    /// them. The status call is not on a limiter, so taking them costs the caller none of its permits.
    /// </summary>
    private static async Task<(string Cookies, string Token)> AntiforgeryAsync(
        TestServer server,
        string callerAddress)
    {
        var context = await server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(callerAddress);
            request.Request.Method = "GET";
            request.Request.Scheme = "http";
            request.Request.Host = new HostString("localhost");
            request.Request.Path = "/api/auth/status";
        });

        var issued = context.Response.Headers.SetCookie
            .Select(header => (header ?? string.Empty).Split(';')[0])
            .ToArray();

        var prefix = $"{AntiforgeryToken.CookieName}=";
        var token = issued.First(cookie => cookie.StartsWith(prefix, StringComparison.Ordinal));

        return (string.Join("; ", issued), Uri.UnescapeDataString(token[prefix.Length..]));
    }

    private static async Task<int> SendAsync(
        TestServer server,
        string callerAddress,
        string method,
        string path,
        string? body = null,
        string? apiKey = null)
    {
        // A request that changes something is refused without the antiforgery token, and that refusal
        // comes before the endpoint's own answer, so without this every assertion below would be
        // measuring the token check rather than the limiter. A real client holds the pair from the
        // status call its page makes on load; this takes it the same way.
        var antiforgery = await AntiforgeryAsync(server, callerAddress);

        var context = await server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(callerAddress);
            request.Request.Method = method;
            request.Request.Scheme = "http";
            request.Request.Host = new HostString("localhost");
            request.Request.Path = path;
            request.Request.Headers.Cookie = antiforgery.Cookies;
            request.Request.Headers[AntiforgeryToken.HeaderName] = antiforgery.Token;

            if (apiKey != null)
            {
                request.Request.Headers["X-Api-Key"] = apiKey;
            }

            if (body != null)
            {
                var payload = Encoding.UTF8.GetBytes(body);
                request.Request.ContentType = "application/json";
                request.Request.ContentLength = payload.Length;
                request.Request.Body = new MemoryStream(payload);
            }
        });

        return context.Response.StatusCode;
    }

    /// <summary>
    /// Sends one request with the response body captured, which is how the limiter's own reply gets
    /// read rather than only counted. <see cref="SendAsync"/> stays status-only for the tests above.
    /// </summary>
    private static async Task<RejectedResponse> SendAndReadAsync(TestServer server, string callerAddress, string path)
    {
        var body = new MemoryStream();

        var context = await server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(callerAddress);
            request.Request.Method = "POST";
            request.Request.Scheme = "http";
            request.Request.Host = new HostString("localhost");
            request.Request.Path = path;
            request.Response.Body = body;
        });

        return new RejectedResponse(
            context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers.RetryAfter.ToString(),
            Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>The parts of a refused request's reply these tests assert on.</summary>
    private sealed record RejectedResponse(int StatusCode, string? ContentType, string RetryAfter, string Body);

    private sealed record RouteRateLimit(
        string Method,
        string Route,
        string? Action,
        bool Anonymous,
        string? LimiterPolicy,
        bool TakesAPassword);
}
