using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Repositories.Interfaces;

public interface ITagsRepository
{
    Task<List<Tag>> GetAllTagsAsync(CancellationToken cancellationToken = default);
    Task<Tag?> GetTagByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<Tag?> GetTagByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<Tag> CreateTagAsync(Tag tag, CancellationToken cancellationToken = default);
    Task<Tag> UpdateTagAsync(Tag tag, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(int id, CancellationToken cancellationToken = default);
    Task<List<Download>> GetDownloadsWithTagAsync(int tagId, CancellationToken cancellationToken = default);
    Task<List<Tag>> GetTagsForDownloadAsync(int downloadId, CancellationToken cancellationToken = default);
    Task<int> GetTagUsageCountAsync(int tagId, CancellationToken cancellationToken = default);
}
