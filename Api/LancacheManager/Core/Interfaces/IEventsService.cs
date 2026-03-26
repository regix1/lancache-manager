using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IEventsService : ICrudRepository<Event, long>
{
    // Entity-specific methods
    Task<List<Event>> GetAllEventsAsync(CancellationToken cancellationToken = default);
    Task<List<Event>> GetActiveEventsAsync(CancellationToken cancellationToken = default);
    Task<List<Event>> GetEventsByDateRangeAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);
    Task<Event?> GetEventByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<Event> CreateEventAsync(Event evt, CancellationToken cancellationToken = default);
    Task<Event> UpdateEventAsync(Event evt, CancellationToken cancellationToken = default);
    Task DeleteEventAsync(long id, CancellationToken cancellationToken = default);
    Task<List<Download>> GetDownloadsForEventAsync(long eventId, bool taggedOnly, CancellationToken cancellationToken = default);
    Task TagDownloadAsync(long eventId, long downloadId, bool autoTagged, CancellationToken cancellationToken = default);
    Task UntagDownloadAsync(long eventId, long downloadId, CancellationToken cancellationToken = default);
    Task<int> AutoTagDownloadsForActiveEventsAsync(CancellationToken cancellationToken = default);
}
