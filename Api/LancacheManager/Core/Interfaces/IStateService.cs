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

    // Cache Clear Operations Methods (stored in data/operations/cache_operations.json)
    List<CacheClearOperation> GetCacheClearOperations();
    void RemoveCacheClearOperation(string id);
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
    bool GetHasProcessedLogs();
    void SetHasProcessedLogs(bool processed);

    // Last PICS Crawl Methods
    DateTime? GetLastPicsCrawl();
    void SetLastPicsCrawl(DateTime crawlTime);

    // Service Interval Methods
    double? GetServiceInterval(string serviceKey);
    void SetServiceInterval(string serviceKey, double hours);
    void ClearServiceInterval(string serviceKey);

    // Service RunOnStartup Methods
    bool? GetServiceRunOnStartup(string serviceKey);
    void SetServiceRunOnStartup(string serviceKey, bool runOnStartup);
    void ClearServiceRunOnStartup(string serviceKey);

    // Crawl Interval Methods
    double GetCrawlIntervalHours();
    void SetCrawlIntervalHours(double hours);

    // Crawl Mode Methods
    object GetCrawlIncrementalMode();
    void SetCrawlIncrementalMode(object mode);

    // Depot Processing Methods
    DepotProcessingState GetDepotProcessingState();

    // Steam Authentication Methods
    string? GetSteamAuthMode();
    void SetSteamAuthMode(string mode);
    string? GetSteamUsername();
    void SetSteamUsername(string? username);
    string? GetSteamRefreshToken();
    void SetSteamRefreshToken(string? token);
    bool HasSteamRefreshToken();

    // Guest Session Duration Methods
    int GetGuestSessionDurationHours();
    void SetGuestSessionDurationHours(int hours);

    // Guest Mode Lock Methods
    bool GetGuestModeLocked();
    void SetGuestModeLocked(bool locked);

    // Theme Preference Methods
    string? GetSelectedTheme();
    void SetSelectedTheme(string? themeId);

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
}
