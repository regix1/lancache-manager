using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Service interface for client group management.
/// Extends ICrudRepository for compatibility with CrudControllerBase.
/// </summary>
public interface IClientGroupsService : ICrudRepository<ClientGroup, long>
{
    // Entity-specific methods (GetAll/GetById/Create/Update are inherited from ICrudRepository)
    Task<ClientGroup?> GetByNicknameAsync(string nickname, CancellationToken cancellationToken = default);
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
    Task<ClientGroupMember> AddMemberAsync(long groupId, string clientIp, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(long groupId, string clientIp, CancellationToken cancellationToken = default);
    Task<Dictionary<string, (long GroupId, string Nickname)>> GetIpMappingAsync(CancellationToken cancellationToken = default);
}
