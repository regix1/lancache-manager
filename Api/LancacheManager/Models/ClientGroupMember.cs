using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Models;

public class ClientGroupMember : IUtcMarkable
{
    public int Id { get; set; }
    public int ClientGroupId { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public DateTime AddedAtUtc { get; set; }

    // Navigation property
    public ClientGroup ClientGroup { get; set; } = null!;

    public void MarkDateTimesAsUtc()
    {
        AddedAtUtc = AddedAtUtc.AsUtc();
    }
}
