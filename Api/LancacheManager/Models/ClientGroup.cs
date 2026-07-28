using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Models;

public class ClientGroup : IUtcMarkable
{
    public long Id { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// When true the client stats surfaces emit one row per member IP, each labelled with the
    /// nickname, instead of summing the members into a single row. Defaults to false so
    /// existing groups keep reporting as one combined row.
    /// </summary>
    public bool SeparateMemberRows { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Navigation property
    public ICollection<ClientGroupMember> Members { get; set; } = new List<ClientGroupMember>();

    public void MarkDateTimesAsUtc()
    {
        CreatedAtUtc = CreatedAtUtc.AsUtc();
        UpdatedAtUtc = UpdatedAtUtc.AsUtc();
        foreach (var member in Members)
        {
            member.MarkDateTimesAsUtc();
        }
    }
}
