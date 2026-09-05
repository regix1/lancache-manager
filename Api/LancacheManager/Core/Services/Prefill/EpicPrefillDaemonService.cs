using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services;

/// <summary>
/// Manages Epic Games Prefill daemon Docker containers.
/// Inherits all shared container management from PrefillDaemonServiceBase.
/// </summary>
public class EpicPrefillDaemonService : PrefillDaemonServiceBase
{
    private const string EpicDockerImage = "ghcr.io/regix1/epic-prefill-daemon:latest";
    private readonly EpicMappingService _mappingService;

    public EpicPrefillDaemonService(
        ILogger<EpicPrefillDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        IStateService stateService,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        EpicMappingService mappingService,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions,
        ILancacheServerLocator locator,
        IPrefillContainerGatewayFactory containerGatewayFactory,
        IActivityRegistry? activityRegistry = null,
        IUnifiedOperationTracker? operationTracker = null)
        : base(logger, notifications, configuration, pathResolver, stateService, sessionService, cacheService, networkOptions, locator, containerGatewayFactory, activityRegistry, operationTracker)
    {
        _mappingService = mappingService;
    }

    // Route per-connection and broadcast notifications to the Epic prefill hub
    protected override string HubRoutingTarget => "epic";

    // Service identity
    protected override string ServiceName => "Epic";
    protected override PrefillPlatform Platform => PrefillPlatform.Epic;
    protected override string ContainerPrefix => "epic-daemon-";
    protected override string DefaultDockerImage => EpicDockerImage;

    protected override string GetImageName()
        => _configuration["Prefill:EpicDockerImage"] ?? EpicDockerImage;
    protected override int GetGuestPermissionDurationHours()
        => _stateService.GetEpicGuestPrefillDurationHours();

    public override IntegrationLoginAvailability GetIntegrationLoginAvailability(Guid? accountId)
    {
        if (accountId is null)
        {
            return new IntegrationLoginAvailability(false, null, "account-required");
        }

        return _mappingService.TryGetSavedLoginAccount(accountId.Value, out var account)
            ? new IntegrationLoginAvailability(true, account, null)
            : new IntegrationLoginAvailability(false, null, "no-saved-login");
    }

    protected override async Task<bool> ReuseIntegrationLoginAsync(
        DaemonSession session,
        Guid accountId,
        Action onCommandDispatched,
        CancellationToken cancellationToken)
    {
        var refreshToken = await _mappingService.CreatePrefillRefreshTokenAsync(accountId, cancellationToken);
        EnsureCurrentSession(session);
        await session.Client.ProvideEpicAutoLoginWithDispatchAsync(
            session.Id,
            refreshToken,
            onCommandDispatched,
            cancellationToken);
        return true;
    }

    // Diagnostics
    protected override string DiagnosticsConnectivityUrl => "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/assets/Windows?label=Live";
    protected override string[] DiagnosticsDnsDomains => new[]
    {
        "epicgames-download1.akamaized.net",
        "egdownload.fastly-edge.com"
    };

    // SignalR event names (Epic-specific)
    protected override string EventSessionCreated => SignalREvents.EpicDaemonSessionCreated;
    protected override string EventSessionUpdated => SignalREvents.EpicDaemonSessionUpdated;
    protected override string EventSessionTerminated => SignalREvents.EpicDaemonSessionTerminated;
    protected override string EventAuthStateChanged => SignalREvents.EpicAuthStateChanged;
    protected override string EventCredentialChallenge => SignalREvents.EpicCredentialChallenge;
    protected override string EventStatusChanged => SignalREvents.EpicStatusChanged;
    protected override string EventPrefillStateChanged => SignalREvents.EpicPrefillStateChanged;
    protected override string EventPrefillProgress => SignalREvents.EpicPrefillProgress;
    protected override string EventPrefillHistoryUpdated => SignalREvents.EpicPrefillHistoryUpdated;
    protected override string EventSessionEnded => SignalREvents.EpicSessionEnded;

    // Epic daemon uses a different HKDF info string for credential encryption
    protected override string CredentialEncryptionHkdfInfo => "EpicPrefill-Credential-Encryption";

    /// <summary>
    /// After authentication, check for banned Epic users and collect owned games from all authenticated sessions in the background.
    /// </summary>
    protected override Task OnAuthenticatedAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Check all authenticated sessions for banned users
                await KickBannedSessionsAsync();

                await CollectSessionGameMappingsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect Epic game mappings after authentication");
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Iterate all authenticated sessions and collect owned games.
    /// Merges results into the cumulative mapping database.
    /// </summary>
    private async Task CollectSessionGameMappingsAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.AuthState != DaemonAuthState.Authenticated) continue;

            try
            {
                _logger.LogInformation(
                    "Collecting Epic game mappings from session {SessionId}",
                    session.Id);

                var games = await session.Client.GetOwnedGamesAsync();
                if (games.Count == 0)
                {
                    _logger.LogInformation("No owned games returned from Epic session {SessionId}", session.Id);
                    continue;
                }

                // The daemon sends raw catalog artwork, not a picked URL - portrait art picked
                // daemon-side is exactly what got Epic image support removed in the first place.
                // Pick the landscape banner here, before it gets persisted.
                foreach (var game in games)
                {
                    if (string.IsNullOrEmpty(game.ImageUrl) && game.KeyImages is { Count: > 0 })
                    {
                        game.ImageUrl = EpicApiDirectClient.GetBestImageUrl(game.KeyImages, game.Name);
                    }

                    // The picked URL is the only part anything downstream reads, so the raw entries stop
                    // here instead of riding along into the merge.
                    game.KeyImages = null;
                }

                var sessionHash = CryptoUtils.ComputeAnonymousHash(session.UserId.ToString());
                var result = await _mappingService.MergeOwnedGamesAsync(games, sessionHash, "prefill-login");

                _logger.LogInformation(
                    "Epic game mapping merge complete: {New} new, {Updated} updated, {Total} total",
                    result.NewGames, result.UpdatedGames, result.TotalGames);

                // Also collect CDN patterns for URL-to-game mapping
                try
                {
                    var cdnInfos = await session.Client.GetCdnInfoAsync();
                    if (cdnInfos.Count > 0)
                    {
                        await _mappingService.MergeCdnPatternsAsync(cdnInfos);
                        _logger.LogInformation(
                            "Epic CDN patterns collected: {Count} patterns from session {SessionId}",
                            cdnInfos.Count, session.Id);
                    }
                }
                catch (Exception cdnEx)
                {
                    _logger.LogWarning(cdnEx,
                        "Failed to collect CDN patterns from Epic session {SessionId} (daemon may not support get-cdn-info yet)",
                        session.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to collect games from Epic session {SessionId}",
                    session.Id);
            }
        }
    }

    /// <summary>
    /// Override cache status check for Epic since Epic uses string app IDs (not uint depot/manifest pairs).
    /// Sends app IDs directly to the Epic daemon which checks build versions against its local cache.
    /// </summary>
    public override Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<string> appIds,
        CancellationToken cancellationToken = default)
        => GetStringAppCacheStatusAsync(sessionId, appIds, cancellationToken);
}
