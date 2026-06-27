using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Models;
using LancacheManager.Services.Xbox;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services;

/// <summary>
/// Manages Xbox / Microsoft Store Prefill daemon Docker containers.
/// Inherits all shared container management from PrefillDaemonServiceBase.
///
/// Xbox prefill is login-required: the user authenticates with Microsoft via the OAuth
/// device-code flow in their OWN browser, so no password/cookie ever enters the container.
/// Because it is login-required, the session starts <see cref="DaemonAuthState.NotAuthenticated"/>
/// (the base default - NOT overridden), and bans must be enforced after authentication when the
/// gamertag/display name is known (mirrors Epic). The daemon's status DisplayName populates
/// session.Username via the generalized capture in PrefillDaemonServiceBase.Notifications.cs.
/// </summary>
public class XboxPrefillDaemonService : PrefillDaemonServiceBase
{
    private const string XboxDockerImage = "ghcr.io/regix1/xbox-prefill-daemon:latest";

    private readonly XboxMappingService _mappingService;

    public XboxPrefillDaemonService(
        ILogger<XboxPrefillDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        IStateService stateService,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        XboxMappingService mappingService,
        IScheduledPrefillAuthService scheduledPrefillAuthService,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions)
        : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, scheduledPrefillAuthService, networkOptions)
    {
        _mappingService = mappingService;
    }

    // Route per-connection and broadcast notifications to the Xbox prefill hub
    protected override string HubRoutingTarget => "xbox";

    // Service identity
    protected override string ServiceName => "Xbox";
    protected override string ContainerPrefix => "xbox-daemon-";
    protected override string DefaultDockerImage => XboxDockerImage;

    // Xbox is login-required - it inherits InitialAuthState = NotAuthenticated (do NOT override
    // to Authenticated; that is only for anonymous services like Battle.net/Riot).

    protected override string GetImageName()
        => _configuration["Prefill:XboxDockerImage"] ?? XboxDockerImage;

    // Diagnostics
    protected override string DiagnosticsConnectivityUrl => "https://displaycatalog.mp.microsoft.com/";
    protected override string[] DiagnosticsDnsDomains => new[]
    {
        "assets1.xboxlive.com",
        "dl.delivery.mp.microsoft.com"
    };

    // SignalR event names (Xbox-specific)
    protected override string EventSessionCreated => SignalREvents.XboxDaemonSessionCreated;
    protected override string EventSessionUpdated => SignalREvents.XboxDaemonSessionUpdated;
    protected override string EventSessionTerminated => SignalREvents.XboxDaemonSessionTerminated;
    protected override string EventAuthStateChanged => SignalREvents.XboxAuthStateChanged;
    protected override string EventCredentialChallenge => SignalREvents.XboxCredentialChallenge;
    protected override string EventStatusChanged => SignalREvents.XboxStatusChanged;
    protected override string EventPrefillStateChanged => SignalREvents.XboxPrefillStateChanged;
    protected override string EventPrefillProgress => SignalREvents.XboxPrefillProgress;
    protected override string EventPrefillHistoryUpdated => SignalREvents.XboxPrefillHistoryUpdated;
    protected override string EventSessionEnded => SignalREvents.XboxSessionEnded;

    // Xbox daemon uses a distinct HKDF info string for the socket handshake encryption context
    protected override string CredentialEncryptionHkdfInfo => "XboxPrefill-Credential-Encryption";

    /// <summary>
    /// After authentication, check for banned Xbox users. Mirrors Epic: Xbox uses device-code auth
    /// (no credential paste), so the username ban cannot be checked at credential time - it must be
    /// enforced once the daemon reports the authenticated gamertag/display name.
    ///
    /// NOTE: catalog mapping is deliberately NOT driven from here. Mapping is owned by the scheduled
    /// <see cref="LancacheManager.Services.Xbox.XboxCatalogMappingService"/> (a schedule + manual trigger
    /// + an on-authentication nudge it wires via <c>OnDaemonAuthenticated</c>), so prefill is no longer
    /// the mapping mechanic - a game downloaded on a real Xbox/PC gets named without any prefill.
    /// </summary>
    protected override Task OnAuthenticatedAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await KickBannedSessionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enforce Xbox username bans after authentication");
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks all authenticated Xbox sessions against the username ban list.
    /// If a banned user is found, their session is terminated immediately.
    /// This is the STRONG (re-auth-proof) username ban; the base also enforces a UserId-GUID ban at
    /// session-create. If the daemon does not surface a stable DisplayName, session.Username is empty
    /// and Xbox falls back to the UserId-only ban (works, but evadable by re-auth).
    /// Mirrors EpicPrefillDaemonService.KickBannedSessionsAsync (same contract, no Epic business logic).
    /// </summary>
    private async Task KickBannedSessionsAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.AuthState != DaemonAuthState.Authenticated) continue;
            if (string.IsNullOrEmpty(session.Username)) continue;

            if (await _sessionService.IsUsernameBannedAsync(session.Username))
            {
                _logger.LogWarning(
                    "Blocked banned Xbox user {Username} after authentication. Terminating session {SessionId}",
                    session.Username, session.Id);

                await TerminateSessionAsync(session.Id, "Banned by admin", true);
            }
        }
    }

    /// <summary>
    /// Collects the Xbox catalog from every authenticated session via the daemon's <c>get-cdn-info</c>
    /// command and persists it (product-&gt;title mappings + per-file CDN URL fragments) via
    /// <see cref="XboxMappingService.MergeDaemonCatalogAsync"/>, so the resolver can map opaque
    /// <c>wsus</c>/<c>xboxlive</c> cache hits back to Xbox titles. This is the PRODUCER side of the Xbox
    /// naming feature - without it the XboxCdnPatterns/XboxGameMappings tables stay empty and downloads
    /// never auto-name. It is DRIVEN BY <see cref="LancacheManager.Services.Xbox.XboxCatalogMappingService"/>
    /// (a schedule + manual trigger + an on-authentication nudge), NOT by a user prefill, so a game
    /// downloaded on a real Xbox/PC is named on the next pass (get-cdn-info returns the FULL owned catalog,
    /// not just prefilled titles). The method lives here because it needs the protected session set.
    /// Best-effort: a failure here never affects the prefill session. Returns the total number of CDN
    /// patterns newly persisted across all authenticated sessions.
    /// </summary>
    public async Task<int> RefreshCatalogFromActiveSessionsAsync(CancellationToken ct = default)
    {
        var totalNewPatterns = 0;

        foreach (var session in _sessions.Values)
        {
            if (session.AuthState != DaemonAuthState.Authenticated) continue;

            try
            {
                _logger.LogInformation(
                    "Collecting Xbox CDN patterns from session {SessionId}",
                    session.Id);

                var cdnInfos = await session.Client.GetCdnInfoAsync(ct);
                if (cdnInfos.Count == 0)
                {
                    _logger.LogInformation("No Xbox CDN info returned from session {SessionId}", session.Id);
                    continue;
                }

                var newPatterns = await _mappingService.MergeDaemonCatalogAsync(cdnInfos, ct);
                totalNewPatterns += newPatterns;
                _logger.LogInformation(
                    "Xbox catalog collected from session {SessionId}: {Apps} apps, {NewPatterns} new CDN patterns",
                    session.Id, cdnInfos.Count, newPatterns);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to collect Xbox CDN patterns from session {SessionId} (daemon may not support get-cdn-info yet)",
                    session.Id);
            }
        }

        return totalNewPatterns;
    }

    /// <summary>
    /// Override cache status check for Xbox since it uses string app IDs (Microsoft Store ProductIds),
    /// not uint depot/manifest pairs. Sends app IDs directly to the Xbox daemon which checks prefilled
    /// versions against the latest available for each product.
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

        // Send app IDs as strings directly to the Xbox daemon (bypassing depot-based lookup)
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
