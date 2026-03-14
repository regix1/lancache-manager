namespace LancacheManager.Core.Interfaces;

public interface IDatabaseService
{
    string StartResetSelectedTablesAsync(List<string> tableNames);
    bool IsResetOperationRunning { get; }
    Task<int> GetLogEntriesCount();
}
