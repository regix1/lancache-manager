using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IEventsService : ICrudRepository<Event, int>
{
    // Entity-specific methods
    Task<List<Event>> GetAllEventsAsync(CancellationToken cancellationToken = default);
    Task<List<Event>> GetActiveEventsAsync(CancellationToken cancellationToken = default);
    Task<List<Event>> GetEventsByDateRangeAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);
    Task<Event?> GetEventByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<Event> CreateEventAsync(Event evt, CancellationToken cancellationToken = default);
    Task<Event> UpdateEventAsync(Event evt, CancellationToken cancellationToken = default);
    Task DeleteEventAsync(int id, CancellationToken cancellationToken = default);
    Task<List<Download>> GetDownloadsForEventAsync(int eventId, bool taggedOnly, CancellationToken cancellationToken = default);
    Task TagDownloadAsync(int eventId, int downloadId, bool autoTagged, CancellationToken cancellationToken = default);
    Task UntagDownloadAsync(int eventId, int downloadId, CancellationToken cancellationToken = default);
    Task<int> AutoTagDownloadsForActiveEventsAsync(CancellationToken cancellationToken = default);
}
