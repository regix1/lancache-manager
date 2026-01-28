using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using OperationState = LancacheManager.Core.Services.OperationState;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for operation state management
/// Handles temporary operation state storage for background tasks
/// </summary>
[ApiController]
[Route("api/operation-state")]
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
    [RequireAuth]
    public IActionResult GetState(string key)
    {
        var state = _stateService.GetState(key);
        // Return 200 with null instead of 404 to avoid browser console errors
        return Ok(state);
    }

    [HttpPost]
    [RequireAuth]
    public IActionResult SaveState([FromBody] SaveStateRequest request)
    {
        if (string.IsNullOrEmpty(request.Key))
        {
            return BadRequest(new ErrorResponse { Error = "Key is required" });
        }

        var state = new OperationState
        {
            Key = request.Key,
            Type = request.Type ?? string.Empty,
            Data = request.Data,
            Status = request.Status,
            Message = request.Message,
            ExpiresAt = DateTime.UtcNow.AddMinutes(request.ExpirationMinutes ?? 30)
        };

        _stateService.SaveState(request.Key, state);

        return Ok(new SaveStateResponse { Success = true, Key = request.Key });
    }

    [HttpPatch("{key}")]
    [RequireAuth]
    public IActionResult UpdateState(string key, [FromBody] UpdateStateRequest request)
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
                Data = JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
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

        return Ok(new StateUpdateResponse { Success = true });
    }

    [HttpDelete("{key}")]
    [RequireAuth]
    public IActionResult RemoveState(string key)
    {
        _stateService.RemoveState(key);
        return Ok(new StateUpdateResponse { Success = true });
    }

    [HttpGet]
    [RequireAuth]
    public IActionResult GetAllStates([FromQuery] string? type = null)
    {
        var states = string.IsNullOrEmpty(type)
            ? _stateService.GetAllStates()
            : _stateService.GetStatesByType(type);

        return Ok(states);
    }

    [HttpPost("cleanup")]
    [RequireAuth]
    public IActionResult CleanupExpired()
    {
        // Trigger manual cleanup if needed
        var allStates = _stateService.GetAllStates();
        return Ok(new StateCleanupResponse
        {
            Success = true,
            ActiveStates = allStates.Count
        });
    }
}
