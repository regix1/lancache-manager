using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
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

    public async Task<List<ClientGroup>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return (await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .OrderBy(g => g.Nickname)
            .ToListAsync(cancellationToken))
            .WithUtcMarking();
    }

    public async Task<ClientGroup?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        return (await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == id, cancellationToken))
            ?.WithUtcMarking();
    }

    public async Task<ClientGroup?> GetByNicknameAsync(string nickname, CancellationToken cancellationToken = default)
    {
        return (await _context.ClientGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Nickname == nickname, cancellationToken))
            ?.WithUtcMarking();
    }

    /// <summary>
    /// The current instant at the resolution the timestamp column keeps. PostgreSQL stores a
    /// timestamp to the microsecond, while <see cref="DateTime.UtcNow"/> carries ticks a hundred
    /// times finer, so a stamp taken straight from the clock is handed back to the caller in a form
    /// the database will never return. The version precondition compares exact instants, so a
    /// caller sending that stamp back would be turned down against the very write that produced it.
    /// </summary>
    private static DateTime StoredNow()
    {
        var ticks = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    public async Task<ClientGroup> CreateAsync(ClientGroup group, CancellationToken cancellationToken = default)
    {
        group.CreatedAtUtc = StoredNow();

        // Stamped from birth so every group a caller can hold a copy of carries one. A group whose
        // stamp is missing cannot be saved against a precondition, which would leave exactly the
        // newest groups unprotected.
        group.UpdatedAtUtc = group.CreatedAtUtc;

        _context.ClientGroups.Add(group);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created client group: {Nickname} (ID: {Id})", group.Nickname, group.Id);
        return group.WithUtcMarking();
    }

    public async Task<ClientGroup> UpdateAsync(ClientGroup group, CancellationToken cancellationToken = default)
    {
        var existing = await _context.ClientGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == group.Id, cancellationToken);

        if (existing == null)
        {
            throw new NotFoundException($"Client group with ID {group.Id}");
        }

        existing.Nickname = group.Nickname;
        existing.Description = group.Description;
        existing.SeparateMemberRows = group.SeparateMemberRows;
        existing.UpdatedAtUtc = StoredNow();

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated client group: {Nickname} (ID: {Id})", existing.Nickname, existing.Id);
        return existing.WithUtcMarking();
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var group = await _context.ClientGroups.FindAsync(new object[] { id }, cancellationToken);
        if (group != null)
        {
            _context.ClientGroups.Remove(group);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Deleted client group: {Nickname} (ID: {Id})", group.Nickname, group.Id);
        }
    }

    /// <summary>
    /// Makes <paramref name="clientIps"/> the group's complete membership: rows for addresses that are
    /// no longer wanted are deleted, addresses that are new are inserted, and addresses already in the
    /// group are left where they are so their AddedAtUtc survives.
    /// </summary>
    /// <returns>
    /// The addresses that were skipped because a different group already owns them. They are returned
    /// rather than thrown so one contested address cannot fail the rest of the save.
    /// </returns>
    public async Task<List<string>> SetMembersAsync(long groupId, IReadOnlyList<string> clientIps, CancellationToken cancellationToken = default)
    {
        var group = await _context.ClientGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

        if (group == null)
        {
            throw new NotFoundException($"Client group with ID {groupId}");
        }

        var desiredIps = clientIps.Distinct(StringComparer.Ordinal).ToList();
        var currentIps = group.Members.Select(m => m.ClientIp).ToHashSet(StringComparer.Ordinal);

        var addedIps = desiredIps.Where(ip => !currentIps.Contains(ip)).ToList();

        // The unique index on ClientIp means an address another group holds cannot simply be inserted.
        // Reassigning it across groups is a separate decision with its own confirmation, so it is
        // skipped and named back to the caller.
        var ownedElsewhere = addedIps.Count == 0
            ? new List<string>()
            : await _context.ClientGroupMembers
                .Where(m => m.ClientGroupId != groupId && addedIps.Contains(m.ClientIp))
                .Select(m => m.ClientIp)
                .ToListAsync(cancellationToken);

        var rejectedIps = addedIps.Where(ip => ownedElsewhere.Contains(ip, StringComparer.Ordinal)).ToList();
        var insertedIps = addedIps.Where(ip => !rejectedIps.Contains(ip, StringComparer.Ordinal)).ToList();

        var desiredLookup = desiredIps.ToHashSet(StringComparer.Ordinal);
        var removedMembers = group.Members.Where(m => !desiredLookup.Contains(m.ClientIp)).ToList();

        _context.ClientGroupMembers.RemoveRange(removedMembers);
        _context.ClientGroupMembers.AddRange(insertedIps.Select(ip => new ClientGroupMember
        {
            ClientGroupId = groupId,
            ClientIp = ip,
            AddedAtUtc = DateTime.UtcNow
        }));

        // Membership is part of what a caller reads when it takes a copy of the group, so the stamp
        // has to move here too - otherwise two editors both hold a stamp that still looks current and
        // the second one silently replaces the first one's addresses.
        group.UpdatedAtUtc = StoredNow();

        // One save, so the removals and the insertions land together: EF Core wraps a single
        // SaveChanges in a transaction, and a half-applied membership would leave the group showing
        // addresses the user removed alongside ones they never added.
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Set members on client group {Nickname} (ID: {Id}): {Added} added, {Removed} removed, {Rejected} already owned elsewhere",
            group.Nickname, groupId, insertedIps.Count, removedMembers.Count, rejectedIps.Count);

        return rejectedIps;
    }

    public async Task<Dictionary<string, ClientGroupAssignment>> GetIpMappingAsync(CancellationToken cancellationToken = default)
    {
        var mappings = await _context.ClientGroupMembers
            .AsNoTracking()
            .Join(
                _context.ClientGroups.AsNoTracking(),
                member => member.ClientGroupId,
                group => group.Id,
                (member, group) => new
                {
                    member.ClientIp,
                    member.ClientGroupId,
                    group.Nickname,
                    group.SeparateMemberRows
                })
            .ToListAsync(cancellationToken);

        return mappings
            .GroupBy(m => m.ClientIp)
            .ToDictionary(
                g => g.Key,
                g => new ClientGroupAssignment(
                    g.First().ClientGroupId,
                    g.First().Nickname,
                    g.First().SeparateMemberRows));
    }

    // ===== ICrudRepository-like methods (delegating to entity-specific methods) =====
    // GetAllAsync/GetByIdAsync/CreateAsync/UpdateAsync now share signatures with the
    // entity-specific methods above (post-rename), so they satisfy ICrudRepository directly.

    public async Task DeleteAsync(ClientGroup entity, CancellationToken ct = default)
        => await DeleteAsync(entity.Id, ct);

    public async Task<bool> ExistsAsync(long id, CancellationToken ct = default)
        => await _context.ClientGroups.AnyAsync(g => g.Id == id, ct);
}
