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
        IOptionsMonitor<PrefillNetworkOptions> networkOptions,
        TimeProvider? cacheStatusClock = null,
        IUnifiedOperationTracker? operationTracker = null)
        : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory(), operationTracker: operationTracker, cacheStatusClock: cacheStatusClock)
    {
    }

    public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;

    public Func<List<string>, DateTimeOffset, CancellationToken, Task<CacheStatusResult>>? CacheStatus { get; set; }
    public DateTimeOffset? CacheStatusExpiresAtUtc { get; private set; }
    public CancellationToken CacheStatusToken { get; private set; }

    public override Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        CacheStatusExpiresAtUtc = expiresAtUtc;
        CacheStatusToken = cancellationToken;
        return CacheStatus is null
            ? base.GetCacheStatusAsync(sessionId, appIds, expiresAtUtc, cancellationToken)
            : CacheStatus(appIds, expiresAtUtc, cancellationToken);
    }

    public Task<CacheStatusResult> GetStringStatusAsync(
        string sessionId,
        List<string> appIds,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
        => GetStringAppCacheStatusAsync(sessionId, appIds, expiresAtUtc, cancellationToken);
}
