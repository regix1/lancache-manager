using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public MetricsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Get metrics endpoint security status
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var requiresAuth = _configuration.GetValue<bool>("Security:RequireAuthForMetrics", false);

        return Ok(new
        {
            requiresAuthentication = requiresAuth,
            endpoint = "/metrics",
            authMethod = requiresAuth ? "X-Api-Key header required" : "Public access"
        });
    }
}
