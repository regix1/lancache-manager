using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// One client IP's traffic totals, as produced by the SQL <c>GROUP BY ClientIp</c> over Downloads.
/// </summary>
public readonly record struct ClientIpAggregate(
    string ClientIp,
    long TotalCacheHitBytes,
    long TotalCacheMissBytes,
    int TotalDownloads,
    double TotalDurationSeconds,
    DateTime LastActivityUtc);

/// <summary>
/// Folds per-IP download totals into the <see cref="ClientStatsWithGroup"/> wire shape shared by
/// <c>GET /api/stats/clients</c> and the dashboard batch's <c>clients</c> section, so both
/// surfaces rank and label clients identically.
/// </summary>
public static class ClientStatsAggregationHelper
{
    /// <summary>
    /// Total seconds spanned by a client's downloads. DateTime subtraction does not translate
    /// inside an aggregate, so both call sites project min/max and fold here.
    /// </summary>
    public static double SpanSeconds(DateTime minStartUtc, DateTime maxEndUtc)
        => maxEndUtc > minStartUtc ? (maxEndUtc - minStartUtc).TotalSeconds : 0;

    /// <summary>
    /// Runs the per-IP <c>GROUP BY</c> behind both client-stats surfaces and folds the result into
    /// <see cref="ClientIpAggregate"/>. Keeping the projection here as well as the ranking means the
    /// two surfaces cannot drift at the step immediately above the shared fold.
    /// </summary>
    public static async Task<List<ClientIpAggregate>> QueryIpAggregatesAsync(
        IQueryable<Download> query,
        CancellationToken ct)
    {
        var ipStats = await query
            .GroupBy(d => d.ClientIp)
            .Select(g => new
            {
                ClientIp = g.Key,
                TotalCacheHitBytes = g.Sum(d => d.CacheHitBytes),
                TotalCacheMissBytes = g.Sum(d => d.CacheMissBytes),
                TotalDownloads = g.Count(),
                // Min start and max end feed the client-side duration span below.
                MinStartTimeUtc = g.Min(d => d.StartTimeUtc),
                MaxEndTimeUtc = g.Max(d => d.EndTimeUtc),
                LastActivityUtc = g.Max(d => d.StartTimeUtc)
            })
            .ToListAsync(ct);

        return ipStats.Select(s => new ClientIpAggregate(
            s.ClientIp,
            s.TotalCacheHitBytes,
            s.TotalCacheMissBytes,
            s.TotalDownloads,
            SpanSeconds(s.MinStartTimeUtc, s.MaxEndTimeUtc),
            s.LastActivityUtc)).ToList();
    }

    /// <summary>
    /// The addresses worth naming for a caller about to show <paramref name="limit"/> rows: the
    /// busiest first, cut to what will be displayed. The ranking has to happen before names are
    /// looked up rather than after, because reading the aggregate list in the order the database
    /// grouped it and cutting there spends the lookup budget on machines no row shows while the
    /// busiest client keeps a bare address. Both client-stats surfaces call this so their name
    /// coverage cannot drift from each other.
    /// </summary>
    public static List<string> TopClientIpsByTraffic(IEnumerable<ClientIpAggregate> ipAggregates, int limit)
    {
        return ipAggregates
            .OrderByDescending(a => a.TotalCacheHitBytes + a.TotalCacheMissBytes)
            .Take(limit)
            .Select(a => a.ClientIp)
            .ToList();
    }

