using System.Net;
using System.Reflection;
using Docker.DotNet;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // Docker localizes its error text, so a 409 on the persistent create is retried by status code alone.
    [Fact]
    public async Task StartRoute_CreateConflictWithLocalizedText_RetriesAndStarts()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var deps = MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true));
        using var daemon = new TestSteamDaemon(deps, gateway);
        gateway.FailNextCreateContainer(new DockerApiException(HttpStatusCode.Conflict, "Der Name wird bereits verwendet"));
        var controller = new PersistentPrefillController(
            new SingleDaemonServiceProvider(daemon), deps.StateService, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var middleware = new GlobalExceptionMiddleware(
            async _ => await controller.StartAsync(
                new StartPersistentSessionRequest { Service = PrefillPlatform.Steam }, CancellationToken.None),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(2, gateway.CountOf("Create:"));
    }
}
