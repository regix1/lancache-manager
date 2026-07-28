using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Extensions;

/// <summary>
/// Extension methods for mapping entities to DTOs
/// </summary>
public static class MappingExtensions
{
    // ===== ClientGroup Mappings =====

    /// <summary>
    /// Maps a ClientGroup entity to a ClientGroupDto
    /// </summary>
    public static ClientGroupDto ToDto(this ClientGroup group) => new()
    {
        Id = group.Id,
        Nickname = group.Nickname,
        Description = group.Description,
        SeparateMemberRows = group.SeparateMemberRows,
        CreatedAtUtc = group.CreatedAtUtc,
        UpdatedAtUtc = group.UpdatedAtUtc,
        MemberIps = group.Members.Select(m => m.ClientIp).OrderBy(ip => ip).ToList()
    };
}
