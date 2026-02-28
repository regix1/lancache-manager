using System.Text.Json;
using LancacheManager.Core.Interfaces;
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

    public EpicPrefillDaemonService(
        ILogger<EpicPrefillDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService)
        : base(logger, notifications, configuration, pathResolver, sessionService, cacheService)
    {
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

    // Epic doesn't need OnSessionAuthenticated/OnAllSessionsLoggedOut
    // (no SteamKit2 session conflict to manage)
    // The base class defaults (Task.CompletedTask) are fine

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
