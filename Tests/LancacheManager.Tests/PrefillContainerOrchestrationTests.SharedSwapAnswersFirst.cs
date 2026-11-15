using System.Text.Encodings.Web;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // How long the request may take to answer. A request that does not wait on Docker answers in
    // milliseconds; one that waits on the held kill below never answers on its own.
    private static readonly TimeSpan RequestAnswerBound = TimeSpan.FromSeconds(10);

    // With authentication off, a browser that still holds a User account cookie from before the switch is
    // handed the shared session on its next request, and the account session ends with its prefill. Docker
    // can take up to 100 s per call to kill that prefill's container; the page load that carried the cookie
    // must not wait for it. The kill is held until the request has answered.
    [Fact]
    public async Task SwapToTheSharedSession_AnswersBeforeTheOldSessionsPrefillIsStopped()
    {
        var scene = await StartAccountPrefillAsync(SessionType.User);
        await using var app = scene.App;
        var configuration = app.GetRequiredService<IConfiguration>();
        configuration["Security:EnableAuthentication"] = "false";
        scene.SteamGateway.HoldKillContainer = true;

        var requestServices = new ServiceCollection();
        requestServices.AddOptions();
        requestServices.AddSingleton(scene.Sessions);
        requestServices.AddSingleton(new AccessService(
            app.GetRequiredService<StateService>(), configuration, app.GetRequiredService<IDbContextFactory<AppDbContext>>()));
        requestServices.AddSingleton(app.GetRequiredService<SteamDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<EpicPrefillDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<XboxPrefillDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<BattleNetDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<RiotDaemonService>());
        await using var request = requestServices.BuildServiceProvider();

        var pageLoad = new DefaultHttpContext { RequestServices = request };
        pageLoad.Request.Headers.Cookie = $"{SessionCookieName}={scene.Token}";
        var handler = new SessionAuthenticationHandler(
            request.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);
        await handler.InitializeAsync(
            new AuthenticationScheme(SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
            pageLoad);

        var authenticate = handler.AuthenticateAsync();
        try
        {
            var answered = await Task.WhenAny(authenticate, Task.Delay(RequestAnswerBound)) == authenticate;
            Assert.True(
                answered,
                "with authentication off, the first request carrying a User account cookie does not answer until that session's prefill container is killed, so the page waits on Docker");
            Assert.True((await authenticate).Succeeded);
            Assert.True(
                scene.Sessions.IsSharedAdminSession(Assert.IsType<UserSession>(pageLoad.Items["Session"]).Id),
                "the request that carried the User account cookie was not handed the shared session");

            // The old session's prefill still stops, without the request having waited for it.
            await scene.SteamGateway.KillContainerEntered.Task.WaitAsync(RequestAnswerBound);
        }
        finally
        {
            scene.SteamGateway.ReleaseKillContainer.TrySetResult();
        }
    }
}
