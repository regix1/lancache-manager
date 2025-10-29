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

    // Cache Clear Operations Methods
    List<CacheClearOperation> GetCacheClearOperations();
    void RemoveCacheClearOperation(string id);

    // Operation States Methods
    List<OperationState> GetOperationStates();
    void RemoveOperationState(string id);

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
    bool GetCrawlIncrementalMode();
    void SetCrawlIncrementalMode(bool incremental);

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
}
