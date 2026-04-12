using LancacheManager.Models;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Controllers.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for garbage collection management
/// Handles GC settings, manual triggering, and memory optimization
/// </summary>
[ApiController]
[Route("api/gc")]
[RequiresGcManagement]
[Authorize(Policy = "AdminOnly")]
public class GcController : ControllerBase
{
    private readonly SettingsService _gcSettingsService;
    private readonly IMemoryManager _memoryManager;
    private readonly ILogger<GcController> _logger;
    private readonly IServiceScheduleRegistry _scheduleRegistry;
    private static DateTime _lastGcTriggerTime = DateTime.MinValue;
    private static readonly object _gcTriggerLock = new object();

    public GcController(
        SettingsService gcSettingsService,
        IMemoryManager memoryManager,
        ILogger<GcController> logger,
        IServiceScheduleRegistry scheduleRegistry)
    {
        _gcSettingsService = gcSettingsService;
        _memoryManager = memoryManager;
        _logger = logger;
        _scheduleRegistry = scheduleRegistry;
    }

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var settings = _gcSettingsService.GetSettings();
        return Ok(new GcSettingsResponse
        {
            Enabled = settings.Enabled,
            MemoryThresholdMB = settings.MemoryThresholdMB
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettingsAsync([FromBody] UpdateGcSettingsRequest request)
    {
        if (request.MemoryThresholdMB < 512 || request.MemoryThresholdMB > 32768)
        {
            return BadRequest(new ErrorResponse { Error = "Memory threshold must be between 512MB and 32GB" });
        }

        // Resolve the Enabled flag. Prefer the new field; fall back to the legacy
        // Aggressiveness string for one release of backward compat with older frontends.
        bool enabled;
        if (request.Enabled.HasValue)
        {
            enabled = request.Enabled.Value;
        }
        else if (!string.IsNullOrEmpty(request.Aggressiveness))
        {
#pragma warning disable CS0618 // Type or member is obsolete — legacy DTO compat path
            if (!Enum.TryParse<GcAggressiveness>(request.Aggressiveness, true, out var aggressiveness))
            {
                return BadRequest(new ErrorResponse { Error = "Invalid aggressiveness level" });
            }
            enabled = aggressiveness != GcAggressiveness.Disabled;
#pragma warning restore CS0618
        }
        else
        {
            enabled = false;
        }

        var newSettings = new GcSettings
        {
            Enabled = enabled,
            MemoryThresholdMB = request.MemoryThresholdMB
        };

        var updatedSettings = await _gcSettingsService.UpdateSettingsAsync(newSettings);

        // Broadcast so the Schedules page shows/hides the performanceOptimization card
        // as soon as the Enabled flag flips. Interval changes flow through the standard
        // ServiceScheduleRegistry.SetInterval path, not from here.
        _scheduleRegistry.NotifySchedulesChanged();

        return Ok(new GcSettingsResponse
        {
            Enabled = updatedSettings.Enabled,
            MemoryThresholdMB = updatedSettings.MemoryThresholdMB,
            Message = "GC settings updated successfully"
        });
    }

    [HttpPost("trigger")]
    public IActionResult TriggerGarbageCollection()
    {
        var now = DateTime.UtcNow;
        var cooldownPeriod = TimeSpan.FromSeconds(5);

        // Check cooldown
        lock (_gcTriggerLock)
        {
            var timeSinceLastGc = now - _lastGcTriggerTime;
            if (timeSinceLastGc < cooldownPeriod)
            {
                var remainingSeconds = (cooldownPeriod - timeSinceLastGc).TotalSeconds;
                return Ok(new GcTriggerResponse
                {
                    Skipped = true,
                    Reason = "cooldown",
                    RemainingSeconds = Math.Round(remainingSeconds, 1),
                    Message = $"GC cooldown active. Please wait {Math.Ceiling(remainingSeconds)}s"
                });
            }

            var process = System.Diagnostics.Process.GetCurrentProcess();
            var beforeMB = process.WorkingSet64 / (1024.0 * 1024.0);

            // Use platform-specific memory manager for garbage collection
            // On Linux, this includes malloc_trim to force glibc to return memory to OS
            // On Windows, standard GC is sufficient
            _memoryManager.PerformAggressiveGarbageCollection(_logger);

            _lastGcTriggerTime = DateTime.UtcNow;

            process.Refresh();
            var afterMB = process.WorkingSet64 / (1024.0 * 1024.0);
            var freedMB = beforeMB - afterMB;

            _logger.LogInformation("Manual GC triggered at {BeforeMB:F0}MB, after GC: {AfterMB:F0}MB (freed {FreedMB:F0}MB)",
                beforeMB, afterMB, freedMB);

            return Ok(new GcTriggerResponse
            {
                Skipped = false,
                BeforeMB = Math.Round(beforeMB, 0),
                AfterMB = Math.Round(afterMB, 0),
                FreedMB = Math.Round(freedMB, 0),
                Message = "Garbage collection completed"
            });
        }
    }
}
