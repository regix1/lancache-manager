using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Service interface for client group management.
/// Extends ICrudRepository for compatibility with CrudControllerBase.
/// </summary>
public interface IClientGroupsService : ICrudRepository<ClientGroup, long>
{
    // Entity-specific methods
    Task<List<ClientGroup>> GetAllGroupsAsync(CancellationToken cancellationToken = default);
    Task<ClientGroup?> GetGroupByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<ClientGroup?> GetGroupByNicknameAsync(string nickname, CancellationToken cancellationToken = default);
    Task<ClientGroup> CreateGroupAsync(ClientGroup group, CancellationToken cancellationToken = default);
    Task<ClientGroup> UpdateGroupAsync(ClientGroup group, CancellationToken cancellationToken = default);
    Task DeleteGroupAsync(long id, CancellationToken cancellationToken = default);
    Task<ClientGroupMember> AddMemberAsync(long groupId, string clientIp, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(long groupId, string clientIp, CancellationToken cancellationToken = default);
    Task<Dictionary<string, (long GroupId, string Nickname)>> GetIpToGroupMappingAsync(CancellationToken cancellationToken = default);
}
