using LancacheManager.Models;
using LancacheManager.Models.Responses;

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

    // Service RunOnStartup Methods
    bool? GetServiceRunOnStartup(string serviceKey);
    void SetServiceRunOnStartup(string serviceKey, bool runOnStartup);
    void ClearServiceRunOnStartup(string serviceKey);

    // Service NotificationMode Methods (absent key = use the service's DefaultNotificationMode)
    NotificationMode? GetServiceNotificationMode(string serviceKey);
    void SetServiceNotificationMode(string serviceKey, NotificationMode mode);
    void ClearServiceNotificationMode(string serviceKey);

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
    bool HasSteamRefreshToken();

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

    // Stats Exclusion Methods
    List<string> GetExcludedClientIps();
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

    // Setup Wizard State Methods
    string? GetCurrentSetupStep();
    void SetCurrentSetupStep(string? step);
    string? GetDataSourceChoice();
    void SetDataSourceChoice(string? choice);
    string? GetCompletedPlatforms();
    void SetCompletedPlatforms(string? platforms);

    // Epic Guest Prefill Permission Methods
    bool GetEpicGuestPrefillEnabledByDefault();
    void SetEpicGuestPrefillEnabledByDefault(bool enabled);
    int GetEpicGuestPrefillDurationHours();
    void SetEpicGuestPrefillDurationHours(int hours);
    int? GetEpicDefaultGuestMaxThreadCount();
    void SetEpicDefaultGuestMaxThreadCount(int? value);
    string GetEpicDefaultPrefillMaxConcurrency();
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
