namespace LancacheManager.Core.Interfaces;

public interface IDatabaseService
{
    Guid StartResetAsync(List<string> tableNames);
    bool IsResetOperationRunning { get; }
    Task<int> GetLogCount();
}
