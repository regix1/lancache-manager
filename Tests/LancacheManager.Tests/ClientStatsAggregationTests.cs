using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the client-nickname contract shared by <c>GET /api/stats/clients</c> and the dashboard
/// batch's <c>clients</c> section: a combined group's IPs collapse into one nicknamed row and
/// that fold happens before the top-N cut, a group that reports separately emits one nicknamed
/// row per member IP instead, and both a separated member row and an ungrouped row always report
/// <c>IsGrouped=false</c> instead of omitting the flag.
/// </summary>
public sealed class ClientStatsAggregationTests
{
    private static readonly DateTime BaseActivity = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private static ClientIpAggregate Aggregate(
        string clientIp,
        long hitBytes,
        long missBytes,
        int downloads = 1,
        double durationSeconds = 10,
        DateTime? lastActivityUtc = null)
        => new(clientIp, hitBytes, missBytes, downloads, durationSeconds, lastActivityUtc ?? BaseActivity);

    /// <summary>No client address has a reverse-DNS name, which is also the shape every surface
    /// passes while the hostname lookup is turned off.</summary>
    private static readonly Dictionary<string, string> NoHostnames = new();

    private static Dictionary<string, string> Hostnames(params (string Ip, string Hostname)[] entries)
        => entries.ToDictionary(e => e.Ip, e => e.Hostname);

    private static Dictionary<string, ClientGroupAssignment> Mapping(
        params (string Ip, long GroupId, string Nickname)[] entries)
        => entries.ToDictionary(
            e => e.Ip,
            e => new ClientGroupAssignment(e.GroupId, e.Nickname, SeparateMemberRows: false));

    private static Dictionary<string, ClientGroupAssignment> SeparatedMapping(
        params (string Ip, long GroupId, string Nickname)[] entries)
        => entries.ToDictionary(
            e => e.Ip,
            e => new ClientGroupAssignment(e.GroupId, e.Nickname, SeparateMemberRows: true));

    [Fact]
    public void GroupedIpsCollapseIntoOneRowCarryingTheNickname()
    {
        var aggregates = new[]
        {
            Aggregate("10.0.0.2", hitBytes: 30, missBytes: 10, downloads: 2, durationSeconds: 4,
                lastActivityUtc: BaseActivity),
            Aggregate("10.0.0.1", hitBytes: 50, missBytes: 10, downloads: 3, durationSeconds: 6,
                lastActivityUtc: BaseActivity.AddMinutes(5))
        };

        var result = ClientStatsAggregationHelper.AggregateAndRank(
            aggregates,
            Mapping(("10.0.0.1", 7, "Lab PCs"), ("10.0.0.2", 7, "Lab PCs")),
            NoHostnames,
            limit: 10);

        var row = Assert.Single(result);
        Assert.Equal("Lab PCs", row.DisplayName);
        Assert.True(row.IsGrouped);
        Assert.Equal(7, row.GroupId);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.2" }, row.GroupMemberIps);
        Assert.Equal(80, row.TotalCacheHitBytes);
        Assert.Equal(20, row.TotalCacheMissBytes);
        Assert.Equal(100, row.TotalBytes);
        Assert.Equal(5, row.TotalDownloads);
        // 100 bytes over the summed 4 + 6 seconds. Taking any single member's duration instead of
        // the total gives a different speed, so this is what pins the sum.
        Assert.Equal(10, row.AverageBytesPerSecond);
        // The most recent member activity represents the group.
        Assert.Equal(BaseActivity.AddMinutes(5), row.LastActivityUtc);
        Assert.Equal(DateTimeKind.Utc, row.LastActivityUtc.Kind);
    }

    [Fact]
    public void GroupTotalsAreSummedBeforeTheLimitIsApplied()
    {
        // Neither group member out-ranks the ungrouped client on its own; only their combined
        // traffic does. Taking the top-N before folding would drop the group entirely.
        var aggregates = new[]
        {
            Aggregate("10.0.0.1", hitBytes: 60, missBytes: 0),
            Aggregate("10.0.0.2", hitBytes: 60, missBytes: 0),
            Aggregate("10.0.0.9", hitBytes: 100, missBytes: 0)
        };
        var mapping = Mapping(("10.0.0.1", 3, "Lab PCs"), ("10.0.0.2", 3, "Lab PCs"));

        var topOne = ClientStatsAggregationHelper.AggregateAndRank(aggregates, mapping, NoHostnames, limit: 1);

        var winner = Assert.Single(topOne);
        Assert.Equal("Lab PCs", winner.DisplayName);
        Assert.Equal(120, winner.TotalBytes);

        // With room for both, the group still ranks above the larger single IP.
        var topTwo = ClientStatsAggregationHelper.AggregateAndRank(aggregates, mapping, NoHostnames, limit: 2);
        Assert.Equal(new[] { "Lab PCs", null }, topTwo.Select(r => r.DisplayName));
        Assert.Equal(new[] { 120L, 100L }, topTwo.Select(r => r.TotalBytes));
    }

