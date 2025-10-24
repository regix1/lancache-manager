using Microsoft.AspNetCore.Mvc;
using LancacheManager.Services;
using LancacheManager.Security;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GcController : ControllerBase
{
    private readonly GcSettingsService _gcSettingsService;
    private readonly ILogger<GcController> _logger;
    private static DateTime _lastGcTriggerTime = DateTime.MinValue;
    private static readonly object _gcTriggerLock = new object();

    public GcController(GcSettingsService gcSettingsService, ILogger<GcController> logger)
    {
        _gcSettingsService = gcSettingsService;
        _logger = logger;
    }

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
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

    [HttpPost("settings")]
    [RequireAuth]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateGcSettingsRequest request)
    {
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
        try
        {
            var settings = _gcSettingsService.GetSettings();

            // Only trigger if OnPageLoad mode is enabled
            if (settings.Aggressiveness != GcAggressiveness.OnPageLoad)
            {
                return BadRequest(new { error = "GC trigger endpoint only works in OnPageLoad mode" });
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

                // Use proper pattern for releasing unmanaged SQLite memory
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                _lastGcTriggerTime = DateTime.UtcNow;

                process.Refresh();
                var afterMB = process.WorkingSet64 / (1024.0 * 1024.0);
                var freedMB = beforeMB - afterMB;

                _logger.LogInformation("GC triggered by API at {BeforeMB:F0}MB, after GC: {AfterMB:F0}MB (freed {FreedMB:F0}MB)",
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
