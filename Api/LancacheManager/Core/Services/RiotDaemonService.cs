using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services;

/// <summary>
/// Manages Riot Prefill daemon Docker containers.
/// Inherits all shared container management from PrefillDaemonServiceBase.
///
/// Riot prefill is fully anonymous - it prefills public Riot CDN content (League of Legends,
/// Valorant) with no account login or credentials. A connected daemon reports itself as
/// logged-in/initialized immediately (the daemon's status update drives the session straight to
/// <see cref="DaemonAuthState.Authenticated"/> via the base status handler), so there is no
/// login step, credential challenge, or owned-game-library collection. The
/// EventAuthStateChanged/EventCredentialChallenge events still need constant values (the base
/// requires them) but are not expected to fire in normal operation.
/// </summary>
public class RiotDaemonService : PrefillDaemonServiceBase
{
    private const string RiotDockerImage = "ghcr.io/regix1/riot-prefill-daemon:latest";

    public RiotDaemonService(
        ILogger<RiotDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        IStateService stateService,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions,
        ILancacheServerLocator locator)
        : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, locator)
    {
    }

    // Route per-connection and broadcast notifications to the Riot prefill hub
    protected override string HubRoutingTarget => "riot";

    // Service identity
    protected override string ServiceName => "riot";
    protected override PrefillPlatform Platform => PrefillPlatform.Riot;
    protected override string ContainerPrefix => "riot-prefill-";
    protected override string DefaultDockerImage => RiotDockerImage;

    // Riot is fully anonymous - a session is authenticated/ready the moment it is created,
    // with no login step or credential challenge. This drives the returned session DTO straight to
    // Authenticated so the client unlocks prefill commands immediately.
    protected override DaemonAuthState InitialAuthState => DaemonAuthState.Authenticated;

    protected override string GetImageName()
        => _configuration["Prefill:RiotDockerImage"] ?? RiotDockerImage;
    protected override int GetGuestPermissionDurationHours()
        => _stateService.GetRiotGuestPrefillDurationHours();

    // Diagnostics
    protected override string DiagnosticsConnectivityUrl => "https://lol.dyn.riotcdn.net/";
    protected override string[] DiagnosticsDnsDomains => new[]
    {
        "lol.dyn.riotcdn.net",
        "valorant.dyn.riotcdn.net"
    };

    // SignalR event names (Riot-specific)
    protected override string EventSessionCreated => SignalREvents.RiotDaemonSessionCreated;
    protected override string EventSessionUpdated => SignalREvents.RiotDaemonSessionUpdated;
    protected override string EventSessionTerminated => SignalREvents.RiotDaemonSessionTerminated;
    protected override string EventAuthStateChanged => SignalREvents.RiotAuthStateChanged;
    protected override string EventCredentialChallenge => SignalREvents.RiotCredentialChallenge;
    protected override string EventStatusChanged => SignalREvents.RiotStatusChanged;
    protected override string EventPrefillStateChanged => SignalREvents.RiotPrefillStateChanged;
    protected override string EventPrefillProgress => SignalREvents.RiotPrefillProgress;
    protected override string EventPrefillHistoryUpdated => SignalREvents.RiotPrefillHistoryUpdated;
    protected override string EventSessionEnded => SignalREvents.RiotSessionEnded;

    // Riot daemon uses a distinct HKDF info string for the socket handshake encryption context
    protected override string CredentialEncryptionHkdfInfo => "RiotPrefill-Credential-Encryption";

    /// <summary>
    /// Override cache status check for Riot since it uses string app IDs (product slugs such as
    /// league_of_legends / valorant), not uint depot/manifest pairs. Sends app IDs directly to the
    /// Riot daemon which checks prefilled versions against the latest available for each product.
    /// </summary>
    public override Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        CancellationToken cancellationToken = default)
        => GetStringAppCacheStatusAsync(sessionId, appIds, cancellationToken);
}
