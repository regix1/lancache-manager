using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// One schedule's recent endings, as <see cref="ServiceScheduleRegistry"/> orders them by the
/// revision of each ending's first terminal row. Enough to count the current failure streak and to
/// tell whether a run succeeded after a kept ending, whatever order the terminal handlers run in.
/// </summary>
internal sealed class ScheduleOutcomes
{
    /// <summary>Each recorded ending's status by its completed revision, oldest first.</summary>
    public SortedDictionary<long, OperationStatus> Endings { get; } = new();
}
