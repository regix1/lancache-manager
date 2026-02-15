using LancacheManager.Core.Services;
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
        CreatedAtUtc = group.CreatedAtUtc,
        UpdatedAtUtc = group.UpdatedAtUtc,
        MemberIps = group.Members.Select(m => m.ClientIp).OrderBy(ip => ip).ToList()
    };

    /// <summary>
    /// Maps a collection of ClientGroups to DTOs
    /// </summary>
    public static List<ClientGroupDto> ToDtos(this IEnumerable<ClientGroup> groups) =>
        groups.Select(g => g.ToDto()).ToList();
}
