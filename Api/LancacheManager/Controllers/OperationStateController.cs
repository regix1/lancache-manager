using LancacheManager.Services;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OperationStateController : ControllerBase
{
    private readonly OperationStateService _stateService;
    private readonly ILogger<OperationStateController> _logger;

    public OperationStateController(
        OperationStateService stateService,
        ILogger<OperationStateController> logger)
    {
        _stateService = stateService;
        _logger = logger;
    }

    [HttpGet("{key}")]
    public IActionResult GetState(string key)
    {
        try
        {
            var state = _stateService.GetState(key);
            if (state == null)
            {
                return NotFound();
            }
            
            return Ok(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting state for key: {key}");
            return StatusCode(500, new { error = "Failed to get state" });
        }
    }

    [HttpPost]
    public IActionResult SaveState([FromBody] SaveStateRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Key))
            {
                return BadRequest(new { error = "Key is required" });
            }

            var state = new OperationState
            {
                Key = request.Key,
                Type = request.Type ?? string.Empty,
                Data = request.Data ?? new Dictionary<string, object>(),
                Status = request.Status,
                Message = request.Message,
                ExpiresAt = DateTime.UtcNow.AddMinutes(request.ExpirationMinutes ?? 30)
            };

            _stateService.SaveState(request.Key, state);
            
            return Ok(new { success = true, key = request.Key });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving state");
            return StatusCode(500, new { error = "Failed to save state" });
        }
    }

    // FIX: Use explicit route to avoid routing issues
    [HttpPatch("{key}")]
    [Route("{key}")]
    public IActionResult UpdateState(string key, [FromBody] UpdateStateRequest request)
    {
        try
        {
            var state = _stateService.GetState(key);
            if (state == null)
            {
                _logger.LogWarning($"State not found for key: {key}");
                // Instead of 404, create a new state if it doesn't exist
                state = new OperationState
                {
                    Key = key,
                    Type = "unknown",
                    Data = new Dictionary<string, object>(),
                    Status = request.Status,
                    Message = request.Message,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(30)
                };
                _stateService.SaveState(key, state);
            }

            if (request.Updates != null && request.Updates.Count > 0)
            {
                _stateService.UpdateState(key, request.Updates);
            }

            if (request.Status != null)
            {
                state.Status = request.Status;
                _stateService.SaveState(key, state);
            }

            if (request.Message != null)
            {
                state.Message = request.Message;
                _stateService.SaveState(key, state);
            }

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error updating state for key: {key}");
            return StatusCode(500, new { error = "Failed to update state", message = ex.Message });
        }
    }

    [HttpDelete("{key}")]
    public IActionResult RemoveState(string key)
    {
        try
        {
            _stateService.RemoveState(key);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error removing state for key: {key}");
            return StatusCode(500, new { error = "Failed to remove state" });
        }
    }

    [HttpGet]
    public IActionResult GetAllStates([FromQuery] string? type = null)
    {
        try
        {
            var states = string.IsNullOrEmpty(type) 
                ? _stateService.GetAllStates()
                : _stateService.GetStatesByType(type);
                
            return Ok(states);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting states");
            return StatusCode(500, new { error = "Failed to get states" });
        }
    }

    [HttpPost("cleanup")]
    public IActionResult CleanupExpired()
    {
        try
        {
            // Trigger manual cleanup if needed
            var allStates = _stateService.GetAllStates();
            return Ok(new { 
                success = true, 
                activeStates = allStates.Count 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up states");
            return StatusCode(500, new { error = "Failed to cleanup states" });
        }
    }
}

public class SaveStateRequest
{
    public string Key { get; set; } = string.Empty;
    public string? Type { get; set; }
    public Dictionary<string, object>? Data { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public int? ExpirationMinutes { get; set; }
}

public class UpdateStateRequest
{
    public Dictionary<string, object>? Updates { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}