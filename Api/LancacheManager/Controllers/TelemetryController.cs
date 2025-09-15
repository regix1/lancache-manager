using Microsoft.AspNetCore.Mvc;
using System;

namespace LancacheManager.Controllers
{
    [ApiController]
    [Route("api/system")]
    public class TelemetryController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<TelemetryController> _logger;

        public TelemetryController(IConfiguration configuration, ILogger<TelemetryController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet("telemetry-status")]
        public IActionResult GetTelemetryStatus()
        {
            try
            {
                // Check if telemetry is enabled via environment variable
                // Default to false (opt-in) for privacy
                var telemetryEnabled = _configuration.GetValue<bool>("TELEMETRY_ENABLED", false);

                // Also check for the environment variable directly
                var envTelemetry = Environment.GetEnvironmentVariable("TELEMETRY_ENABLED");
                if (!string.IsNullOrEmpty(envTelemetry))
                {
                    telemetryEnabled = envTelemetry.ToLower() == "true" || envTelemetry == "1";
                }

                var version = _configuration.GetValue<string>("APP_VERSION", "1.0.0");

                return Ok(new
                {
                    enabled = telemetryEnabled,
                    version = version,
                    privacy_policy = "https://github.com/Regix1/lancache-manager/blob/main/PRIVACY.md"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting telemetry status");
                return Ok(new { enabled = false }); // Default to disabled on error
            }
        }

        [HttpPost("telemetry/events")]
        public IActionResult ReceiveTelemetryEvents([FromBody] TelemetryBatch batch)
        {
            try
            {
                // Check if telemetry is enabled
                var telemetryEnabled = _configuration.GetValue<bool>("TELEMETRY_ENABLED", false);
                var envTelemetry = Environment.GetEnvironmentVariable("TELEMETRY_ENABLED");
                if (!string.IsNullOrEmpty(envTelemetry))
                {
                    telemetryEnabled = envTelemetry.ToLower() == "true" || envTelemetry == "1";
                }

                if (!telemetryEnabled)
                {
                    return StatusCode(403, "Telemetry is disabled");
                }

                // Log telemetry events (you could forward these to your analytics service)
                _logger.LogInformation($"Received {batch.Events?.Count ?? 0} telemetry events from user {batch.UserId}");

                // Here you could:
                // 1. Forward to PostHog, Plausible, or other analytics service
                // 2. Store in a database
                // 3. Send to a queue for processing

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing telemetry events");
                return StatusCode(500, "Error processing telemetry");
            }
        }
    }

    public class TelemetryBatch
    {
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public List<TelemetryEvent> Events { get; set; }
    }

    public class TelemetryEvent
    {
        public string Event { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public long Timestamp { get; set; }
    }
}