    [Fact]
    public void UngroupedRowsAreNeverMarkedGrouped()
    {
        var result = ClientStatsAggregationHelper.AggregateAndRank(
            new[] { Aggregate("10.0.0.9", hitBytes: 25, missBytes: 75, downloads: 4, durationSeconds: 5) },
            Mapping(("10.0.0.1", 3, "Lab PCs")),
            NoHostnames,
            limit: 10);

        var row = Assert.Single(result);
        Assert.False(row.IsGrouped);
        Assert.Null(row.DisplayName);
        Assert.Null(row.GroupId);
        Assert.Null(row.GroupMemberIps);
        Assert.Equal("10.0.0.9", row.ClientIp);
        Assert.Equal(25, row.CacheHitPercent);
        Assert.Equal(20, row.AverageBytesPerSecond);
    }

    [Fact]
    public void EmptyDurationsDoNotProduceInfiniteSpeeds()
    {
        var result = ClientStatsAggregationHelper.AggregateAndRank(
            new[] { Aggregate("10.0.0.9", hitBytes: 0, missBytes: 0, downloads: 0, durationSeconds: 0) },
            Mapping(),
            NoHostnames,
            limit: 10);

        var row = Assert.Single(result);
        Assert.Equal(0, row.CacheHitPercent);
        Assert.Equal(0, row.AverageBytesPerSecond);
    }

    [Fact]
    public void SpanSecondsIsZeroWhenNoDownloadHasFinished()
    {
        var start = BaseActivity;
        Assert.Equal(0, ClientStatsAggregationHelper.SpanSeconds(start, default));
        Assert.Equal(0, ClientStatsAggregationHelper.SpanSeconds(start, start));
        Assert.Equal(90, ClientStatsAggregationHelper.SpanSeconds(start, start.AddSeconds(90)));
    }

    [Fact]
    public void SeparatedGroupEmitsOneRowPerMemberCarryingTheNickname()
    {
        var aggregates = new[]
        {
            Aggregate("10.0.0.1", hitBytes: 50, missBytes: 10, downloads: 3, durationSeconds: 6),
            Aggregate("10.0.0.2", hitBytes: 30, missBytes: 10, downloads: 2, durationSeconds: 4)
        };

        var result = ClientStatsAggregationHelper.AggregateAndRank(
            aggregates,
            SeparatedMapping(("10.0.0.1", 7, "Lab PCs"), ("10.0.0.2", 7, "Lab PCs")),
            NoHostnames,
            limit: 10);

        Assert.Equal(2, result.Count);
        Assert.All(result, row =>
        {
            // The nickname labels the row, but the row speaks for one machine: it is not the
            // group row, so it carries no member list.
            Assert.Equal("Lab PCs", row.DisplayName);
            Assert.False(row.IsGrouped);
            Assert.Equal(7, row.GroupId);
            Assert.Null(row.GroupMemberIps);
        });

        var first = result.Single(r => r.ClientIp == "10.0.0.1");
        Assert.Equal(60, first.TotalBytes);
        Assert.Equal(3, first.TotalDownloads);

        var second = result.Single(r => r.ClientIp == "10.0.0.2");
        Assert.Equal(40, second.TotalBytes);
        Assert.Equal(2, second.TotalDownloads);
    }

    [Fact]
    public void SeparatedMembersCompeteIndividuallyForTheTopN()
    {
        // The fixture where 60 + 60 out-ranks the lone 100 when combined. Separated members no
        // longer pool their traffic, so the single IP takes the only slot.
        var aggregates = new[]
        {
            Aggregate("10.0.0.1", hitBytes: 60, missBytes: 0),
            Aggregate("10.0.0.2", hitBytes: 60, missBytes: 0),
            Aggregate("10.0.0.9", hitBytes: 100, missBytes: 0)
        };

        var topOne = ClientStatsAggregationHelper.AggregateAndRank(
            aggregates,
            SeparatedMapping(("10.0.0.1", 3, "Lab PCs"), ("10.0.0.2", 3, "Lab PCs")),
            NoHostnames,
            limit: 1);

        var winner = Assert.Single(topOne);
        Assert.Equal("10.0.0.9", winner.ClientIp);
        Assert.Null(winner.DisplayName);
        Assert.Equal(100, winner.TotalBytes);
    }

