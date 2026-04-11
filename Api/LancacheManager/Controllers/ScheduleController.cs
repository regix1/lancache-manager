using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/system/schedules")]
[Authorize]
public class ScheduleController : ControllerBase
{
    private readonly IServiceScheduleRegistry _registry;
    private readonly IHubContext<DownloadHub> _hubContext;

    public ScheduleController(IServiceScheduleRegistry registry, IHubContext<DownloadHub> hubContext)
    {
        _registry = registry;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Returns all registered service schedules.
    /// </summary>
    [HttpGet("")]
    public ActionResult<IReadOnlyList<ServiceScheduleInfo>> GetAll()
    {
        return Ok(_registry.GetAll());
    }

    /// <summary>
    /// Returns a single service schedule by its key.
    /// </summary>
    [HttpGet("{serviceKey}")]
    public ActionResult<ServiceScheduleInfo> GetByKey(string serviceKey)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound();
        }
        return Ok(info);
    }

    /// <summary>
    /// Updates the interval for a service schedule.
    /// </summary>
    [HttpPut("{serviceKey}")]
    public async Task<ActionResult> SetIntervalAsync(string serviceKey, [FromBody] UpdateScheduleIntervalRequest request)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound();
        }

        _registry.SetInterval(serviceKey, request.IntervalHours);
        await _hubContext.Clients.All.SendAsync("SchedulesUpdated", _registry.GetAll());
        return NoContent();
    }

    /// <summary>
    /// Updates whether the service runs at app startup.
    /// </summary>
    [HttpPut("{serviceKey}/runOnStartup")]
    public async Task<ActionResult> SetRunOnStartupAsync(string serviceKey, [FromBody] UpdateScheduleRunOnStartupRequest request)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound();
        }

        _registry.SetRunOnStartup(serviceKey, request.RunOnStartup);
        await _hubContext.Clients.All.SendAsync("SchedulesUpdated", _registry.GetAll());
        return NoContent();
    }

    /// <summary>
    /// Triggers an immediate run of the service, bypassing the scheduled interval.
    /// </summary>
    [HttpPost("{serviceKey}/run")]
    public async Task<ActionResult> TriggerRunAsync(string serviceKey)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound();
        }

        await _registry.TriggerRunAsync(serviceKey);
        await _hubContext.Clients.All.SendAsync("SchedulesUpdated", _registry.GetAll());
        return Accepted();
    }

    /// <summary>
    /// Resets all service schedules to their hardcoded defaults.
    /// </summary>
    [HttpPost("reset")]
    public async Task<ActionResult> ResetToDefaultsAsync()
    {
        _registry.ResetToDefaults();
        await _hubContext.Clients.All.SendAsync("SchedulesUpdated", _registry.GetAll());
        return Ok();
    }
}

public class UpdateScheduleIntervalRequest
{
    public double IntervalHours { get; set; }
}

public class UpdateScheduleRunOnStartupRequest
{
    public bool RunOnStartup { get; set; }
}
