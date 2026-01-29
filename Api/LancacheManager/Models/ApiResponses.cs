namespace LancacheManager.Models;

// ============================================================
// Common Response Types
// ============================================================
// Domain-specific responses have been split into separate files:
// - AuthResponses.cs      : Authentication and session responses
// - CacheResponses.cs     : Cache operations responses
// - DatabaseResponses.cs  : Database reset and migration responses
// - DepotResponses.cs     : Depot/PICS responses
// - FileBrowserResponses.cs : File browser responses
// - GameResponses.cs      : Game detection/removal responses
// - LogResponses.cs       : Log controller responses
// - OperationResponses.cs : Generic operation responses
// - PrefillResponses.cs   : Prefill daemon responses
// - StatsResponses.cs     : Dashboard and analytics responses
// - SteamResponses.cs     : Steam authentication and API responses
// - SystemResponses.cs    : System config and state responses
// - ThemeResponses.cs     : Theme controller responses
// ============================================================

/// <summary>
/// Simple message response for operations that just return a message
/// </summary>
public class MessageResponse
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;

    public static MessageResponse Ok(string message) => new() { Success = true, Message = message };
}

/// <summary>
/// Response for not found errors
/// </summary>
public class NotFoundResponse
{
    public string Error { get; set; } = string.Empty;
    public string? OperationId { get; set; }
}

/// <summary>
/// Response for conflict errors (e.g., operation already running)
/// </summary>
public class ConflictResponse
{
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Generic error response for BadRequest/validation errors
/// </summary>
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Response for validation errors with multiple error messages
/// Used by FluentValidation filter for consistent error responses
/// </summary>
public class ValidationErrorResponse
{
    public string Error { get; set; } = "Validation failed";
    public List<ValidationFieldError> Errors { get; set; } = new();
}

/// <summary>
/// Individual field validation error
/// </summary>
public class ValidationFieldError
{
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Static factory methods for common API responses.
/// Reduces inline anonymous object creation across controllers.
/// </summary>
public static class ApiResponse
{
    // ==================== Error Responses ====================

    /// <summary>Creates a standard error response object.</summary>
    public static ErrorResponse Error(string error, string? details = null) => new()
    {
        Error = error,
        Details = details
    };

    /// <summary>Creates a not found response for a specific entity type.</summary>
    public static NotFoundResponse NotFound(string entityType) => new()
    {
        Error = $"{entityType} not found"
    };

    /// <summary>Creates a not found response with operation ID.</summary>
    public static NotFoundResponse NotFound(string entityType, string operationId) => new()
    {
        Error = $"{entityType} not found",
        OperationId = operationId
    };

    /// <summary>Creates a conflict response (e.g., operation already running).</summary>
    public static ConflictResponse Conflict(string error) => new()
    {
        Error = error
    };

    // ==================== Success Responses ====================

    /// <summary>Creates a simple success message response.</summary>
    public static MessageResponse Success(string message) => new()
    {
        Success = true,
        Message = message
    };

    /// <summary>Creates a message-only response object.</summary>
    public static object Message(string message) => new { message };

    /// <summary>Creates a success response with custom data.</summary>
    public static object Ok(string message) => new { message };

    // ==================== Validation Responses ====================

    /// <summary>Creates an error response for missing required fields.</summary>
    public static ErrorResponse Required(string fieldName) => new()
    {
        Error = $"{fieldName} is required"
    };

    /// <summary>Creates an error response for invalid values.</summary>
    public static ErrorResponse Invalid(string message) => new()
    {
        Error = message
    };

    /// <summary>Creates an error response for duplicate entries.</summary>
    public static ErrorResponse Duplicate(string entityType, string fieldName) => new()
    {
        Error = $"A {entityType.ToLower()} with this {fieldName.ToLower()} already exists"
    };

    // ==================== Internal Error Responses ====================

    /// <summary>Creates an internal server error response.</summary>
    public static ErrorResponse InternalError(string operation) => new()
    {
        Error = $"An error occurred while {operation}. Check server logs for details."
    };

    /// <summary>Creates an internal server error response with details.</summary>
    public static ErrorResponse InternalError(string operation, string details) => new()
    {
        Error = $"An error occurred while {operation}",
        Details = details
    };
}
