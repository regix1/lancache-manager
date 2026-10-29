using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // A validity save whose date write fails tells the person the days were saved and the dates were not.
    [Fact]
    public async Task ValiditySaveWhoseDateWriteFailsSaysTheDaysWereSavedAsync()
    {
        var failingWrite = new FailingRowSave(entry => entry.Property(row => row.ExpiresAtUtc).IsModified) { Armed = true };
        var (_, database) = NewDatabase(failingWrite);
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        await SeedActivePersistentRowAsync(sessions, DateTime.UtcNow.AddDays(20));
        var config = Config(PersistenceMode.FullPersistence, steamEnabled: true);
        var state = OrchestrationStateService.New(config, 30);
        var deps = MakeDeps(database, sessions, config) with { StateService = state };
        using var steam = new TestSteamDaemon(deps, new RecordingContainerGateway());
        await using var daemons = new ServiceCollection().AddSingleton<SteamDaemonService>(steam).BuildServiceProvider();
        var controller = new PersistentPrefillController(daemons, state, deps.CacheService,
            NullLogger<PersistentPrefillController>.Instance);
        var middleware = new GlobalExceptionMiddleware(
            async _ => await controller.SetValidityAsync(new PersistentLoginValidityDto { Days = 7 }),
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        using var answer = JsonDocument.Parse(body.ToArray());
        Assert.Equal("errors.prefill.validityDatesNotSaved", answer.RootElement.GetProperty("stageKey").GetString());
        Assert.Equal(7, state.GetAdminPersistentLoginValidityDays());
    }
}
