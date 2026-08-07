using System.Reflection;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// What a caller receives when the request was fine but something the server depends on is not
/// there. Two sites throw <see cref="ServiceUnavailableException"/>: Docker being unreachable in
/// <c>PrefillDaemonServiceBase.StartPersistentSessionForEditAsync</c>, and the GitHub depot download
/// failing in <c>DepotsController.ImportDepotMappingsAsync</c>. Both sentences name the dependency
/// and say what to do about it, which is exactly what a 500 would throw away.
///
/// <see cref="GlobalExceptionMiddleware"/> is what turns the throw into a response, so each sentence
/// is asserted as a matched pair: thrown as <see cref="ServiceUnavailableException"/> it yields 503
/// with the sentence intact, and thrown as a plain <see cref="InvalidOperationException"/> it yields
/// 500 with the sentence replaced by "An unexpected error occurred".
///
/// The environment is Production throughout, because that is where the middleware sanitizes
/// messages. In Development every exception message is echoed back, so a Development-only check
/// would pass identically with or without the typed exception and prove nothing.
/// </summary>
public class ServiceUnavailableResponseTests
{
    private const string DockerUnavailableMessage =
        "Docker is not running or not accessible. Please start Docker Desktop and try again.";

    private const string DepotImportFailedMessage =
        "Failed to download and import pre-created depot data from GitHub";

    private const string GenericServerMessage = "An unexpected error occurred";

    /// <summary>
    /// The real production throw, driven through the daemon with the docker seam reporting itself
    /// unavailable. This is the test that fails if the call site is reverted, since the assertion is
    /// on the type the method actually throws rather than on one constructed here.
    /// </summary>
    [Fact]
    public async Task DockerUnavailable_StartingEditSession_ThrowsServiceUnavailable()
    {
        var daemon = CreateDaemonWithUnavailableDocker();

        var thrown = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => daemon.StartPersistentSessionForEditAsync(
                PrefillPlatform.Steam,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Equal(DockerUnavailableMessage, thrown.Message);
    }

    [Fact]
    public async Task DockerUnavailable_ThrownAsServiceUnavailable_Returns503WithTheRealMessage()
    {
        var response = await RunMiddlewareAsync(new ServiceUnavailableException(DockerUnavailableMessage));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal(DockerUnavailableMessage, response.Error);
    }

    [Fact]
    public async Task DockerUnavailable_ThrownAsInvalidOperation_Returns500AndLosesTheMessage()
    {
        var response = await RunMiddlewareAsync(new InvalidOperationException(DockerUnavailableMessage));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(GenericServerMessage, response.Error);
        Assert.DoesNotContain("Docker", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DepotImportFailure_ThrownAsServiceUnavailable_Returns503WithTheRealMessage()
    {
        var response = await RunMiddlewareAsync(new ServiceUnavailableException(DepotImportFailedMessage));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal(DepotImportFailedMessage, response.Error);
    }

    [Fact]
    public async Task DepotImportFailure_ThrownAsInvalidOperation_Returns500AndLosesTheMessage()
    {
        var response = await RunMiddlewareAsync(new InvalidOperationException(DepotImportFailedMessage));

        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        Assert.Equal(GenericServerMessage, response.Error);
        Assert.DoesNotContain("GitHub", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A daemon whose only configured behaviour is a docker seam that reports itself unavailable.
    /// The availability check is the first statement in the method under test, so nothing past the
    /// seam needs to work for the throw to be reached.
    /// </summary>
    private static SteamDaemonService CreateDaemonWithUnavailableDocker()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"service_unavailable_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new InMemoryDbContextFactory(dbOptions);

        return new SteamDaemonService(
            NullLogger<SteamDaemonService>.Instance,
            (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new ConfigurationBuilder().Build(),
            (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>(),
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance),
            new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance),
            new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions()),
            new TestLancacheServerLocator(),
            new UnavailableContainerGatewayFactory());
    }

    /// <summary>
    /// Runs one request through <see cref="GlobalExceptionMiddleware"/> whose inner delegate throws
    /// <paramref name="thrown"/>, and reads back the status code and the body's <c>error</c> field.
    /// </summary>
    private static async Task<ErrorResponse> RunMiddlewareAsync(Exception thrown)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/depots/import";
        var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new GlobalExceptionMiddleware(
            _ => throw thrown,
            NullLogger<GlobalExceptionMiddleware>.Instance,
            new TestHostEnvironment("Production"));

        await middleware.InvokeAsync(context);

        var json = Encoding.UTF8.GetString(body.ToArray());
        using var document = JsonDocument.Parse(json);
        return new ErrorResponse(
            context.Response.StatusCode,
            document.RootElement.GetProperty("error").GetString() ?? string.Empty);
    }

    /// <summary>The two parts of the middleware's reply this file asserts on.</summary>
    private sealed record ErrorResponse(int StatusCode, string Error);

    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "LancacheManager";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
