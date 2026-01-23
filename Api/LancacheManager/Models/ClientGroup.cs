using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Models;

public class ClientGroup : IUtcMarkable
{
    public int Id { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Description { get; set; }
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
