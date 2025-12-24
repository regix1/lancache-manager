namespace LancacheManager.Models;

public class ClientGroupMember
{
    public int Id { get; set; }
    public int ClientGroupId { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public DateTime AddedAtUtc { get; set; }

    // Navigation property
    public ClientGroup ClientGroup { get; set; } = null!;
}
