using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

public class ClientGroupsService : IClientGroupsService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ClientGroupsService> _logger;

    public ClientGroupsService(AppDbContext context, ILogger<ClientGroupsService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<ClientGroup>> GetAllGroupsAsync(CancellationToken cancellationToken = default)
    {
        return (await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .OrderBy(g => g.Nickname)
            .ToListAsync(cancellationToken))
            .WithUtcMarking();
    }

    public async Task<ClientGroup?> GetGroupByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return (await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken))
            ?.WithUtcMarking();
    }

    public async Task<ClientGroup?> GetGroupByNicknameAsync(string nickname, CancellationToken cancellationToken = default)
    {
        return (await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Nickname == nickname, cancellationToken))
            ?.WithUtcMarking();
    }

    public async Task<ClientGroup?> GetGroupByClientIpAsync(string clientIp, CancellationToken cancellationToken = default)
    {
        var member = await _context.ClientGroupMembers
            .AsNoTracking()
            .Include(m => m.ClientGroup)
            .ThenInclude(g => g.Members)
            .FirstOrDefaultAsync(m => m.ClientIp == clientIp, cancellationToken);

        return member?.ClientGroup?.WithUtcMarking();
    }

    public async Task<ClientGroup> CreateGroupAsync(ClientGroup group, CancellationToken cancellationToken = default)
    {
        group.CreatedAtUtc = DateTime.UtcNow;
        _context.ClientGroups.Add(group);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created client group: {Nickname} (ID: {Id})", group.Nickname, group.Id);
        return group.WithUtcMarking();
    }

    public async Task<ClientGroup> UpdateGroupAsync(ClientGroup group, CancellationToken cancellationToken = default)
    {
        var existing = await _context.ClientGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == group.Id, cancellationToken);

        if (existing == null)
        {
            throw new InvalidOperationException($"Client group with ID {group.Id} not found");
        }

        existing.Nickname = group.Nickname;
        existing.Description = group.Description;
        existing.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated client group: {Nickname} (ID: {Id})", existing.Nickname, existing.Id);
        return existing.WithUtcMarking();
    }

    public async Task DeleteGroupAsync(int id, CancellationToken cancellationToken = default)
    {
        var group = await _context.ClientGroups.FindAsync(new object[] { id }, cancellationToken);
        if (group != null)
        {
            _context.ClientGroups.Remove(group);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Deleted client group: {Nickname} (ID: {Id})", group.Nickname, group.Id);
        }
    }

    public async Task<ClientGroupMember> AddMemberAsync(int groupId, string clientIp, CancellationToken cancellationToken = default)
    {
        // Check if IP is already in a group
        var existingMember = await _context.ClientGroupMembers
            .FirstOrDefaultAsync(m => m.ClientIp == clientIp, cancellationToken);

        if (existingMember != null)
        {
            throw new InvalidOperationException($"IP {clientIp} is already a member of group ID {existingMember.ClientGroupId}");
        }

        var group = await _context.ClientGroups.FindAsync(new object[] { groupId }, cancellationToken);
        if (group == null)
        {
            throw new InvalidOperationException($"Client group with ID {groupId} not found");
        }

        var member = new ClientGroupMember
        {
            ClientGroupId = groupId,
            ClientIp = clientIp,
            AddedAtUtc = DateTime.UtcNow
        };

        _context.ClientGroupMembers.Add(member);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Added IP {ClientIp} to client group {Nickname} (ID: {Id})", clientIp, group.Nickname, groupId);
        return member.WithUtcMarking();
    }

    public async Task RemoveMemberAsync(int groupId, string clientIp, CancellationToken cancellationToken = default)
    {
        var member = await _context.ClientGroupMembers
            .FirstOrDefaultAsync(m => m.ClientGroupId == groupId && m.ClientIp == clientIp, cancellationToken);

        if (member != null)
        {
            _context.ClientGroupMembers.Remove(member);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Removed IP {ClientIp} from client group ID {GroupId}", clientIp, groupId);
        }
    }

    public async Task<Dictionary<string, (int GroupId, string Nickname)>> GetIpToGroupMappingAsync(CancellationToken cancellationToken = default)
    {
        var mappings = await _context.ClientGroupMembers
            .AsNoTracking()
            .Join(
                _context.ClientGroups.AsNoTracking(),
                member => member.ClientGroupId,
                group => group.Id,
                (member, group) => new { member.ClientIp, member.ClientGroupId, group.Nickname })
            .ToListAsync(cancellationToken);

        return mappings
            .GroupBy(m => m.ClientIp)
            .ToDictionary(
                g => g.Key,
                g => (g.First().ClientGroupId, g.First().Nickname));
    }

    // ===== ICrudRepository-like methods (delegating to entity-specific methods) =====

    public Task<List<ClientGroup>> GetAllAsync(CancellationToken ct = default)
        => GetAllGroupsAsync(ct);

    public Task<ClientGroup?> GetByIdAsync(int id, CancellationToken ct = default)
        => GetGroupByIdAsync(id, ct);

    public Task<ClientGroup> CreateAsync(ClientGroup entity, CancellationToken ct = default)
        => CreateGroupAsync(entity, ct);

    public Task<ClientGroup> UpdateAsync(ClientGroup entity, CancellationToken ct = default)
        => UpdateGroupAsync(entity, ct);

    public async Task DeleteAsync(ClientGroup entity, CancellationToken ct = default)
        => await DeleteGroupAsync(entity.Id, ct);

    public async Task<bool> ExistsAsync(int id, CancellationToken ct = default)
        => await _context.ClientGroups.AnyAsync(g => g.Id == id, ct);
}
