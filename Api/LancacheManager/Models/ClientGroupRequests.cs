namespace LancacheManager.Models;

public class CreateClientGroupRequest
{
    public string Nickname { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string>? InitialIps { get; set; }

    /// <summary>Omitted by older clients, which keeps the new group aggregated.</summary>
    public bool SeparateMemberRows { get; set; }
}

public class UpdateClientGroupRequest
{
    public string Nickname { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Nullable so an omitted field is rejected by validation instead of defaulting to false and
    /// silently collapsing a separated group back to a combined row.
    /// </summary>
    public bool? SeparateMemberRows { get; set; }
}

public class AddMemberRequest
{
    public string ClientIp { get; set; } = string.Empty;
}
