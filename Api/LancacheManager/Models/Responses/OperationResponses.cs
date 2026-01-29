namespace LancacheManager.Models;

/// <summary>
/// Response for async operations that return an operation ID for tracking
/// </summary>
public class OperationResponse
{
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";

    public static OperationResponse Started(string operationId, string message) => new()
    {
        OperationId = operationId,
        Message = message,
        Status = "running"
    };
}

/// <summary>
/// Response for operation status checks
/// </summary>
public class OperationStatusResponse
{
    public bool IsProcessing { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public int? PercentComplete { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Response for save state operation
/// </summary>
public class SaveStateResponse
{
    public bool Success { get; set; }
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// Response for operation state update
/// </summary>
public class StateUpdateResponse
{
    public bool Success { get; set; }
}

/// <summary>
/// Response for cleanup operation
/// </summary>
public class StateCleanupResponse
{
    public bool Success { get; set; }
    public int ActiveStates { get; set; }
}

/// <summary>
/// Service unavailable response with retry info
/// </summary>
public class ServiceUnavailableResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int RetryAfter { get; set; }
}
