using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Repositories;

public class EventsRepository : IEventsRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<EventsRepository> _logger;

    public EventsRepository(AppDbContext context, ILogger<EventsRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<Event>> GetAllEventsAsync(CancellationToken cancellationToken = default)
    {
        var events = await _context.Events
            .AsNoTracking()
            .OrderByDescending(e => e.StartTimeUtc)
            .ToListAsync(cancellationToken);

        foreach (var evt in events)
        {
            evt.StartTimeUtc = DateTime.SpecifyKind(evt.StartTimeUtc, DateTimeKind.Utc);
            evt.EndTimeUtc = DateTime.SpecifyKind(evt.EndTimeUtc, DateTimeKind.Utc);
            evt.CreatedAtUtc = DateTime.SpecifyKind(evt.CreatedAtUtc, DateTimeKind.Utc);
            if (evt.UpdatedAtUtc.HasValue)
            {
                evt.UpdatedAtUtc = DateTime.SpecifyKind(evt.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
        }

        return events;
    }

    public async Task<List<Event>> GetActiveEventsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var events = await _context.Events
            .AsNoTracking()
            .Where(e => e.StartTimeUtc <= now && e.EndTimeUtc >= now)
            .OrderBy(e => e.StartTimeUtc)
            .ToListAsync(cancellationToken);

        foreach (var evt in events)
        {
            evt.StartTimeUtc = DateTime.SpecifyKind(evt.StartTimeUtc, DateTimeKind.Utc);
            evt.EndTimeUtc = DateTime.SpecifyKind(evt.EndTimeUtc, DateTimeKind.Utc);
            evt.CreatedAtUtc = DateTime.SpecifyKind(evt.CreatedAtUtc, DateTimeKind.Utc);
            if (evt.UpdatedAtUtc.HasValue)
            {
                evt.UpdatedAtUtc = DateTime.SpecifyKind(evt.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
        }

        return events;
    }

    public async Task<List<Event>> GetEventsByDateRangeAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var events = await _context.Events
            .AsNoTracking()
            .Where(e =>
                (e.StartTimeUtc >= startUtc && e.StartTimeUtc <= endUtc) ||
                (e.EndTimeUtc >= startUtc && e.EndTimeUtc <= endUtc) ||
                (e.StartTimeUtc <= startUtc && e.EndTimeUtc >= endUtc))
            .OrderBy(e => e.StartTimeUtc)
            .ToListAsync(cancellationToken);

        foreach (var evt in events)
        {
            evt.StartTimeUtc = DateTime.SpecifyKind(evt.StartTimeUtc, DateTimeKind.Utc);
            evt.EndTimeUtc = DateTime.SpecifyKind(evt.EndTimeUtc, DateTimeKind.Utc);
            evt.CreatedAtUtc = DateTime.SpecifyKind(evt.CreatedAtUtc, DateTimeKind.Utc);
            if (evt.UpdatedAtUtc.HasValue)
            {
                evt.UpdatedAtUtc = DateTime.SpecifyKind(evt.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
        }

        return events;
    }

    public async Task<Event?> GetEventByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var evt = await _context.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (evt != null)
        {
            evt.StartTimeUtc = DateTime.SpecifyKind(evt.StartTimeUtc, DateTimeKind.Utc);
            evt.EndTimeUtc = DateTime.SpecifyKind(evt.EndTimeUtc, DateTimeKind.Utc);
            evt.CreatedAtUtc = DateTime.SpecifyKind(evt.CreatedAtUtc, DateTimeKind.Utc);
            if (evt.UpdatedAtUtc.HasValue)
            {
                evt.UpdatedAtUtc = DateTime.SpecifyKind(evt.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
        }

        return evt;
    }

    public async Task<Event> CreateEventAsync(Event evt, CancellationToken cancellationToken = default)
    {
        evt.CreatedAtUtc = DateTime.UtcNow;
        _context.Events.Add(evt);
        await _context.SaveChangesAsync(cancellationToken);

        evt.StartTimeUtc = DateTime.SpecifyKind(evt.StartTimeUtc, DateTimeKind.Utc);
        evt.EndTimeUtc = DateTime.SpecifyKind(evt.EndTimeUtc, DateTimeKind.Utc);
        evt.CreatedAtUtc = DateTime.SpecifyKind(evt.CreatedAtUtc, DateTimeKind.Utc);

        _logger.LogInformation("Created event: {Name} (ID: {Id})", evt.Name, evt.Id);
        return evt;
    }

    public async Task<Event> UpdateEventAsync(Event evt, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Events.FindAsync(new object[] { evt.Id }, cancellationToken);
        if (existing == null)
        {
            throw new InvalidOperationException($"Event with ID {evt.Id} not found");
        }

        existing.Name = evt.Name;
        existing.Description = evt.Description;
        existing.StartTimeUtc = evt.StartTimeUtc;
        existing.EndTimeUtc = evt.EndTimeUtc;
        existing.StartTimeLocal = evt.StartTimeLocal;
        existing.EndTimeLocal = evt.EndTimeLocal;
        existing.Color = evt.Color;
        existing.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        existing.StartTimeUtc = DateTime.SpecifyKind(existing.StartTimeUtc, DateTimeKind.Utc);
        existing.EndTimeUtc = DateTime.SpecifyKind(existing.EndTimeUtc, DateTimeKind.Utc);
        existing.CreatedAtUtc = DateTime.SpecifyKind(existing.CreatedAtUtc, DateTimeKind.Utc);
        existing.UpdatedAtUtc = DateTime.SpecifyKind(existing.UpdatedAtUtc!.Value, DateTimeKind.Utc);

        _logger.LogInformation("Updated event: {Name} (ID: {Id})", existing.Name, existing.Id);
        return existing;
    }

    public async Task DeleteEventAsync(int id, CancellationToken cancellationToken = default)
    {
        var evt = await _context.Events.FindAsync(new object[] { id }, cancellationToken);
        if (evt != null)
        {
            _context.Events.Remove(evt);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Deleted event: {Name} (ID: {Id})", evt.Name, evt.Id);
        }
    }

    public async Task<List<Download>> GetDownloadsForEventAsync(int eventId, bool taggedOnly, CancellationToken cancellationToken = default)
    {
        var evt = await _context.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

        if (evt == null)
        {
            return new List<Download>();
        }

        List<Download> downloads;

        if (taggedOnly)
        {
            // Get only explicitly tagged downloads
            downloads = await _context.EventDownloads
                .AsNoTracking()
                .Where(ed => ed.EventId == eventId)
                .Select(ed => ed.Download)
                .OrderByDescending(d => d.StartTimeUtc)
                .ToListAsync(cancellationToken);
        }
        else
        {
            // Get downloads within the event time window
            downloads = await _context.Downloads
                .AsNoTracking()
                .Where(d => d.StartTimeUtc >= evt.StartTimeUtc && d.StartTimeUtc <= evt.EndTimeUtc)
                .OrderByDescending(d => d.StartTimeUtc)
                .ToListAsync(cancellationToken);
        }

        foreach (var download in downloads)
        {
            download.StartTimeUtc = DateTime.SpecifyKind(download.StartTimeUtc, DateTimeKind.Utc);
            if (download.EndTimeUtc != default)
            {
                download.EndTimeUtc = DateTime.SpecifyKind(download.EndTimeUtc, DateTimeKind.Utc);
            }
        }

        return downloads;
    }

    public async Task TagDownloadAsync(int eventId, int downloadId, bool autoTagged, CancellationToken cancellationToken = default)
    {
        // Check if already tagged
        var existing = await _context.EventDownloads
            .FirstOrDefaultAsync(ed => ed.EventId == eventId && ed.DownloadId == downloadId, cancellationToken);

        if (existing != null)
        {
            return; // Already tagged
        }

        var eventDownload = new EventDownload
        {
            EventId = eventId,
            DownloadId = downloadId,
            TaggedAtUtc = DateTime.UtcNow,
            AutoTagged = autoTagged
        };

        _context.EventDownloads.Add(eventDownload);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogDebug("Tagged download {DownloadId} to event {EventId} (auto: {AutoTagged})", downloadId, eventId, autoTagged);
    }

    public async Task UntagDownloadAsync(int eventId, int downloadId, CancellationToken cancellationToken = default)
    {
        var eventDownload = await _context.EventDownloads
            .FirstOrDefaultAsync(ed => ed.EventId == eventId && ed.DownloadId == downloadId, cancellationToken);

        if (eventDownload != null)
        {
            _context.EventDownloads.Remove(eventDownload);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Untagged download {DownloadId} from event {EventId}", downloadId, eventId);
        }
    }

    public async Task<int> AutoTagDownloadsForActiveEventsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var activeEvents = await _context.Events
            .Where(e => e.StartTimeUtc <= now && e.EndTimeUtc >= now)
            .ToListAsync(cancellationToken);

        if (activeEvents.Count == 0)
        {
            return 0;
        }

        int totalTagged = 0;

        foreach (var evt in activeEvents)
        {
            // Get downloads within the event time window that are not yet tagged
            var untaggedDownloads = await _context.Downloads
                .Where(d => d.StartTimeUtc >= evt.StartTimeUtc && d.StartTimeUtc <= evt.EndTimeUtc)
                .Where(d => !_context.EventDownloads.Any(ed => ed.EventId == evt.Id && ed.DownloadId == d.Id))
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            foreach (var downloadId in untaggedDownloads)
            {
                var eventDownload = new EventDownload
                {
                    EventId = evt.Id,
                    DownloadId = downloadId,
                    TaggedAtUtc = DateTime.UtcNow,
                    AutoTagged = true
                };

                _context.EventDownloads.Add(eventDownload);
                totalTagged++;
            }
        }

        if (totalTagged > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Auto-tagged {Count} downloads to active events", totalTagged);
        }

        return totalTagged;
    }
}
