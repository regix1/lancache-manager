using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Common interface for daemon communication (file-based or socket-based).
/// </summary>
public interface IDaemonClient : IDisposable
{
    event Func<CredentialChallenge, Task>? OnCredentialChallenge;
    event Func<DaemonStatus, Task>? OnStatusUpdate;
    event Func<SocketPrefillProgress, Task>? OnProgressUpdate;
    event Func<string, Task>? OnError;
    event Func<Task>? OnDisconnected;

    /// <summary>
    /// Connects to the daemon.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current daemon status.
    /// </summary>
    Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a command to the daemon and waits for response.
    /// </summary>
    Task<CommandResponse> SendCommandAsync(
        string type,
        Dictionary<string, string>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Start login process.
    /// </summary>
    Task<CredentialChallenge?> StartLoginAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Provide encrypted credential in response to a challenge.
    /// </summary>
    Task ProvideCredentialAsync(
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Request a non-interactive auto-login (ECDH) challenge from the daemon.
    /// </summary>
    Task<CredentialChallenge?> GetAutoLoginChallengeAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform a non-interactive auto-login by encrypting a {username, refreshToken}
    /// payload and sending it to the daemon. Returns true on success.
    /// </summary>
    Task<bool> ProvideAutoLoginAsync(
        string sessionId,
        string username,
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform a non-interactive Epic auto-login by encrypting a {refreshToken} payload
    /// and sending it to the daemon. Returns true on success.
    /// </summary>
    Task<bool> ProvideEpicAutoLoginAsync(
        string sessionId,
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Perform a non-interactive Xbox auto-login by encrypting a {refreshToken, deviceKeyPkcs8}
    /// payload and sending it to the daemon. Returns true on success.
    /// </summary>
    Task<bool> ProvideXboxAutoLoginAsync(
        string sessionId,
        string refreshToken,
        string deviceKeyPkcs8,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for next credential challenge.
    /// </summary>
    Task<CredentialChallenge?> WaitForChallengeAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel pending login.
    /// </summary>
    Task CancelLoginAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests the daemon log out and forget its stored account in place (no container restart).
    /// Sends the <c>logout</c> command. Returns true when the daemon acknowledges success; false when
    /// the response reports failure or the round-trip itself fails (socket error, timeout). Callers
    /// must treat false as "not supported here" and fall back to a stop+restart of the container.
    /// NOTE: steam/epic daemon images built before the account-file-delete fix (see
    /// SteamPrefill/EpicPrefill <c>SocketCommandInterface.HandleLogoutAsync</c>) already had a
    /// <c>logout</c> command and will report success here even though they only tear down the live
    /// session without deleting the stored account file - a true-success response is NOT proof the
    /// account was forgotten on an un-updated image. This is in-band indistinguishable from a real
    /// success and is not detected; it self-resolves once the daemon image is rebuilt.
    /// </summary>
    Task<bool> LogoutAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Same round-trip as <see cref="LogoutAsync"/>, but also exposes whether the daemon rejected the
    /// command because this session hasn't finished authenticating yet - an older daemon image's
    /// pre-login command gate (its <c>PreLoginCommands</c> allowlist is missing "logout"), not a
    /// genuine failure. The default implementation adapts <see cref="LogoutAsync"/> for
    /// implementations (and test fakes) that don't override it, reporting <c>RequiresLogin: false</c>;
    /// only <c>SocketDaemonClient</c> and <c>TcpDaemonClient</c> currently override this with the real
    /// signal carried on the daemon's <c>CommandResponse.RequiresLogin</c>.
    /// </summary>
    async Task<LogoutOutcome> LogoutWithReasonAsync(CancellationToken cancellationToken = default)
    {
        var success = await LogoutAsync(cancellationToken);
        return new LogoutOutcome(success, RequiresLogin: false);
    }

    /// <summary>
    /// Cancel running prefill operation.
    /// </summary>
    Task CancelPrefillAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get owned games.
    /// </summary>
    Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get CDN URL patterns for games (Epic only).
    /// </summary>
    Task<List<CdnInfo>> GetCdnInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Set selected apps for prefill.
    /// </summary>
    Task SetSelectedAppsAsync(List<string> appIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start prefill operation.
    /// </summary>
    Task<PrefillResult> PrefillAsync(
        bool all = false,
        bool recent = false,
        bool recentlyPurchased = false,
        int? top = null,
        bool force = false,
        List<string>? operatingSystems = null,
        int? maxConcurrency = null,
        List<CachedDepotInput>? cachedDepots = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the temporary cache.
    /// </summary>
    Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get cache info.
    /// </summary>
    Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get selected apps status with download sizes.
    /// </summary>
    Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(
        List<string>? operatingSystems = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check cache status by comparing cached depots against Steam manifests.
    /// </summary>
    Task<CacheStatusResult> CheckCacheStatusAsync(
        List<CachedDepotInput> cachedDepots,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shutdown daemon.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear pending challenge files (file-based mode only, no-op for socket).
    /// </summary>
    void ClearPendingChallenges();
}
