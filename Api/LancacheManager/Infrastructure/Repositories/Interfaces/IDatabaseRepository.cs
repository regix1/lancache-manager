using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Repositories.Interfaces;

public interface IDatabaseRepository
{
    Task ResetDatabase();
    Task<List<Download>> GetDownloadsWithApp0();
    Task MarkApp0DownloadsInactive();
    Task<List<Download>> GetDownloadsWithBadImageUrls();
    Task<int> FixBadImageUrls();
    Task<int> ClearDepotMappings();
}
