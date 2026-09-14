using System.Net;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class MetricsAuthenticationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("metrics-auth-").FullName;
    private readonly IConfiguration _configuration;
    private readonly StateService _state;
    private readonly ApiKeyService _keys;
    private int _calls;

    public MetricsAuthenticationTests()
    {
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:ApiKeyPath"] = Path.Combine(_root, "metrics-key.txt"),
            ["Security:RequireAuthForMetrics"] = "true"
        }).Build();
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = _root;
        _keys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, _configuration, paths);
        _state = StateTestMethods.CreateStateService(_root);
    }

    [Fact]
    public async Task MissingKeyReturnsTheRequiredReason()
    {
        var context = await InvokeAsync("/metrics", null);
        await AssertErrorAsync(context, 401, "API key required");
        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task WrongKeyReturnsTheInvalidReason()
    {
        var context = await InvokeAsync("/metrics", "wrong-key");
        await AssertErrorAsync(context, 403, "Invalid API key");
        Assert.Equal(0, _calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidKeyPassesAfterInvalidAttempts(bool bearer)
    {
        var address = new IPAddress(Guid.NewGuid().ToByteArray());
        for (var attempt = 0; attempt < 11; attempt++)
        {
            var rejected = await InvokeAsync("/metrics", "wrong-key", bearer, address);
            await AssertErrorAsync(rejected, attempt < 10 ? 403 : 429,
                attempt < 10 ? "Invalid API key" : "Too many invalid API keys");
        }
        Assert.Equal(0, _calls);
        var context = await InvokeAsync("/metrics", _keys.GetApiKey(), bearer, address);
        Assert.Equal(1, _calls);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task UiChoiceOverridesTheConfiguredRequirement(bool? choice, bool configured, bool required)
    {
        _state.GetState().RequireAuthForMetrics = choice;
        _configuration["Security:RequireAuthForMetrics"] = configured.ToString();
        var context = await InvokeAsync("/metrics", null);
        if (required)
        {
            await AssertErrorAsync(context, 401, "API key required");
            Assert.Equal(0, _calls);
        }
        else
        {
            Assert.Equal(1, _calls);
            Assert.Equal(0, context.Response.Body.Length);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SharedModeKeepsTheMetricsRequirementIndependent(bool required, bool legacy)
    {
        _configuration["Security:EnableAuthentication"] = "false";
        _state.GetState().Access.Mode = legacy ? null : AccountMode.Unauthenticated;
        _state.GetState().RequireAuthForMetrics = required;
        var context = await InvokeAsync("/metrics", null);
        Assert.Equal(required ? 0 : 1, _calls);
        if (required) await AssertErrorAsync(context, 401, "API key required");
        else Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task OtherPathsPassThrough()
    {
        var context = await InvokeAsync("/api/status", null);
        Assert.Equal(1, _calls);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private async Task<DefaultHttpContext> InvokeAsync(string path, string? key, bool bearer = false, IPAddress? address = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = address ?? new IPAddress(Guid.NewGuid().ToByteArray());
        context.Response.Body = new MemoryStream();
        if (key != null)
        {
            if (bearer) context.Request.Headers.Authorization = "Bearer " + key;
            else context.Request.Headers["X-Api-Key"] = key;
        }
        var auth = new AuthenticationHelper(_keys, NullLogger<AuthenticationHelper>.Instance);
        var result = auth.ValidateApiKey(context);
        if (!result.IsAuthenticated) Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        var middleware = new MetricsAuthenticationMiddleware(_ =>
        {
            _calls++;
            return Task.CompletedTask;
        }, _configuration, NullLogger<MetricsAuthenticationMiddleware>.Instance);
        await middleware.InvokeAsync(context, auth, _state);
        if (context.Response.StatusCode >= 400)
            await AssertErrorAsync(context, result.StatusCode, result.ErrorMessage!);
        return context;
    }

    private static async Task AssertErrorAsync(HttpContext context, int status, string reason)
    {
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(reason, document.RootElement.GetProperty("error").GetString());
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }
}
