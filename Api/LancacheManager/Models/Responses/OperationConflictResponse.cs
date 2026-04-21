namespace LancacheManager.Models;

/// <summary>
/// Canonical 409 Conflict response body for cache-state conflicts detected by
/// <c>IOperationConflictChecker</c>. Replaces the ad-hoc <c>ConflictResponse</c> /
/// <c>ErrorResponse</c> / anonymous shapes that previously varied across controllers.
///
/// Field order mirrors the target contract in <c>plan/plan.md</c>.
/// Serialized with the global camelCase policy (see <c>Program.cs</c>).
/// </summary>
public sealed class OperationConflictResponse
{
    /// <summary>Machine-readable, stable constant. Always <c>"OPERATION_CONFLICT"</c>.</summary>
    public string Code { get; init; } = "OPERATION_CONFLICT";

    /// <summary>
    /// i18n key for the localized reason (e.g. <c>"errors.conflict.duplicate"</c>,
    /// <c>"errors.conflict.overlappingEntity"</c>).
    /// </summary>
    public string StageKey { get; init; } = string.Empty;

    /// <summary>English fallback message for legacy clients that do not consume <see cref="StageKey"/>.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>Id of the active blocking operation (null for scan-style "already running" globals).</summary>
    public Guid? ActiveOperationId { get; init; }

    /// <summary>Enum name of the blocking operation (e.g. <c>"GameRemoval"</c>).</summary>
    public string? ActiveOperationType { get; init; }

    /// <summary>
    /// Canonical <c>kind:key</c> representation of the blocking operation's scope
    /// (e.g. <c>"steam:480"</c>, <c>"service:steam"</c>, <c>"bulk"</c>).
    /// </summary>
    public string? ActiveOperationScope { get; init; }

    /// <summary>Substitution values for the localized <see cref="StageKey"/> template (gameName, serviceName, activeType, ...).</summary>
    public Dictionary<string, object?>? Context { get; init; }
}
