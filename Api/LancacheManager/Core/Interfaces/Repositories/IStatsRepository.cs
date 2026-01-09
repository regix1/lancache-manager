using LancacheManager.Models;


namespace LancacheManager.Core.Interfaces.Repositories;

public interface IStatsRepository
{
    Task<List<ServiceStats>> GetServiceStatsAsync(CancellationToken cancellationToken = default);
    Task<List<ClientStats>> GetClientStatsAsync(CancellationToken cancellationToken = default);
    Task<List<Download>> GetLatestDownloadsAsync(int limit = int.MaxValue, CancellationToken cancellationToken = default);
    Task<List<GameStat>> GetTopGamesAsync(int limit = 10, string period = "7d", string sortBy = "downloads", CancellationToken cancellationToken = default);
}
