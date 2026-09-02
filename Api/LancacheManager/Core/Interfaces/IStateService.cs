using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IStateService
{
    AppState GetState();
    void SaveState(AppState state);
    void UpdateState(Action<AppState> updater);

    // Log Processing Methods
    long GetLogPosition();
    void SetLogPosition(long position);

    // Per-datasource log position methods (for multi-datasource support)
    long GetLogPosition(string datasourceName);
    void SetLogPosition(string datasourceName, long position);
    // Per-datasource total line count methods
    long GetLogTotalLines(string datasourceName);
    void SetLogTotalLines(string datasourceName, long totalLines);
    // Per-datasource, per-source-stem series offsets (bare-metal / mixed layouts)
    Dictionary<string, long> GetLogSourcePositions(string datasourceName);
    void SetLogSourcePositions(string datasourceName, Dictionary<string, long> positions);
    // Subtract purge-removed line counts from the saved positions. A log purge rewrites the
    // access log in place, so every line index past the removed lines shifts down; an
    // unadjusted position points that many lines into never-ingested content and the next
    // incremental run silently skips it. The position moves back by the already-read subset
    // (first map); the on-disk total-line count moves down by all removals (second map).
    void ReduceLogPositionsAfterPurge(
        string datasourceName,
        IReadOnlyDictionary<string, long> linesRemovedBeforePositionByStem,
        IReadOnlyDictionary<string, long> linesRemovedByStem);
    // Snapshot a datasource's per-stem positions to a temp JSON file for a purge's
    // --stem-positions argument. Null when no positions exist; caller deletes the file.
    Task<string?> WriteStemPositionsTempFileAsync(string datasourceName);
    void ClearLogSourcePositions(string datasourceName, IEnumerable<string> stems);
    // Per-datasource ingestion diagnostics (typed counters + missing-source warning)
    LogIngestDiagnostics? GetLogIngestDiagnostics(string datasourceName);
    void SetLogIngestDiagnostics(string datasourceName, LogIngestDiagnostics diagnostics);
    void SetLogMissingSourcesWarning(string datasourceName, string? message);

    // Cache Clear Operations Methods (stored in data/operations/cache_operations.json)
    List<CacheClearOperation> GetCacheClearOperations();
    void RemoveCacheClearOperation(Guid id);
    void UpdateCacheClearOperations(Action<List<CacheClearOperation>> updater);

    // Operation States Methods (stored in data/operations/operation_history.json)
    List<OperationState> GetOperationStates();
    void RemoveOperationState(string id);
    void UpdateOperationStates(Action<List<OperationState>> updater);

    // Setup Completed Methods
    bool GetSetupCompleted();
    void SetSetupCompleted(bool completed);
    Task WaitForSetupCompletedAsync(CancellationToken cancellationToken);

    // Has Processed Logs Methods (async wait)
    Task WaitForLogsProcessedAsync(CancellationToken cancellationToken);

    // Data Availability Methods
    bool HasDataLoaded();
    void SetDataLoaded(bool loaded, int mappingCount = 0);

    // Has Processed Logs Methods
    bool HasProcessedLogs();
    void SetHasProcessedLogs(bool processed);

    // Last PICS Crawl Methods
    DateTime? GetLastPicsCrawl();
    void SetLastPicsCrawl(DateTime crawlTime);

    // Status Check (DNS diagnostics) Methods
    StatusCheckResult? GetStatusCheckResult();
    void SetStatusCheckResult(StatusCheckResult result);

    // Status Check DNS resolver mode ("auto" | "bridge" | "host"); persisted verbatim.
    string GetStatusCheckResolverMode();
    void SetStatusCheckResolverMode(string mode);

    // Epic Mapping Last-Collection Methods
    DateTime? GetEpicMappingCollectedAt();
    void SetEpicMappingLastCollection(DateTime collectionTime);

    // Service Interval Methods
    double? GetServiceInterval(string serviceKey);
    void SetServiceInterval(string serviceKey, double hours);
    void ClearServiceInterval(string serviceKey);

    // Service Custom Schedule Methods (absent key = the service runs on its interval)
    CustomSchedule? GetServiceCustomSchedule(string serviceKey);
    void SetServiceCustomSchedule(string serviceKey, CustomSchedule schedule);
    void ClearServiceCustomSchedule(string serviceKey);

    // Per-datasource cache-size override methods (missing/zero = automatic detection)
    Dictionary<string, long> GetDatasourceCacheSizeOverrides();
    void SetDatasourceCacheSizeOverride(string datasourceName, long? bytes);

    // Service RunOnStartup Methods
    bool? GetServiceRunOnStartup(string serviceKey);
    void SetServiceRunOnStartup(string serviceKey, bool runOnStartup);
    void ClearServiceRunOnStartup(string serviceKey);

    // Service NotificationMode Methods (absent key = use the service's DefaultNotificationMode)
    NotificationMode? GetServiceNotificationMode(string serviceKey);
    void SetServiceNotificationMode(string serviceKey, NotificationMode mode);
    void ClearServiceNotificationMode(string serviceKey);

    // Service NotificationDisplayMode Methods (absent key = Full; unlike NotificationMode there is no
    // per-service compiled default to resolve against, so the registry mapper resolves the absent case)
    NotificationDisplayMode? GetServiceNotificationDisplayMode(string serviceKey);
    void SetServiceNotificationDisplayMode(string serviceKey, NotificationDisplayMode mode);
    void ClearServiceNotificationDisplayMode(string serviceKey);

    // Scheduled Prefill Config Methods
    // Get returns a validated, default-constructed-if-missing config; Set validates before persisting
    // (both throw ScheduledPrefillConfigValidationException on invalid config).
    ScheduledPrefillConfigDto GetScheduledPrefillConfig();
    void SetScheduledPrefillConfig(ScheduledPrefillConfigDto config);

    // Scheduled Prefill Per-Service Last-Run Methods (durable, keyed by PrefillPlatform name).
    // Drive the independent per-service due-check + next-run computation; persist to state.json.
    // This is the SCHEDULE BASIS (anchor + advance-on-attempt), not the display "last run".
    DateTime? GetScheduledPrefillServiceLastRun(string platform);
    void SetScheduledPrefillServiceLastRun(string platform, DateTime lastRunUtc);
    void ClearScheduledPrefillServiceLastRun();

    // Actual per-service last-run (the honest "Last run" the schedule view shows): stamped ONLY when a
    // service genuinely runs, so it stays null until the first real run. Durable across restart.
    DateTime? GetScheduledPrefillServiceLastActualRun(string platform);
    void SetScheduledPrefillServiceLastActualRun(string platform, DateTime lastRunUtc);

    // Crawl Interval Methods
    double GetCrawlIntervalHours();
    void SetCrawlIntervalHours(double hours);

    // Per-game metric cap: how many games /metrics reports, so the setting survives a restart.
    int GetTopGameCount();
    void SetTopGameCount(int count);

    // Crawl Mode Methods
    object GetCrawlIncrementalMode();
    void SetCrawlIncrementalMode(object mode);

    // Depot Processing Methods
    DepotProcessingState GetDepotProcessingState();

    // Steam Authentication Methods
    SteamAuthMode? GetSteamAuthMode();
    void SetSteamAuthMode(SteamAuthMode mode);
    string? GetSteamUsername();
    void SetSteamUsername(string? username);
    string? GetSteamRefreshToken();
    void SetSteamRefreshToken(string? token);

    // Guest Session Duration Methods
    int? GetGuestSessionDurationHours();
    void SetGuestSessionDurationHours(int? hours);

    // Guest Mode Lock Methods
    bool GetGuestModeLocked();
    void SetGuestModeLocked(bool locked);

    // Default Guest Theme Methods
    string? GetDefaultGuestTheme();
    void SetDefaultGuestTheme(string? themeId);

    // Refresh Rate Methods
    string GetRefreshRate();
    void SetRefreshRate(string rate);

    // Default Guest Refresh Rate Methods
    string GetDefaultGuestRefreshRate();
    void SetDefaultGuestRefreshRate(string rate);

    // Metrics Authentication Toggle Methods
    bool? GetRequireAuthForMetrics();
    void SetRequireAuthForMetrics(bool? value);

    // Evicted Data Mode Methods
    string GetEvictedDataMode();
    void SetEvictedDataMode(string mode);

    // Eviction Scan Notification Methods
    bool GetEvictionScanNotifications();
    void SetEvictionScanNotifications(bool enabled);

    // Orphaned Downloads Prune Methods (eviction scan opt-in)
    bool GetPruneOrphanedDownloads();
    void SetPruneOrphanedDownloads(bool enabled);

    // Client Hostname Lookup Methods (reverse DNS on client addresses)
    bool GetClientHostnameLookup();
    void SetClientHostnameLookup(bool enabled);

    /// <summary>The DNS server an admin named for client hostname lookups, or null to discover one.</summary>
    string? GetClientHostnameResolver();
    void SetClientHostnameResolver(string? resolverIp);

    /// <summary>Whether guest sessions are shown client machine names. Off by default.</summary>
    bool GetClientHostnameGuestAccess();
    void SetClientHostnameGuestAccess(bool allowed);

    /// <summary>Whether the lookup may ask the router addresses of each client's own subnet.</summary>
    bool GetClientHostnameRouterLookup();
    void SetClientHostnameRouterLookup(bool enabled);

    /// <summary>Whether the lookup may ask Docker for resolvers and the Docker host.</summary>
    bool GetClientHostnameDockerLookup();
    void SetClientHostnameDockerLookup(bool enabled);

    // Stats Exclusion Methods
    void SetExcludedClientIps(List<string> ips);
    List<ClientExclusionRule> GetExcludedClientRules();
    void SetExcludedClientRules(List<ClientExclusionRule> rules);
    List<string> GetHiddenClientIps();
    List<string> GetStatsExcludedOnlyClientIps();

    // Guest Prefill Permission Methods
    bool GetGuestPrefillEnabledByDefault();
    void SetGuestPrefillEnabledByDefault(bool enabled);
    int GetGuestPrefillDurationHours();
    void SetGuestPrefillDurationHours(int hours);

    // Validity window (days, 1-365) for a persistent admin login before re-login is required
    int GetAdminPersistentLoginValidityDays();
    void SetAdminPersistentLoginValidityDays(int days);

    // Default Guest Max Thread Count
    int? GetDefaultGuestMaxThreadCount();
    void SetDefaultGuestMaxThreadCount(int? value);

    // Default Prefill Settings
    List<string> GetDefaultPrefillOperatingSystems();
    void SetDefaultPrefillOperatingSystems(List<string> osList);
    string GetDefaultPrefillMaxConcurrency();
    void SetDefaultPrefillMaxConcurrency(string value);

    // Epic Guest Prefill Permission Methods
    bool GetEpicGuestPrefillEnabledByDefault();
    void SetEpicGuestPrefillEnabledByDefault(bool enabled);
    int GetEpicGuestPrefillDurationHours();
    void SetEpicGuestPrefillDurationHours(int hours);
    int? GetEpicDefaultGuestMaxThreadCount();
    void SetEpicDefaultGuestMaxThreadCount(int? value);
    void SetEpicDefaultPrefillMaxConcurrency(string value);

    // Battle.net Guest Prefill Permission Methods
    bool GetBattleNetGuestPrefillEnabledByDefault();
    void SetBattleNetGuestPrefillEnabledByDefault(bool enabled);
    int GetBattleNetGuestPrefillDurationHours();
    void SetBattleNetGuestPrefillDurationHours(int hours);

    // Riot Guest Prefill Duration (also caps that service's guest container lifetime - see
    // PrefillDaemonServiceBase.GetGuestPermissionDurationHours)
    int GetRiotGuestPrefillDurationHours();
    void SetRiotGuestPrefillDurationHours(int hours);

    // Xbox Guest Prefill Duration (also caps that service's guest container lifetime - see
    // PrefillDaemonServiceBase.GetGuestPermissionDurationHours)
    int GetXboxGuestPrefillDurationHours();
    void SetXboxGuestPrefillDurationHours(int hours);
}
