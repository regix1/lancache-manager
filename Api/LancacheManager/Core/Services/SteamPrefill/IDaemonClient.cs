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
    /// Cancel running prefill operation.
    /// </summary>
    Task CancelPrefillAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get owned games.
    /// </summary>
    Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Set selected apps for prefill.
    /// </summary>
    Task SetSelectedAppsAsync(List<uint> appIds, CancellationToken cancellationToken = default);

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
    /// Shutdown daemon.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear pending challenge files (file-based mode only, no-op for socket).
    /// </summary>
    void ClearPendingChallenges();
}
