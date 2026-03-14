using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;


namespace LancacheManager.Core.Services;

/// <summary>
/// Manages Steam daemon Docker containers.
/// Inherits all shared daemon logic from PrefillDaemonServiceBase and provides
/// Steam-specific configuration (image names, SignalR event names, ban checking).
/// </summary>
public partial class SteamDaemonService : PrefillDaemonServiceBase
{
    private const string SteamDockerImage = "ghcr.io/regix1/steam-prefill-daemon:latest";

    private readonly ISteamAuthStorageService _steamAuthStorage;

    public SteamDaemonService(
        ILogger<SteamDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        ISteamAuthStorageService steamAuthStorage)
        : base(logger, notifications, configuration, pathResolver, sessionService, cacheService)
    {
        _steamAuthStorage = steamAuthStorage;
    }

    // === Abstract property implementations ===

    protected override string ServiceName => "Steam";
    protected override string ContainerPrefix => "steam-daemon-";
    protected override string DefaultDockerImage => SteamDockerImage;
    protected override string GetImageName()
        => _configuration["Prefill:DockerImage"] ?? SteamDockerImage;

    // === Diagnostics ===

    protected override string DiagnosticsConnectivityUrl => "https://api.steampowered.com/";
    protected override string[] DiagnosticsDnsDomains => new[]
    {
        "lancache.steamcontent.com",
        "steam.cache.lancache.net"
    };

    // === SignalR event names ===

    protected override string EventSessionCreated => SignalREvents.DaemonSessionCreated;
    protected override string EventSessionUpdated => SignalREvents.DaemonSessionUpdated;
    protected override string EventSessionTerminated => SignalREvents.DaemonSessionTerminated;
    protected override string EventAuthStateChanged => SignalREvents.AuthStateChanged;
    protected override string EventCredentialChallenge => SignalREvents.CredentialChallenge;
    protected override string EventStatusChanged => SignalREvents.StatusChanged;
    protected override string EventPrefillStateChanged => SignalREvents.PrefillStateChanged;
    protected override string EventPrefillProgress => SignalREvents.PrefillProgress;
    protected override string EventPrefillHistoryUpdated => SignalREvents.PrefillHistoryUpdated;
    protected override string EventSessionEnded => SignalREvents.SessionEnded;

    // === Steam-specific credential handling (ban checking) ===

    /// <summary>
    /// Provides an encrypted credential in response to a challenge.
    /// Adds Steam-specific ban checking before delegating to the base implementation.
    /// </summary>
    public override async Task ProvideCredentialAsync(
        string sessionId,
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        // If this is the username credential, check for bans before proceeding
        if (challenge.CredentialType.Equals("username", StringComparison.OrdinalIgnoreCase))
        {
            // Check if this user is banned
            if (await _sessionService.IsUsernameBannedAsync(credential))
            {
                _logger.LogWarning("Blocked banned Steam user {Username} from logging in. Session: {SessionId}",
                    credential, sessionId);

                // Clean up the pending challenge so the next login attempt starts fresh
                session.Client.ClearPendingChallenges();

                // Reset auth state to allow for a clean error display
                session.AuthState = DaemonAuthState.NotAuthenticated;
                await NotifyAuthStateChangeAsync(session);

                throw new UnauthorizedAccessException("This Steam account has been banned from using prefill.");
            }
        }

        // Delegate to base implementation for the actual credential exchange
        await base.ProvideCredentialAsync(sessionId, challenge, credential, cancellationToken);
    }
}