    [Fact]
    public void MixedModesRankWithoutDoubleCounting()
    {
        var aggregates = new[]
        {
            Aggregate("10.0.0.1", hitBytes: 50, missBytes: 0),
            Aggregate("10.0.0.2", hitBytes: 30, missBytes: 0),
            Aggregate("10.0.1.1", hitBytes: 40, missBytes: 0),
            Aggregate("10.0.1.2", hitBytes: 20, missBytes: 0),
            Aggregate("10.0.9.9", hitBytes: 70, missBytes: 0)
        };
        var mapping = Mapping(("10.0.0.1", 1, "Lab PCs"), ("10.0.0.2", 1, "Lab PCs"))
            .Concat(SeparatedMapping(("10.0.1.1", 2, "Dorm"), ("10.0.1.2", 2, "Dorm")))
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        var result = ClientStatsAggregationHelper.AggregateAndRank(aggregates, mapping, NoHostnames, limit: 50);

        // One combined row, two separated member rows, one ungrouped row.
        Assert.Equal(4, result.Count);
        Assert.Equal(
            aggregates.Sum(a => a.TotalCacheHitBytes + a.TotalCacheMissBytes),
            result.Sum(r => r.TotalBytes));
        Assert.Equal(result.Count, result.Select(r => r.ClientIp).Distinct().Count());
    }

