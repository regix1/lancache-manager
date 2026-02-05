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
    Dictionary<string, long> GetAllLogPositions();

    // Per-datasource total line count methods
    long GetLogTotalLines(string datasourceName);
    void SetLogTotalLines(string datasourceName, long totalLines);
    Dictionary<string, long> GetAllLogTotalLines();

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

    // Data Availability Methods
    bool HasDataLoaded();
    void SetDataLoaded(bool loaded, int mappingCount = 0);

    // Has Processed Logs Methods
    bool GetHasProcessedLogs();
    void SetHasProcessedLogs(bool processed);

    // Last PICS Crawl Methods
    DateTime? GetLastPicsCrawl();
    void SetLastPicsCrawl(DateTime crawlTime);

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
}
