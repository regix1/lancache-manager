using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// How the notification bar draws a run: a full card, a background row, or nothing. Declared from
/// most to least visible, so the smaller value is the more visible one.
/// </summary>
[JsonConverter(typeof(RunVisibilityJsonConverter))]
public enum RunVisibility { Card, Background, Hidden }

/// <summary>
/// Serializes <see cref="RunVisibility"/> as camelCase strings ("card", "background", "hidden"), the
/// same way <see cref="NotificationModeJsonConverter"/> does for its enum.
/// </summary>
internal sealed class RunVisibilityJsonConverter : JsonStringEnumConverter<RunVisibility>
{
    public RunVisibilityJsonConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

/// <summary>
/// One tracked operation as the browser's run list draws it. Sent to account holders on every state
/// change and returned in bulk by the run-list endpoint, so the browser keeps exactly one entry per run.
/// </summary>
/// <param name="PreviousOperationId">The waiting operation this run took over from, set on its first row.</param>
/// <param name="NextOperationId">The final operation doing this run's work after it handed it on (the end
/// of the handoff chain), the same meaning as <see cref="OperationStatusResponse.NextOperationId"/>; null
/// when nothing took over.</param>
/// <param name="Warnings">What the run left undone, each a locale key and the values its text names;
/// empty when it did everything. A completed or canceled run with one is drawn as a kept amber card.</param>
/// <param name="Retained">True for an ending that stays until someone closes its card.</param>
/// <param name="Closed">True on the one row sent when a kept ending is closed.</param>
/// <param name="Repairing">True while a repair the run owes is running; the row stays until it ends.</param>
/// <param name="RepairError">Why the run's repair failed out, which keeps the row until it is closed;
/// null when no repair failed.</param>
/// <param name="FullRepair">True when the run is the one full repair per datasource that a set-aside
/// repair file starts.</param>
/// <param name="LiveIngest">True for a live log ingest pass; the browser keeps only its kept failure.</param>
/// <param name="IntegrationLogin">True for a mapping sign-in run, which belongs to no schedule.</param>
/// <param name="OwnerSessionId">The auth session whose browser alone draws this run; null for everyone.</param>
/// <param name="CompletedRevision">The revision of the run's first terminal row, which orders endings;
/// null while the run is live.</param>
/// <param name="Revision">Rises by exactly one for every row sent, in send order.</param>
public sealed record OperationRun(
    Guid OperationId, string OperationType, string Name, string Status, RunVisibility Visibility,
    double PercentComplete, string Message, string? Error, string? BlockedByName,
    Guid? PreviousOperationId, Guid? ParentOperationId, Guid? NextOperationId, IReadOnlyList<RunWarning> Warnings,
    bool Retained, bool Closed, bool Repairing, string? RepairError, bool FullRepair, int ConsecutiveFailures,
    bool LatestRunSucceeded, Guid? ScheduleId,
    PrefillPlatform? ServiceId, bool LiveIngest, bool IntegrationLogin, Guid? OwnerSessionId,
    long? CompletedRevision, DateTime StartedAt, long Revision);

/// <summary>
/// Every tracked run plus the revision read before they were listed, so the browser drops a run
/// missing from the list only when it has seen nothing newer about it.
/// </summary>
public sealed record OperationRunsSnapshot(IReadOnlyList<OperationRun> Runs, long Revision);

/// <summary>
/// One thing a run left undone, as the browser words it: a locale key and the values its text
/// names (a folder, a file count, a list of datasources).
/// </summary>
public sealed record RunWarning(string StageKey, IReadOnlyDictionary<string, object?> Context);
