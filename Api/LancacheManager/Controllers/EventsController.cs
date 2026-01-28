using LancacheManager.Controllers.Base;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for event management
/// Handles CRUD operations for events and download tagging
/// </summary>
[ApiController]
[Route("api/events")]
public class EventsController : CrudControllerBase<Event, Event, CreateEventRequest, UpdateEventRequest, int>
{
    private readonly IEventsService _eventsService;

    protected override string ResourceName => "Event";

    public EventsController(
        IEventsService eventsService,
        ISignalRNotificationService notifications,
        ILogger<EventsController> logger)
        : base(eventsService, notifications, logger)
    {
        _eventsService = eventsService;
    }

    // ===== Abstract Method Implementations =====

    protected override Event ToDto(Event entity) => entity;

    protected override Event FromCreateRequest(CreateEventRequest request)
    {
        var startUtc = request.StartTime.FromUnixSeconds();
        var endUtc = request.EndTime.FromUnixSeconds();

        return new Event
        {
            Name = request.Name,
            Description = request.Description,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            StartTimeLocal = request.StartTimeLocal ?? startUtc,
            EndTimeLocal = request.EndTimeLocal ?? endUtc,
            ColorIndex = Math.Clamp(request.ColorIndex ?? 1, 1, 8)
        };
    }

    protected override void ApplyUpdate(Event entity, UpdateEventRequest request)
    {
        var startUtc = request.StartTime.FromUnixSeconds();
        var endUtc = request.EndTime.FromUnixSeconds();

        entity.Name = request.Name;
        entity.Description = request.Description;
        entity.StartTimeUtc = startUtc;
        entity.EndTimeUtc = endUtc;
        entity.StartTimeLocal = request.StartTimeLocal ?? startUtc;
        entity.EndTimeLocal = request.EndTimeLocal ?? endUtc;
        entity.ColorIndex = Math.Clamp(request.ColorIndex ?? entity.ColorIndex, 1, 8);
    }

    protected override Task ValidateCreateRequestAsync(CreateEventRequest request, CancellationToken ct)
        => Task.CompletedTask;

    protected override Task ValidateUpdateRequestAsync(int id, UpdateEventRequest request, Event existingEntity, CancellationToken ct)
        => Task.CompletedTask;

    // ===== SignalR Notifications =====

    protected override async Task OnCreatedAsync(Event entity, Event dto)
    {
        await Notifications.NotifyAllAsync(SignalREvents.EventCreated, dto);
    }

    protected override async Task OnUpdatedAsync(Event entity, Event dto)
    {
        await Notifications.NotifyAllAsync(SignalREvents.EventUpdated, dto);
    }

    protected override async Task OnDeletedAsync(int id)
    {
        await Notifications.NotifyAllAsync(SignalREvents.EventDeleted, id);
    }

    // ===== CRUD Endpoints =====

    /// <summary>
    /// Get all events
    /// </summary>
    [HttpGet]
    [RequireGuestSession]
    [ResponseCache(Duration = 5)]
    public override Task<IActionResult> GetAll(CancellationToken ct = default)
        => base.GetAll(ct);

    /// <summary>
    /// Get currently active events
    /// </summary>
    [HttpGet("active")]
    [RequireGuestSession]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetActive()
    {
        var events = await _eventsService.GetActiveEventsAsync();
        return Ok(events);
    }

    /// <summary>
    /// Get events for calendar view (by date range)
    /// </summary>
    [HttpGet("calendar")]
    [RequireGuestSession]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetCalendarEvents([FromQuery] long start, [FromQuery] long end)
    {
        var startUtc = start.FromUnixSeconds();
        var endUtc = end.FromUnixSeconds();

        var events = await _eventsService.GetEventsByDateRangeAsync(startUtc, endUtc);
        return Ok(events);
    }

    /// <summary>
    /// Get a single event by ID
    /// </summary>
    [HttpGet("{id:int}")]
    [RequireGuestSession]
    public override Task<IActionResult> GetById(int id, CancellationToken ct = default)
        => base.GetById(id, ct);

    /// <summary>
    /// Create a new event
    /// </summary>
    /// <remarks>
    /// Validation is handled automatically by FluentValidation (see CreateEventRequestValidator)
    /// </remarks>
    [HttpPost]
    [RequireAuth]
    public override Task<IActionResult> Create([FromBody] CreateEventRequest request, CancellationToken ct = default)
        => base.Create(request, ct);

    /// <summary>
    /// Update an existing event
    /// </summary>
    /// <remarks>
    /// Validation is handled automatically by FluentValidation (see UpdateEventRequestValidator)
    /// </remarks>
    [HttpPut("{id:int}")]
    [RequireAuth]
    public override Task<IActionResult> Update(int id, [FromBody] UpdateEventRequest request, CancellationToken ct = default)
        => base.Update(id, request, ct);

    /// <summary>
    /// Delete an event
    /// </summary>
    [HttpDelete("{id:int}")]
    [RequireAuth]
    public override Task<IActionResult> Delete(int id, CancellationToken ct = default)
        => base.Delete(id, ct);

    /// <summary>
    /// Get downloads for an event
    /// </summary>
    [HttpGet("{id:int}/downloads")]
    [RequireGuestSession]
    [ResponseCache(Duration = 5)]
    public async Task<IActionResult> GetDownloads(int id, [FromQuery] bool taggedOnly = false)
    {
        var evt = await _eventsService.GetByIdOrThrowAsync(id, "Event");

        var downloads = await _eventsService.GetDownloadsForEventAsync(id, taggedOnly);
        return Ok(downloads);
    }

    /// <summary>
    /// Manually tag a download to an event
    /// </summary>
    [HttpPost("{eventId:int}/downloads/{downloadId:int}")]
    [RequireAuth]
    public async Task<IActionResult> TagDownload(int eventId, int downloadId)
    {
        var evt = await _eventsService.GetByIdOrThrowAsync(eventId, "Event");

        await _eventsService.TagDownloadAsync(eventId, downloadId, autoTagged: false);

        // Notify clients via SignalR
        await Notifications.NotifyAllAsync(SignalREvents.DownloadTagged, new DownloadTagged(eventId, downloadId));

        return Ok(ApiResponse.Message("Download tagged to event"));
    }

    /// <summary>
    /// Remove a download tag from an event
    /// </summary>
    [HttpDelete("{eventId:int}/downloads/{downloadId:int}")]
    [RequireAuth]
    public async Task<IActionResult> UntagDownload(int eventId, int downloadId)
    {
        await _eventsService.UntagDownloadAsync(eventId, downloadId);
        return NoContent();
    }
}
