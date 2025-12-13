using static LancacheManager.Infrastructure.Repositories.StateRepository;

namespace LancacheManager.Infrastructure.Repositories.Interfaces;

public interface IStateRepository
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

    // Theme Preference Methods
    string? GetSelectedTheme();
    void SetSelectedTheme(string? themeId);

    // Default Guest Theme Methods
    string? GetDefaultGuestTheme();
    void SetDefaultGuestTheme(string? themeId);

    // Steam Session Replacement Tracking Methods
    int GetSessionReplacedCount();
    void SetSessionReplacedCount(int count);
    DateTime? GetLastSessionReplacement();
    void SetLastSessionReplacement(DateTime? timestamp);
    void IncrementSessionReplacedCount(); // Convenience method that also sets timestamp
    void ResetSessionReplacedCount(); // Resets both count and timestamp
}
