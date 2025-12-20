using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for event management
/// Handles CRUD operations for events and download tagging
/// </summary>
[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly IEventsRepository _eventsRepository;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<EventsController> _logger;

    public EventsController(
        IEventsRepository eventsRepository,
        IHubContext<DownloadHub> hubContext,
        ILogger<EventsController> logger)
    {
        _eventsRepository = eventsRepository;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Get all events
    /// </summary>
    [HttpGet]
    [RequireAuth]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetAll()
    {
        try
        {
            var events = await _eventsRepository.GetAllEventsAsync();
            return Ok(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all events");
            return Ok(new List<Event>());
        }
    }

    /// <summary>
    /// Get currently active events
    /// </summary>
    [HttpGet("active")]
    [RequireAuth]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetActive()
    {
        try
        {
            var events = await _eventsRepository.GetActiveEventsAsync();
            return Ok(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting active events");
            return Ok(new List<Event>());
        }
    }

    /// <summary>
    /// Get events for calendar view (by date range)
    /// </summary>
    [HttpGet("calendar")]
    [RequireAuth]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetCalendarEvents([FromQuery] long start, [FromQuery] long end)
    {
        try
        {
            var startUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
            var endUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;

            var events = await _eventsRepository.GetEventsByDateRangeAsync(startUtc, endUtc);
            return Ok(events);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting calendar events");
            return Ok(new List<Event>());
        }
    }

    /// <summary>
    /// Get a single event by ID
    /// </summary>
    [HttpGet("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> GetById(int id)
    {
        try
        {
            var evt = await _eventsRepository.GetEventByIdAsync(id);
            if (evt == null)
            {
                return NotFound(new { error = "Event not found" });
            }
            return Ok(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting event {Id}", id);
            return StatusCode(500, new { error = "Failed to get event" });
        }
    }

    /// <summary>
    /// Create a new event
    /// </summary>
    [HttpPost]
    [RequireAuth]
    public async Task<IActionResult> Create([FromBody] CreateEventRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { error = "Event name is required" });
            }

            var startUtc = DateTimeOffset.FromUnixTimeSeconds(request.StartTime).UtcDateTime;
            var endUtc = DateTimeOffset.FromUnixTimeSeconds(request.EndTime).UtcDateTime;

            if (endUtc <= startUtc)
            {
                return BadRequest(new { error = "End time must be after start time" });
            }

            var evt = new Event
            {
                Name = request.Name,
                Description = request.Description,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                StartTimeLocal = request.StartTimeLocal ?? startUtc,
                EndTimeLocal = request.EndTimeLocal ?? endUtc,
                Color = request.Color ?? string.Empty // Color comes from frontend theme variables
            };

            var created = await _eventsRepository.CreateEventAsync(evt);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("EventCreated", created);

            return Created($"/api/events/{created.Id}", created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating event");
            return StatusCode(500, new { error = "Failed to create event" });
        }
    }

    /// <summary>
    /// Update an existing event
    /// </summary>
    [HttpPut("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateEventRequest request)
    {
        try
        {
            var existing = await _eventsRepository.GetEventByIdAsync(id);
            if (existing == null)
            {
                return NotFound(new { error = "Event not found" });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { error = "Event name is required" });
            }

            var startUtc = DateTimeOffset.FromUnixTimeSeconds(request.StartTime).UtcDateTime;
            var endUtc = DateTimeOffset.FromUnixTimeSeconds(request.EndTime).UtcDateTime;

            if (endUtc <= startUtc)
            {
                return BadRequest(new { error = "End time must be after start time" });
            }

            existing.Name = request.Name;
            existing.Description = request.Description;
            existing.StartTimeUtc = startUtc;
            existing.EndTimeUtc = endUtc;
            existing.StartTimeLocal = request.StartTimeLocal ?? startUtc;
            existing.EndTimeLocal = request.EndTimeLocal ?? endUtc;
            existing.Color = request.Color ?? existing.Color;

            var updated = await _eventsRepository.UpdateEventAsync(existing);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("EventUpdated", updated);

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating event {Id}", id);
            return StatusCode(500, new { error = "Failed to update event" });
        }
    }

    /// <summary>
    /// Delete an event
    /// </summary>
    [HttpDelete("{id:int}")]
    [RequireAuth]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var existing = await _eventsRepository.GetEventByIdAsync(id);
            if (existing == null)
            {
                return NotFound(new { error = "Event not found" });
            }

            await _eventsRepository.DeleteEventAsync(id);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("EventDeleted", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting event {Id}", id);
            return StatusCode(500, new { error = "Failed to delete event" });
        }
    }

    /// <summary>
    /// Get downloads for an event
    /// </summary>
    [HttpGet("{id:int}/downloads")]
    [RequireAuth]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetDownloads(int id, [FromQuery] bool taggedOnly = false)
    {
        try
        {
            var evt = await _eventsRepository.GetEventByIdAsync(id);
            if (evt == null)
            {
                return NotFound(new { error = "Event not found" });
            }

            var downloads = await _eventsRepository.GetDownloadsForEventAsync(id, taggedOnly);
            return Ok(downloads);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting downloads for event {Id}", id);
            return Ok(new List<Download>());
        }
    }

    /// <summary>
    /// Manually tag a download to an event
    /// </summary>
    [HttpPost("{eventId:int}/downloads/{downloadId:int}")]
    [RequireAuth]
    public async Task<IActionResult> TagDownload(int eventId, int downloadId)
    {
        try
        {
            var evt = await _eventsRepository.GetEventByIdAsync(eventId);
            if (evt == null)
            {
                return NotFound(new { error = "Event not found" });
            }

            await _eventsRepository.TagDownloadAsync(eventId, downloadId, autoTagged: false);

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("DownloadTagged", new { eventId, downloadId });

            return Ok(new { message = "Download tagged to event" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tagging download {DownloadId} to event {EventId}", downloadId, eventId);
            return StatusCode(500, new { error = "Failed to tag download" });
        }
    }

    /// <summary>
    /// Remove a download tag from an event
    /// </summary>
    [HttpDelete("{eventId:int}/downloads/{downloadId:int}")]
    [RequireAuth]
    public async Task<IActionResult> UntagDownload(int eventId, int downloadId)
    {
        try
        {
            await _eventsRepository.UntagDownloadAsync(eventId, downloadId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error untagging download {DownloadId} from event {EventId}", downloadId, eventId);
            return StatusCode(500, new { error = "Failed to untag download" });
        }
    }
}

/// <summary>
/// Request model for creating an event
/// </summary>
public class CreateEventRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long StartTime { get; set; } // Unix timestamp
    public long EndTime { get; set; } // Unix timestamp
    public DateTime? StartTimeLocal { get; set; }
    public DateTime? EndTimeLocal { get; set; }
    public string? Color { get; set; }
}

/// <summary>
/// Request model for updating an event
/// </summary>
public class UpdateEventRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long StartTime { get; set; } // Unix timestamp
    public long EndTime { get; set; } // Unix timestamp
    public DateTime? StartTimeLocal { get; set; }
    public DateTime? EndTimeLocal { get; set; }
    public string? Color { get; set; }
}
