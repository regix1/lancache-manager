using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces.Repositories;

public interface IDatabaseRepository
{
    Task ResetDatabase();
    string StartResetSelectedTablesAsync(List<string> tableNames);
    bool IsResetOperationRunning { get; }
    Task<int> GetLogEntriesCount();
    Task<List<Download>> GetDownloadsWithApp0();
    Task MarkApp0DownloadsInactive();
    Task<List<Download>> GetDownloadsWithBadImageUrls();
    Task<int> FixBadImageUrls();
    Task<int> ClearDepotMappings();
}
