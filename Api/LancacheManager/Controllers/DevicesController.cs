using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for device registration.
/// Auth stripped — POST always returns success (setup wizard step 1 calls this).
/// </summary>
[ApiController]
[Route("api/devices")]
public class DevicesController : ControllerBase
{
    private readonly ILogger<DevicesController> _logger;

    public DevicesController(ILogger<DevicesController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// GET /api/devices - Returns empty list (no device tracking).
    /// </summary>
    [HttpGet]
    public IActionResult GetAllDevices()
    {
        return Ok(new DeviceListResponse
        {
            Devices = new List<object>(),
            Count = 0
        });
    }

    /// <summary>
    /// POST /api/devices - Always returns success (setup wizard step 1).
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("auth")]
    public IActionResult RegisterDevice([FromBody] RegisterDeviceRequest request)
    {
        _logger.LogInformation("Device registration (no-op): {DeviceId}", request.DeviceId);

        return Created($"/api/devices/{request.DeviceId}", new
        {
            success = true,
            message = "Device registered successfully"
        });
    }
}
