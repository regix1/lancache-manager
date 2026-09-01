using LancacheManager.Models;


namespace LancacheManager.Core.Interfaces;

public interface IStatsDataService
{
    // limit has no default: an omitted one used to mean the whole Downloads table, which is not a
    // safe thing for a caller to ask for by accident.
    Task<List<Download>> GetLatestDownloadsAsync(int limit, bool activeOnly = false, CancellationToken cancellationToken = default);
}
