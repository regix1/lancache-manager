namespace LancacheManager.Core.Interfaces;

public interface IDatabaseService
{
    Guid StartResetSelectedTablesAsync(List<string> tableNames);
    bool IsResetOperationRunning { get; }
    Task<int> GetLogEntriesCount();
}
