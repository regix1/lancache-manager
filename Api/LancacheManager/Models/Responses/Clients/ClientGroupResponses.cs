namespace LancacheManager.Models;

public class ClientGroupDto
{
    public long Id { get; set; }
    public string Nickname { get; set; } = string.Empty;

    /// <summary>Optional free-text note. Null when the group was created or last saved without one.</summary>
    public string? Description { get; set; }
    public bool SeparateMemberRows { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the group's fields or membership were last saved. Null when the group has never been updated since creation.</summary>
    public DateTime? UpdatedAtUtc { get; set; }
    public List<string> MemberIps { get; set; } = new();
}

/// <summary>
/// The outcome of replacing a client group's addresses: the group as it now stands, plus every
/// address the request asked for that did not land, so the caller can name them instead of
/// reporting a save that only partly happened.
/// </summary>
public class SetMembersResponse
{
    public ClientGroupDto Group { get; set; } = new();

    /// <summary>Addresses left out because a different group already owns them.</summary>
    public List<string> RejectedIps { get; set; } = new();
}

/// <summary>
/// A newly created client group, plus every address from the create request that did not land.
/// Derives from <see cref="ClientGroupDto"/> so the create response is still the group itself and the
/// rejection list is purely additive for callers that already read the group fields.
/// </summary>
public class CreateClientGroupResponse : ClientGroupDto
{
    /// <summary>Addresses left out because a different group already owns them, or because they are not an address.</summary>
    public List<string> RejectedIps { get; set; } = new();
}

/// <summary>
/// Names the addresses in a membership request that are not addresses at all, so the caller can point
/// at the offending entries rather than being told the whole request was bad.
/// </summary>
public class InvalidClientIpsResponse
{
    public string Error { get; set; } = string.Empty;

    /// <summary>i18n key naming the refusal, so the browser shows it in the reader's language.</summary>
    public string? StageKey { get; set; }

    public List<string> InvalidIps { get; set; } = new();
}

/// <summary>
/// The answer to a membership save built on a copy of the group that has since moved on. Carries the
/// group as it now stands so the caller can start again from the current addresses without a second
/// request.
/// </summary>
public class ClientGroupChangedResponse
{
    /// <summary>The only value <see cref="Error"/> takes, so callers match on it rather than on prose.</summary>
    public const string ErrorCode = "client-group-changed";

    public string Error { get; set; } = string.Empty;
    public ClientGroupDto CurrentGroup { get; set; } = new();
}

/// <summary>
/// The answer to a create that asked for addresses and could not take a single one. The group is
/// removed again before this goes out, so there is no group to carry - only the addresses that
/// blocked it.
/// </summary>
public class RejectedClientIpsResponse
{
    /// <summary>The only value <see cref="Error"/> takes, so callers match on it rather than on prose.</summary>
    public const string ErrorCode = "all-addresses-rejected";

    public string Error { get; set; } = string.Empty;
    public List<string> RejectedIps { get; set; } = new();
}

/// <summary>
/// Extended ClientStats that includes client group information for display
/// </summary>
public class ClientStatsWithGroup
{
    public string ClientIp { get; set; } = string.Empty;
    public string? DisplayName { get; set; } // Nickname if grouped, null if not
    public long? GroupId { get; set; }
    public bool IsGrouped { get; set; }
    public List<string>? GroupMemberIps { get; set; } // All IPs in group (if aggregated)
    public long TotalCacheHitBytes { get; set; }
    public long TotalCacheMissBytes { get; set; }
    public long TotalBytes { get; set; }
    public double CacheHitPercent { get; set; }
    public int TotalDownloads { get; set; }
    public double TotalDurationSeconds { get; set; }
    public double AverageBytesPerSecond { get; set; }

    /// <summary>
    /// Last activity timestamp in UTC. Frontend handles timezone conversion.
    /// </summary>
    public DateTime LastActivityUtc { get; set; }
}
