using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;

namespace LancacheManager.Core.Services;

/// <summary>
/// Manages Epic Games Prefill daemon Docker containers.
/// Inherits all shared container management from PrefillDaemonServiceBase.
/// </summary>
public class EpicPrefillDaemonService : PrefillDaemonServiceBase
{
    private const string EpicDockerImage = "ghcr.io/regix1/epic-prefill-daemon:latest";
    private readonly EpicMappingService _mappingService;

    /// <summary>
    /// Event raised when any Epic prefill daemon session becomes authenticated.
    /// EpicMappingService subscribes to this to track daemon auth state.
    /// </summary>
    public event Func<Task>? OnDaemonAuthenticated;

    /// <summary>
    /// Event raised when all Epic prefill daemon sessions are no longer authenticated.
    /// EpicMappingService subscribes to this to track daemon auth state.
    /// </summary>
    public event Func<Task>? OnAllDaemonsLoggedOut;

    public EpicPrefillDaemonService(
        ILogger<EpicPrefillDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        EpicMappingService mappingService)
        : base(logger, notifications, configuration, pathResolver, sessionService, cacheService)
    {
        _mappingService = mappingService;
    }

    // Use Epic hub for per-connection notifications
    protected override bool UseEpicHub => true;

    // Service identity
    protected override string ServiceName => "Epic";
    protected override string ContainerPrefix => "epic-daemon-";
    protected override string DefaultDockerImage => EpicDockerImage;

    protected override string GetImageName()
        => _configuration["Prefill:EpicDockerImage"] ?? EpicDockerImage;

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

    protected override async Task OnSessionAuthenticatedAsync()
    {
        // Fire the OnDaemonAuthenticated event so EpicMappingService can react
        if (OnDaemonAuthenticated != null)
        {
            try
            {
                await OnDaemonAuthenticated.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnDaemonAuthenticated handler");
            }
        }

        // Collect owned games from all authenticated sessions in the background
        _ = Task.Run(async () =>
        {
            try
            {
                await CollectGameMappingsFromAuthenticatedSessionsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to collect Epic game mappings after authentication");
            }
        });
    }

    /// <summary>
    /// Called when all sessions are no longer authenticated.
    /// Fires the OnAllDaemonsLoggedOut C# event so EpicMappingService can react.
    /// </summary>
    protected override async Task OnAllSessionsLoggedOutAsync()
    {
        if (OnAllDaemonsLoggedOut != null)
        {
            try
            {
                await OnAllDaemonsLoggedOut.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnAllDaemonsLoggedOut handler");
            }
        }
    }

    /// <summary>
    /// Iterate all authenticated sessions and collect owned games.
    /// Merges results into the cumulative mapping database.
    /// </summary>
    private async Task CollectGameMappingsFromAuthenticatedSessionsAsync()
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

                var sessionHash = ComputeAnonymousHash(session.UserId);
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

    private static string ComputeAnonymousHash(string userId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(userId));
        return Convert.ToBase64String(hash)[..12];
    }

    /// <summary>
    /// Override cache status check for Epic since Epic uses string app IDs (not uint depot/manifest pairs).
    /// Sends app IDs directly to the Epic daemon which checks build versions against its local cache.
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

        // Send app IDs as strings directly to the Epic daemon (bypassing depot-based lookup)
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
