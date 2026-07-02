using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services;

/// <summary>
/// Manages Battle.net Prefill daemon Docker containers.
/// Inherits all shared container management from PrefillDaemonServiceBase.
///
/// Battle.net prefill is fully anonymous - it prefills public Blizzard CDN content with no
/// account login or credentials. A connected daemon reports itself as logged-in/initialized
/// immediately (the daemon's status update drives the session straight to
/// <see cref="DaemonAuthState.Authenticated"/> via the base status handler), so there is no
/// login step, credential challenge, or owned-game-library collection. The
/// EventAuthStateChanged/EventCredentialChallenge events still need constant values (the base
/// requires them) but are not expected to fire in normal operation.
/// </summary>
public class BattleNetDaemonService : PrefillDaemonServiceBase
{
    private const string BattleNetDockerImage = "ghcr.io/regix1/battlenet-prefill-daemon:latest";

    public BattleNetDaemonService(
        ILogger<BattleNetDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        IStateService stateService,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions)
        : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions)
    {
    }

    // Route per-connection and broadcast notifications to the Battle.net prefill hub
    protected override string HubRoutingTarget => "battlenet";

    // Service identity
    protected override string ServiceName => "battlenet";
    protected override string ContainerPrefix => "battlenet-prefill-";
    protected override string DefaultDockerImage => BattleNetDockerImage;

    // Battle.net is fully anonymous - a session is authenticated/ready the moment it is created,
    // with no login step or credential challenge. This drives the returned session DTO straight to
    // Authenticated so the client unlocks prefill commands immediately.
    protected override DaemonAuthState InitialAuthState => DaemonAuthState.Authenticated;

    protected override string GetImageName()
        => _configuration["Prefill:BattlenetDockerImage"] ?? BattleNetDockerImage;
    protected override int GetGuestPermissionDurationHours()
        => _stateService.GetBattleNetGuestPrefillDurationHours();

    // Diagnostics
    protected override string DiagnosticsConnectivityUrl => "https://us.cdn.blizzard.com/";
    protected override string[] DiagnosticsDnsDomains => new[]
    {
        "level3.blizzard.com",
        "blzddist1-a.akamaihd.net"
    };

    // SignalR event names (Battle.net-specific)
    protected override string EventSessionCreated => SignalREvents.BattleNetDaemonSessionCreated;
    protected override string EventSessionUpdated => SignalREvents.BattleNetDaemonSessionUpdated;
    protected override string EventSessionTerminated => SignalREvents.BattleNetDaemonSessionTerminated;
    protected override string EventAuthStateChanged => SignalREvents.BattleNetAuthStateChanged;
    protected override string EventCredentialChallenge => SignalREvents.BattleNetCredentialChallenge;
    protected override string EventStatusChanged => SignalREvents.BattleNetStatusChanged;
    protected override string EventPrefillStateChanged => SignalREvents.BattleNetPrefillStateChanged;
    protected override string EventPrefillProgress => SignalREvents.BattleNetPrefillProgress;
    protected override string EventPrefillHistoryUpdated => SignalREvents.BattleNetPrefillHistoryUpdated;
    protected override string EventSessionEnded => SignalREvents.BattleNetSessionEnded;

    // Battle.net daemon uses a distinct HKDF info string for the socket handshake encryption context
    protected override string CredentialEncryptionHkdfInfo => "BattleNetPrefill-Credential-Encryption";

    /// <summary>
    /// Override cache status check for Battle.net since it uses string app IDs (TACT product
    /// slugs), not uint depot/manifest pairs. Sends app IDs directly to the Battle.net daemon
    /// which checks prefilled versions against the latest available for each product.
    /// </summary>
    public override async Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        if (appIds == null || appIds.Count == 0)
        {
            return new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "No app IDs provided" };
        }

        // Send app IDs as strings directly to the Battle.net daemon (bypassing depot-based lookup)
        var parameters = new Dictionary<string, string>
        {
            ["appIds"] = JsonSerializer.Serialize(appIds)
        };

        var response = await session.Client.SendCommandAsync("check-cache-status", parameters,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        if (!response.Success)
        {
            return new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = response.Error ?? "Failed to check cache status" };
        }

        if (response.Data is JsonElement element)
        {
            var result = JsonSerializer.Deserialize<CacheStatusResult>(element.GetRawText());
            return result ?? new CacheStatusResult { Message = "Failed to parse result" };
        }

        return new CacheStatusResult { Message = response.Message };
    }
}