    [Fact]
    public void SeparatedMemberRowsCarryPerIpDerivedMetrics()
    {
        var aggregates = new[]
        {
            Aggregate("10.0.0.1", hitBytes: 25, missBytes: 75, downloads: 4, durationSeconds: 5)
        };

        var ungrouped = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, Mapping(), NoHostnames, limit: 10));
        var separated = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, SeparatedMapping(("10.0.0.1", 7, "Lab PCs")), NoHostnames, limit: 10));

        Assert.Equal(ungrouped.CacheHitPercent, separated.CacheHitPercent);
        Assert.Equal(ungrouped.AverageBytesPerSecond, separated.AverageBytesPerSecond);
    }

    [Fact]
    public void SingleMemberGroupHasIdenticalTotalsInBothModes()
    {
        var aggregates = new[]
        {
            Aggregate("10.0.0.1", hitBytes: 40, missBytes: 60, downloads: 3, durationSeconds: 8)
        };

        var combined = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, Mapping(("10.0.0.1", 7, "Lab PCs")), NoHostnames, limit: 10));
        var separated = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, SeparatedMapping(("10.0.0.1", 7, "Lab PCs")), NoHostnames, limit: 10));

        Assert.Equal(combined.TotalBytes, separated.TotalBytes);
        Assert.Equal(combined.TotalDownloads, separated.TotalDownloads);
        Assert.Equal(combined.CacheHitPercent, separated.CacheHitPercent);
        Assert.Equal(combined.AverageBytesPerSecond, separated.AverageBytesPerSecond);
        Assert.Equal(combined.LastActivityUtc, separated.LastActivityUtc);

        // Identity is shared as well, so only the two group-shaped fields tell the rows apart.
        Assert.Equal(combined.ClientIp, separated.ClientIp);
        Assert.Equal(combined.DisplayName, separated.DisplayName);
        Assert.Equal(combined.GroupId, separated.GroupId);

        Assert.Equal(new[] { "10.0.0.1" }, combined.GroupMemberIps);
        Assert.Null(separated.GroupMemberIps);
        Assert.True(combined.IsGrouped);
        Assert.False(separated.IsGrouped);
    }

    [Fact]
    public void MembersWithNoAggregateProduceNoRow()
    {
        // An excluded or hidden member IP is filtered out by the query, so it reaches the fold
        // as a mapping entry with nothing to report.
        var aggregates = new[] { Aggregate("10.0.0.1", hitBytes: 40, missBytes: 0) };
        var entries = new (string Ip, long GroupId, string Nickname)[]
        {
            ("10.0.0.1", 4, "Lab PCs"),
            ("10.0.0.2", 4, "Lab PCs")
        };

        var combinedRow = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, Mapping(entries), NoHostnames, limit: 10));
        Assert.Equal("10.0.0.1", combinedRow.ClientIp);
        Assert.Equal(new[] { "10.0.0.1" }, combinedRow.GroupMemberIps);

        var separatedRow = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, SeparatedMapping(entries), NoHostnames, limit: 10));
        Assert.Equal("10.0.0.1", separatedRow.ClientIp);
    }

    [Fact]
    public void AClientWithNoNicknameIsLabelledWithItsHostname()
    {
        var result = ClientStatsAggregationHelper.AggregateAndRank(
            new[] { Aggregate("10.0.0.9", hitBytes: 40, missBytes: 0) },
            Mapping(),
            Hostnames(("10.0.0.9", "gaming-pc.lan")),
            limit: 10);

        var row = Assert.Single(result);
        // The short name is what a row shows: reverse-DNS answers are usually fully qualified and
        // a row has no width for the full name.
        Assert.Equal("gaming-pc", row.DisplayName);
        Assert.Equal("10.0.0.9", row.ClientIp);
        Assert.False(row.IsGrouped);
        Assert.Null(row.GroupId);
    }

    [Fact]
    public void ANicknameOutranksAHostname()
    {
        var aggregates = new[] { Aggregate("10.0.0.1", hitBytes: 40, missBytes: 0) };
        var hostnames = Hostnames(("10.0.0.1", "gaming-pc.lan"));

        var grouped = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, Mapping(("10.0.0.1", 7, "Lab PCs")), hostnames, limit: 10));
        var separated = Assert.Single(ClientStatsAggregationHelper.AggregateAndRank(
            aggregates, SeparatedMapping(("10.0.0.1", 7, "Lab PCs")), hostnames, limit: 10));

        Assert.Equal("Lab PCs", grouped.DisplayName);
        Assert.Equal("Lab PCs", separated.DisplayName);
    }

    [Fact]
    public void AClientWithNeitherNicknameNorHostnameKeepsShowingItsAddress()
    {
        var result = ClientStatsAggregationHelper.AggregateAndRank(
            new[] { Aggregate("10.0.0.9", hitBytes: 40, missBytes: 0) },
            Mapping(),
            Hostnames(("10.0.0.1", "other-pc.lan")),
            limit: 10);

        var row = Assert.Single(result);
        // No display name at all, rather than the address repeated into it: every consumer reads a
        // display name as a label standing in for the address and decorates it accordingly.
        Assert.Null(row.DisplayName);
        Assert.Equal("10.0.0.9", row.ClientIp);
    }

    [Fact]
    public void AHostnameWithNoDomainPartIsUsedWhole()
    {
        var result = ClientStatsAggregationHelper.AggregateAndRank(
            new[] { Aggregate("10.0.0.9", hitBytes: 40, missBytes: 0) },
            Mapping(),
            Hostnames(("10.0.0.9", "gaming-pc")),
            limit: 10);

        Assert.Equal("gaming-pc", Assert.Single(result).DisplayName);
    }

    [Fact]
    public void NewClientGroupsDefaultToCombinedRows()
    {
        // Locks the upgrade default at the entity level, independent of the migration's column
        // default, so a group created in code without touching the flag still reports as one row.
        Assert.False(new ClientGroup().SeparateMemberRows);
    }

    [Fact]
    public void BothClientSurfacesFoldThroughTheSharedHelper()
    {
        // A second Take-then-project implementation is exactly the regression that dropped
        // nicknames from the dashboard, so neither surface may rank clients on its own.
        foreach (var source in new[]
                 {
                     ReadSource("Controllers", "Dashboard", "StatsController.cs"),
                     ReadSource("Core", "Services", "Dashboard", "DashboardBatchService.cs")
                 })
        {
            Assert.Contains("ClientStatsAggregationHelper.AggregateAndRank(", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClientGroupWritesExpireTheLiveDashboardBatch()
    {
        var source = ReadSource("Controllers", "Clients", "ClientGroupsController.cs");

        var invalidations = 0;
        var searchFrom = 0;
        while (true)
        {
            var idx = source.IndexOf("_dashboardBatchService.InvalidateLiveCache();", searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            invalidations++;
            searchFrom = idx + 1;
        }

        // create, update, delete. Membership is saved as a whole list now and broadcasts the group
        // update instead, which expires every dashboard range rather than only the live one.
        Assert.Equal(3, invalidations);
    }

    [Fact]
    public void ClientGroupUpdatesPersistTheRowMode()
    {
        // UpdateAsync copies a hand-picked list of fields onto the tracked entity, so a field
        // left out of that list saves without error and reverts on the next fetch.
        var source = ReadSource("Infrastructure", "Services", "Repositories", "ClientGroupsService.cs");

        Assert.Contains("existing.SeparateMemberRows = group.SeparateMemberRows;", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, "Api", "LancacheManager", .. pathSegments]));
    }
}
