namespace LancacheManager.Models;

/// <summary>
/// Response for async operations that return an operation ID for tracking
/// </summary>
public class OperationResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Running;

    public static OperationResponse Started(Guid operationId, string message) => new()
    {
        OperationId = operationId,
        Message = message,
        Status = OperationStatus.Running
    };
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
