using LancacheManager.Models;


namespace LancacheManager.Core.Interfaces;

public interface IStatsDataService
{
    Task<List<Download>> GetLatestDownloadsAsync(int limit = int.MaxValue, bool activeOnly = false, CancellationToken cancellationToken = default);
}