    /// <summary>
    /// Turns per-IP totals into ranked client rows, honouring each group's reporting mode.
    /// A group that reports combined collapses every member IP into one nicknamed row, and
    /// that fold happens BEFORE the top-N cut so a nickname spread over several IPs still
    /// ranks on its combined traffic. A group that reports separately emits one row per
    /// member IP instead, each labelled with the nickname and competing on its own traffic.
    /// IPs with no group produce a row with <c>IsGrouped=false</c> and no display name.
    /// Every aggregate feeds exactly one row, so no traffic is counted twice and the three
    /// row kinds rank against each other coherently. <paramref name="limit"/> caps ROWS: a
    /// separated group of five members can occupy five of them.
    /// <paramref name="ipToHostname"/> carries the reverse-DNS names known for these addresses and
    /// is empty whenever the hostname lookup is off.
    /// </summary>
    public static List<ClientStatsWithGroup> AggregateAndRank(
        IEnumerable<ClientIpAggregate> ipAggregates,
        IReadOnlyDictionary<string, ClientGroupAssignment> ipToGroupMapping,
        IReadOnlyDictionary<string, string> ipToHostname,
        int limit)
    {
        // The assignment travels with its member list so the nickname cannot go missing for a
        // group id that was collected, which two dictionaries keyed alike had to guarantee by hand.
        var groupedMembers = new Dictionary<long, (ClientGroupAssignment Assignment, List<ClientIpAggregate> Members)>();
        var memberStats = new List<ClientStatsWithGroup>();
        var ungroupedStats = new List<ClientStatsWithGroup>();

        foreach (var aggregate in ipAggregates)
        {
            if (!ipToGroupMapping.TryGetValue(aggregate.ClientIp, out var assignment))
            {
                ungroupedStats.Add(BuildClientStats(
                    clientIp: aggregate.ClientIp,
                    totalCacheHitBytes: aggregate.TotalCacheHitBytes,
                    totalCacheMissBytes: aggregate.TotalCacheMissBytes,
                    totalDownloads: aggregate.TotalDownloads,
                    totalDurationSeconds: aggregate.TotalDurationSeconds,
                    lastActivityUtc: aggregate.LastActivityUtc,
                    isGrouped: false,
                    displayName: ResolveDisplayName(null, ipToHostname, aggregate.ClientIp)));
                continue;
            }

            if (assignment.SeparateMemberRows)
            {
                // The nickname labels the row, but the row speaks for one machine: it is not
                // itself a group row and carries no member list, so member counts and IP lists
                // stay truthful downstream.
                memberStats.Add(BuildClientStats(
                    clientIp: aggregate.ClientIp,
                    totalCacheHitBytes: aggregate.TotalCacheHitBytes,
                    totalCacheMissBytes: aggregate.TotalCacheMissBytes,
                    totalDownloads: aggregate.TotalDownloads,
                    totalDurationSeconds: aggregate.TotalDurationSeconds,
                    lastActivityUtc: aggregate.LastActivityUtc,
                    isGrouped: false,
                    displayName: ResolveDisplayName(assignment.Nickname, ipToHostname, aggregate.ClientIp),
                    groupId: assignment.GroupId,
                    groupMemberIps: null));
                continue;
            }

            if (!groupedMembers.TryGetValue(assignment.GroupId, out var groupEntry))
            {
                groupEntry = (assignment, new List<ClientIpAggregate>());
                groupedMembers[assignment.GroupId] = groupEntry;
            }

            groupEntry.Members.Add(aggregate);
        }

        var groupedStats = new List<ClientStatsWithGroup>(groupedMembers.Count);
        foreach (var (groupId, groupEntry) in groupedMembers)
        {
            var members = groupEntry.Members;
            long cacheHitBytes = 0;
            long cacheMissBytes = 0;
            var downloads = 0;
            double durationSeconds = 0;
            var lastActivityUtc = members[0].LastActivityUtc;
            var memberIps = new List<string>(members.Count);

            foreach (var member in members)
            {
                cacheHitBytes += member.TotalCacheHitBytes;
                cacheMissBytes += member.TotalCacheMissBytes;
                downloads += member.TotalDownloads;
                durationSeconds += member.TotalDurationSeconds;
                if (member.LastActivityUtc > lastActivityUtc)
                {
                    lastActivityUtc = member.LastActivityUtc;
                }
                memberIps.Add(member.ClientIp);
            }

            memberIps.Sort();

            groupedStats.Add(BuildClientStats(
                clientIp: members[0].ClientIp,
                totalCacheHitBytes: cacheHitBytes,
                totalCacheMissBytes: cacheMissBytes,
                totalDownloads: downloads,
                totalDurationSeconds: durationSeconds,
                lastActivityUtc: lastActivityUtc,
                isGrouped: true,
                displayName: ResolveDisplayName(groupEntry.Assignment.Nickname, ipToHostname, members[0].ClientIp),
                groupId: groupId,
                groupMemberIps: memberIps));
        }

        return groupedStats
            .Concat(memberStats)
            .Concat(ungroupedStats)
            .OrderByDescending(c => c.TotalBytes)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// One precedence for every client label: the nickname, else the machine's own name from
    /// reverse DNS, else nothing so the row keeps showing the address. The short name (the text
    /// before the first dot) is what a row displays, because network reverse-DNS answers are
    /// usually fully qualified and a row has no width for the full name; the client applies the
    /// same rule to the hostname map, so both surfaces read alike. Returning null rather than the
    /// address itself is deliberate: consumers treat a display name as a label standing in for the
    /// address, and give it the affordance that reveals the address underneath.
    /// </summary>
    private static string? ResolveDisplayName(
        string? nickname,
        IReadOnlyDictionary<string, string> ipToHostname,
        string clientIp)
    {
        if (!string.IsNullOrWhiteSpace(nickname))
        {
            return nickname;
        }

        if (!ipToHostname.TryGetValue(clientIp, out var hostname) || string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var firstDot = hostname.IndexOf('.');
        return firstDot > 0 ? hostname[..firstDot] : hostname;
    }

    private static ClientStatsWithGroup BuildClientStats(
        string clientIp,
        long totalCacheHitBytes,
        long totalCacheMissBytes,
        int totalDownloads,
        double totalDurationSeconds,
        DateTime lastActivityUtc,
        bool isGrouped,
        string? displayName = null,
        long? groupId = null,
        List<string>? groupMemberIps = null)
    {
        var totalBytes = totalCacheHitBytes + totalCacheMissBytes;

        return new ClientStatsWithGroup
        {
            ClientIp = clientIp,
            DisplayName = displayName,
            GroupId = groupId,
            IsGrouped = isGrouped,
            GroupMemberIps = groupMemberIps,
            TotalCacheHitBytes = totalCacheHitBytes,
            TotalCacheMissBytes = totalCacheMissBytes,
            TotalBytes = totalBytes,
            CacheHitPercent = totalBytes > 0 ? (double)totalCacheHitBytes / totalBytes * 100 : 0,
            TotalDownloads = totalDownloads,
            TotalDurationSeconds = totalDurationSeconds,
            AverageBytesPerSecond = totalDurationSeconds > 0 ? totalBytes / totalDurationSeconds : 0,
            LastActivityUtc = lastActivityUtc.AsUtc()
        };
    }
}
