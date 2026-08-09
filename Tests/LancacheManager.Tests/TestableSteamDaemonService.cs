using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

internal sealed class TestableSteamDaemonService : SteamDaemonService
{
    public TestableSteamDaemonService(
        Microsoft.Extensions.Logging.ILogger<SteamDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        IStateService stateService,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions)
        : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
    {
    }

    public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;
}
