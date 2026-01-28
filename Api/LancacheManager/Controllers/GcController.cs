using LancacheManager.Models;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for garbage collection management
/// Handles GC settings, manual triggering, and memory optimization
/// </summary>
[ApiController]
[Route("api/gc")]
public class GcController : ControllerBase
{
    private readonly SettingsService _gcSettingsService;
    private readonly IMemoryManager _memoryManager;
    private readonly ILogger<GcController> _logger;
    private readonly IConfiguration _configuration;
    private static DateTime _lastGcTriggerTime = DateTime.MinValue;
    private static readonly object _gcTriggerLock = new object();

    public GcController(SettingsService gcSettingsService, IMemoryManager memoryManager, ILogger<GcController> logger, IConfiguration configuration)
    {
        _gcSettingsService = gcSettingsService;
        _memoryManager = memoryManager;
        _logger = logger;
        _configuration = configuration;
    }

    private bool IsGcManagementEnabled()
    {
        return _configuration.GetValue<bool>("Optimizations:EnableGarbageCollectionManagement", false);
    }

    [HttpGet("settings")]
    [RequireGuestSession]
    public IActionResult GetSettings()
    {
        if (!IsGcManagementEnabled())
        {
            return NotFound(new ErrorResponse { Error = "Garbage collection management is disabled" });
        }

        var settings = _gcSettingsService.GetSettings();
        return Ok(new GcSettingsResponse
        {
            Aggressiveness = settings.Aggressiveness.ToString().ToLower(),
            MemoryThresholdMB = settings.MemoryThresholdMB
        });
    }

    [HttpPut("settings")]
    [RequireAuth]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateGcSettingsRequest request)
    {
        if (!IsGcManagementEnabled())
        {
            return NotFound(new ErrorResponse { Error = "Garbage collection management is disabled" });
        }

        if (!Enum.TryParse<GcAggressiveness>(request.Aggressiveness, true, out var aggressiveness))
        {
            return BadRequest(new ErrorResponse { Error = "Invalid aggressiveness level" });
        }

        if (request.MemoryThresholdMB < 512 || request.MemoryThresholdMB > 32768)
        {
            return BadRequest(new ErrorResponse { Error = "Memory threshold must be between 512MB and 32GB" });
        }

        var newSettings = new GcSettings
        {
            Aggressiveness = aggressiveness,
            MemoryThresholdMB = request.MemoryThresholdMB
        };

        var updatedSettings = await _gcSettingsService.UpdateSettingsAsync(newSettings);

        return Ok(new GcSettingsResponse
        {
            Aggressiveness = updatedSettings.Aggressiveness.ToString().ToLower(),
            MemoryThresholdMB = updatedSettings.MemoryThresholdMB,
            Message = "GC settings updated successfully"
        });
    }

    [HttpPost("trigger")]
    [RequireAuth]
    public IActionResult TriggerGarbageCollection()
    {
        if (!IsGcManagementEnabled())
        {
            return NotFound(new ErrorResponse { Error = "Garbage collection management is disabled" });
        }

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
