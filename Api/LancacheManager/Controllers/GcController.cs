using LancacheManager.Application.DTOs;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Services.Interfaces;
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
    private readonly SettingsRepository _gcSettingsService;
    private readonly IMemoryManager _memoryManager;
    private readonly ILogger<GcController> _logger;
    private readonly IConfiguration _configuration;
    private static DateTime _lastGcTriggerTime = DateTime.MinValue;
    private static readonly object _gcTriggerLock = new object();

    public GcController(SettingsRepository gcSettingsService, IMemoryManager memoryManager, ILogger<GcController> logger, IConfiguration configuration)
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
    public IActionResult GetSettings()
    {
        if (!IsGcManagementEnabled())
        {
            return NotFound(new { error = "Garbage collection management is disabled" });
        }

        try
        {
            var settings = _gcSettingsService.GetSettings();
            return Ok(new
            {
                aggressiveness = settings.Aggressiveness.ToString().ToLower(),
                memoryThresholdMB = settings.MemoryThresholdMB
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting GC settings");
            return StatusCode(500, new { error = "Failed to get GC settings" });
        }
    }

    [HttpPut("settings")]
    [RequireAuth]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateGcSettingsRequest request)
    {
        if (!IsGcManagementEnabled())
        {
            return NotFound(new { error = "Garbage collection management is disabled" });
        }

        try
        {
            if (!Enum.TryParse<GcAggressiveness>(request.Aggressiveness, true, out var aggressiveness))
            {
                return BadRequest(new { error = "Invalid aggressiveness level" });
            }

            if (request.MemoryThresholdMB < 512 || request.MemoryThresholdMB > 32768)
            {
                return BadRequest(new { error = "Memory threshold must be between 512MB and 32GB" });
            }

            var newSettings = new GcSettings
            {
                Aggressiveness = aggressiveness,
                MemoryThresholdMB = request.MemoryThresholdMB
            };

            var updatedSettings = await _gcSettingsService.UpdateSettingsAsync(newSettings);

            return Ok(new
            {
                aggressiveness = updatedSettings.Aggressiveness.ToString().ToLower(),
                memoryThresholdMB = updatedSettings.MemoryThresholdMB,
                message = "GC settings updated successfully"
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating GC settings");
            return StatusCode(500, new { error = "Failed to update GC settings" });
        }
    }

    [HttpPost("trigger")]
    [RequireAuth]
    public IActionResult TriggerGarbageCollection()
    {
        if (!IsGcManagementEnabled())
        {
            return NotFound(new { error = "Garbage collection management is disabled" });
        }

        try
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
                    return Ok(new
                    {
                        skipped = true,
                        reason = "cooldown",
                        remainingSeconds = Math.Round(remainingSeconds, 1),
                        message = $"GC cooldown active. Please wait {Math.Ceiling(remainingSeconds)}s"
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

                return Ok(new
                {
                    skipped = false,
                    beforeMB = Math.Round(beforeMB, 0),
                    afterMB = Math.Round(afterMB, 0),
                    freedMB = Math.Round(freedMB, 0),
                    message = "Garbage collection completed"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error triggering garbage collection");
            return StatusCode(500, new { error = "Failed to trigger garbage collection" });
        }
    }
}

public class UpdateGcSettingsRequest
{
    public string Aggressiveness { get; set; } = "disabled";
    public long MemoryThresholdMB { get; set; } = 4096;
}
