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

/// <summary>
/// Lightweight liveness/progress snapshot for a single tracked operation
/// (<c>GET /api/operations/{id}</c>).
/// </summary>
public class OperationStatusResponse
{
    public Guid Id { get; set; }

    /// <summary>False means the operation is no longer tracked (completed, failed, cancelled, or never existed).</summary>
    public bool Active { get; set; }

    public double PercentComplete { get; set; }

    /// <summary>Progress message for the operation. Null when the operation is not tracked.</summary>
    public string? Message { get; set; }
}

/// <summary>
/// A single row in <c>GET /api/operations/waiting</c> - an operation parked in the wait-queue.
/// </summary>
public class WaitingOperationResponse
{
    public Guid OperationId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the operation currently blocking this one from starting. Null when nothing is recorded as blocking it.</summary>
    public string? BlockedByName { get; set; }
}

/// <summary>
/// Result of <c>POST /api/operations/{id}/cancel</c>.
/// </summary>
public class OperationCancelResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }

    /// <summary>True when the operation had already reached a terminal state before this call arrived.</summary>
    public bool AlreadyFinished { get; set; }
}

/// <summary>
/// Success body for <c>POST /api/operations/{id}/force-kill</c>.
/// </summary>
public class OperationForceKillResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
}
