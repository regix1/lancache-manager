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

    /// <summary>
    /// The group's stamp as the caller last read it. The write is turned down when the group has
    /// moved on since. Null means the caller is not tracking the stamp and the write goes through
    /// unconditionally, which is how clients written before this existed keep working.
    /// </summary>
    public DateTime? ExpectedUpdatedAtUtc { get; set; }
}

/// <summary>
/// The complete address list a client group should end up with. Addresses missing from this list are
/// removed from the group, so one save carries the whole membership rather than one call per change.
/// </summary>
public class SetMembersRequest
{
    public List<string> ClientIps { get; set; } = new();

    /// <summary>
    /// The group's stamp as the caller last read it. The save is turned down when the group has moved
    /// on since. Null means the caller is not tracking the stamp and the save goes through
    /// unconditionally, which is how clients written before this existed keep working.
    /// </summary>
    public DateTime? ExpectedUpdatedAtUtc { get; set; }
}
