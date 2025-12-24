using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Repositories.Interfaces;

public interface IClientGroupsRepository
{
    Task<List<ClientGroup>> GetAllGroupsAsync(CancellationToken cancellationToken = default);
    Task<ClientGroup?> GetGroupByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<ClientGroup?> GetGroupByNicknameAsync(string nickname, CancellationToken cancellationToken = default);
    Task<ClientGroup?> GetGroupByClientIpAsync(string clientIp, CancellationToken cancellationToken = default);
    Task<ClientGroup> CreateGroupAsync(ClientGroup group, CancellationToken cancellationToken = default);
    Task<ClientGroup> UpdateGroupAsync(ClientGroup group, CancellationToken cancellationToken = default);
    Task DeleteGroupAsync(int id, CancellationToken cancellationToken = default);
    Task<ClientGroupMember> AddMemberAsync(int groupId, string clientIp, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(int groupId, string clientIp, CancellationToken cancellationToken = default);
    Task<Dictionary<string, (int GroupId, string Nickname)>> GetIpToGroupMappingAsync(CancellationToken cancellationToken = default);
}
