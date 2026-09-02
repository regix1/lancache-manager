using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IEventsService : ICrudRepository<Event, long>
{
    // Entity-specific methods
    Task<List<Event>> GetActiveEventsAsync(CancellationToken cancellationToken = default);
    Task<List<Event>> GetByDateRangeAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);
    Task<List<Download>> GetEventDownloadsAsync(long eventId, bool taggedOnly, CancellationToken cancellationToken = default);
    Task TagDownloadAsync(long eventId, long downloadId, bool autoTagged, CancellationToken cancellationToken = default);
    Task UntagDownloadAsync(long eventId, long downloadId, CancellationToken cancellationToken = default);
    Task<int> AutoTagActiveEventsAsync(CancellationToken cancellationToken = default);
}
