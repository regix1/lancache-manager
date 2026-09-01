namespace LancacheManager.Models;

// ============================================================
// Common Response Types
// ============================================================
// Domain-specific responses have been split into separate files:
// - AuthResponses.cs      : Authentication and session responses
// - CacheResponses.cs     : Cache operations responses
// - DatabaseResponses.cs  : Database reset and migration responses
// - DepotResponses.cs     : Depot/PICS responses
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
    public Guid? OperationId { get; set; }

    /// <summary>
    /// i18n key for the localized reason, the same field a thrown refusal carries. Set on the
    /// routes that answer with this shape instead of throwing; null everywhere the English
    /// sentence is still the only answer.
    /// </summary>
    public string? StageKey { get; set; }

    /// <summary>Substitution values for the localized <see cref="StageKey"/> template.</summary>
    public Dictionary<string, object?>? Context { get; set; }
}

/// <summary>
/// Response for conflict errors (e.g., operation already running)
/// </summary>
public class ConflictResponse
{
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Generic error response for BadRequest/validation errors.
/// Aligned with the <c>GlobalExceptionMiddleware</c> wire shape { error, details?, statusCode? }
/// (camelCase, nulls omitted). <see cref="Error"/> is the canonical, primary message key.
/// </summary>
public class ErrorResponse
{
    /// <summary>
    /// The value <see cref="Code"/> carries on the one refusal a caller is meant to read as "not
    /// right now" rather than "something is wrong": a client download is writing to the cache, so
    /// a scan would read a directory tree that is still changing. Every other 400 on those same
    /// routes, the configuration denials among them, is a real failure and carries no code. The
    /// sentence cannot be the discriminator, because it is prose and changes when anyone edits it.
    /// </summary>
    public const string DownloadInProgressCode = "downloadInProgress";

    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public int? StatusCode { get; set; }

    /// <summary>
    /// A stable token naming this refusal, for a caller that has to tell one 400 apart from
    /// another. Null wherever the sentence is the whole answer, which is nearly everywhere.
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    /// i18n key for the localized reason, the same field a thrown refusal carries. Set on the
    /// routes that answer with this shape instead of throwing; null everywhere the English
    /// sentence is still the only answer.
    /// </summary>
    public string? StageKey { get; set; }

    /// <summary>Substitution values for the localized <see cref="StageKey"/> template.</summary>
    public Dictionary<string, object?>? Context { get; set; }

    /// <summary>
    /// Legacy secondary message field. <see cref="Error"/> is the canonical error key; this is retained
    /// only for the existing SteamApiKeysController payload that sets both. Do NOT add new usages.
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// A response carrying only a message, with no success flag or additional data. Matches the wire
/// shape of <see cref="ApiResponse.Message(string)"/> exactly, so an action can declare it in
/// <c>ProducesResponseType</c> without adding fields <c>ApiResponse.Message</c> does not emit.
/// </summary>
public class MessageOnlyResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for validation errors with multiple error messages
/// Used by FluentValidation filter for consistent error responses
/// </summary>
public class ValidationErrorResponse
{
    public string Error { get; set; } = "Validation failed";

    /// <summary>
    /// i18n key naming the refusal, so the browser shows it in the reader's language. The English
    /// <see cref="Error"/> stays as the fallback for a build whose locale has no words for the key.
    /// </summary>
    public string? StageKey { get; set; }

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

    /// <summary>
    /// The refusal a cache scan gives while a client download is writing to the cache. Built here
    /// rather than through <see cref="Error"/> so every route that returns this refusal carries the
    /// code, and so the routes that return a configuration failure through the same status and
    /// shape keep reading as failures.
    /// </summary>
    public static ErrorResponse DownloadInProgress(string reason) => new()
    {
        Error = reason,
        Code = ErrorResponse.DownloadInProgressCode
    };

    /// <summary>Creates a not found response for a specific entity type.</summary>
    public static NotFoundResponse NotFound(string entityType) => new()
    {
        Error = $"{entityType} not found"
    };

    /// <summary>Creates a not found response with operation ID.</summary>
    public static NotFoundResponse NotFound(string entityType, Guid operationId) => new()
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

    /// <summary>Creates a message-only response object.</summary>
    public static object Message(string message) => new { message };

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
}
