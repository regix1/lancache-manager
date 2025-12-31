using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Repositories;

public class ClientGroupsRepository : IClientGroupsRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<ClientGroupsRepository> _logger;

    public ClientGroupsRepository(AppDbContext context, ILogger<ClientGroupsRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<ClientGroup>> GetAllGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .OrderBy(g => g.Nickname)
            .ToListAsync(cancellationToken);

        foreach (var group in groups)
        {
            group.CreatedAtUtc = DateTime.SpecifyKind(group.CreatedAtUtc, DateTimeKind.Utc);
            if (group.UpdatedAtUtc.HasValue)
            {
                group.UpdatedAtUtc = DateTime.SpecifyKind(group.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
            foreach (var member in group.Members)
            {
                member.AddedAtUtc = DateTime.SpecifyKind(member.AddedAtUtc, DateTimeKind.Utc);
            }
        }

        return groups;
    }

    public async Task<ClientGroup?> GetGroupByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var group = await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        if (group != null)
        {
            group.CreatedAtUtc = DateTime.SpecifyKind(group.CreatedAtUtc, DateTimeKind.Utc);
            if (group.UpdatedAtUtc.HasValue)
            {
                group.UpdatedAtUtc = DateTime.SpecifyKind(group.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
            foreach (var member in group.Members)
            {
                member.AddedAtUtc = DateTime.SpecifyKind(member.AddedAtUtc, DateTimeKind.Utc);
            }
        }

        return group;
    }

    public async Task<ClientGroup?> GetGroupByNicknameAsync(string nickname, CancellationToken cancellationToken = default)
    {
        var group = await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Nickname == nickname, cancellationToken);

        if (group != null)
        {
            group.CreatedAtUtc = DateTime.SpecifyKind(group.CreatedAtUtc, DateTimeKind.Utc);
            if (group.UpdatedAtUtc.HasValue)
            {
                group.UpdatedAtUtc = DateTime.SpecifyKind(group.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
            foreach (var member in group.Members)
            {
                member.AddedAtUtc = DateTime.SpecifyKind(member.AddedAtUtc, DateTimeKind.Utc);
            }
        }

        return group;
    }

    public async Task<ClientGroup?> GetGroupByClientIpAsync(string clientIp, CancellationToken cancellationToken = default)
    {
        var member = await _context.ClientGroupMembers
            .AsNoTracking()
            .Include(m => m.ClientGroup)
            .ThenInclude(g => g.Members)
            .FirstOrDefaultAsync(m => m.ClientIp == clientIp, cancellationToken);

        if (member?.ClientGroup != null)
        {
            var group = member.ClientGroup;
            group.CreatedAtUtc = DateTime.SpecifyKind(group.CreatedAtUtc, DateTimeKind.Utc);
            if (group.UpdatedAtUtc.HasValue)
            {
                group.UpdatedAtUtc = DateTime.SpecifyKind(group.UpdatedAtUtc.Value, DateTimeKind.Utc);
            }
            foreach (var m in group.Members)
            {
                m.AddedAtUtc = DateTime.SpecifyKind(m.AddedAtUtc, DateTimeKind.Utc);
            }
            return group;
        }

        return null;
    }

    public async Task<ClientGroup> CreateGroupAsync(ClientGroup group, CancellationToken cancellationToken = default)
    {
        group.CreatedAtUtc = DateTime.UtcNow;
        _context.ClientGroups.Add(group);
        await _context.SaveChangesAsync(cancellationToken);

        group.CreatedAtUtc = DateTime.SpecifyKind(group.CreatedAtUtc, DateTimeKind.Utc);

        _logger.LogInformation("Created client group: {Nickname} (ID: {Id})", group.Nickname, group.Id);
        return group;
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

        existing.CreatedAtUtc = DateTime.SpecifyKind(existing.CreatedAtUtc, DateTimeKind.Utc);
        existing.UpdatedAtUtc = DateTime.SpecifyKind(existing.UpdatedAtUtc!.Value, DateTimeKind.Utc);
        foreach (var member in existing.Members)
        {
            member.AddedAtUtc = DateTime.SpecifyKind(member.AddedAtUtc, DateTimeKind.Utc);
        }

        _logger.LogInformation("Updated client group: {Nickname} (ID: {Id})", existing.Nickname, existing.Id);
        return existing;
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

        member.AddedAtUtc = DateTime.SpecifyKind(member.AddedAtUtc, DateTimeKind.Utc);

        _logger.LogInformation("Added IP {ClientIp} to client group {Nickname} (ID: {Id})", clientIp, group.Nickname, groupId);
        return member;
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
        // Query members with their groups, projecting to avoid null reference issues
        var mappings = await _context.ClientGroupMembers
            .AsNoTracking()
            .Include(m => m.ClientGroup)
            .Where(m => m.ClientGroup != null)
            .Select(m => new { m.ClientIp, m.ClientGroupId, m.ClientGroup!.Nickname })
            .ToListAsync(cancellationToken);

        // Build dictionary, handling potential duplicate IPs (shouldn't happen due to unique constraint)
        return mappings
            .GroupBy(m => m.ClientIp)
            .ToDictionary(
                g => g.Key,
                g => (g.First().ClientGroupId, g.First().Nickname));
    }
}
