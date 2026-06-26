using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the orphaned-service cleanup FK crash and the Xbox data-loss guard.
///
/// Root cause (see DownloadCleanupService.CleanupOrphanedServicesCoreAsync): the cleanup classified
/// any <c>Downloads.Service</c> absent from the log-file service names as orphaned and deleted its
/// children by Service NAME before deleting the Downloads. Xbox uses a cache-split identity -
/// <c>Downloads.Service='xbox'</c> but its cache LogEntries live under <c>'wsus'</c> - so (1) 'xbox'
/// was always flagged orphaned, and (2) the by-name child delete missed the 'wsus' LogEntries that
/// reference the xbox Downloads via <c>DownloadId</c>, so deleting the parent Downloads violated
/// <c>FK_LogEntries_Downloads_DownloadId</c> (NO ACTION) - PostgreSQL 23503.
///
/// The fix: (a) nullify child LogEntries by <c>DownloadId</c> (not by Service name) before deleting
/// the parents, and (b) treat a cache-split service as in-use when its alias appears in the logs so
/// Xbox is never flagged orphaned.
///
/// The pure classification tests exercise the data-loss guard with no provider. The integration
/// tests run the real ExecuteUpdate/ExecuteDelete cleanup against EF Core's Sqlite provider with
/// foreign keys enforced - the only way to reproduce the FK crash, since the InMemory provider
/// neither supports ExecuteUpdate/ExecuteDelete nor enforces foreign keys. On the PRE-FIX code these
/// integration tests throw the FK violation (the cleanup) / delete the xbox Download (data loss);
/// post-fix they complete cleanly and Xbox survives.
/// </summary>
public class DownloadCleanupServiceTests
{
    // ---------------------------------------------------------------------------------------------
    // Pure classification - data-loss guard (no DB provider needed)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ComputeOrphanedServices_XboxPresentViaWsusAlias_NotOrphaned()
    {
        // 'xbox' is absent from the logs under its own name, but its cache lives under 'wsus', which
        // IS present -> xbox must NOT be flagged orphaned (else every Xbox download gets deleted).
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam", "wsus" });

        Assert.DoesNotContain("xbox", orphans);
        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxOnlyService_PresentViaWsusAlias_NotOrphaned()
    {
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox" },
            new HashSet<string> { "wsus" });

        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxWithoutWsusInLogs_IsOrphaned()
    {
        // The alias only protects xbox while its cache (wsus) is still present. With wsus gone too,
        // xbox is genuinely orphaned - the guard is conditional, not an unconditional whitelist.
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam" });

        Assert.Contains("xbox", orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_GenuineOrphanDetected_PresentServiceKept()
    {
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "origin", "steam" },
            new HashSet<string> { "steam", "wsus" });

        Assert.Contains("origin", orphans);
        Assert.DoesNotContain("steam", orphans);
    }

    // ---------------------------------------------------------------------------------------------
    // Integration - real cleanup against Sqlite with foreign keys enforced
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_XboxCacheSplit_NotDeleted_AndNoFkViolation()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        long xboxId;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            // 'steam' is a present service so the "all services orphaned" safety check does not trip.
            var steam = NewDownload("steam");
            var xbox = NewDownload("xbox");
            seed.Downloads.AddRange(steam, xbox);
            await seed.SaveChangesAsync();

            xboxId = xbox.Id;

            // Xbox cache LogEntry is recorded under 'wsus' and references the xbox Download by FK.
            seed.LogEntries.Add(NewLogEntry("wsus", xboxId));
            await seed.SaveChangesAsync();
        }

        // 'xbox' never appears in log-file service names; only steam + wsus do.
        var logServices = new HashSet<string> { "steam", "wsus" };

        await using (var run = new AppDbContext(options))
        {
            // PRE-FIX: 'xbox' is flagged orphaned and the parent Downloads delete throws the FK
            // violation (the wsus child is not matched by Service name). POST-FIX: the wsus alias
            // marks xbox in-use, so nothing is removed and no exception is thrown.
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            // Data-loss guard: the Xbox download survives a cleanup where 'xbox' is absent from logs.
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "xbox"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));

            // The wsus LogEntry is untouched and still references the xbox Download.
            var wsusEntry = await assert.LogEntries.SingleAsync(le => le.Service == "wsus");
            Assert.Equal(xboxId, wsusEntry.DownloadId);
        }
    }

    [Fact]
    public async Task Cleanup_OrphanWithCrossServiceChild_NullifiesFkBeforeDelete_NoViolation()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        long originId;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            var steam = NewDownload("steam");   // present -> safety check passes
            var origin = NewDownload("origin"); // genuinely orphaned (absent from logs, no alias)
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            originId = origin.Id;

            // A child LogEntry referencing the origin Download but recorded under a DIFFERENT Service
            // name, so the by-name child delete misses it - the parent delete would hit the FK unless
            // the child is re-pointed by DownloadId first.
            seed.LogEntries.Add(NewLogEntry("othercdn", originId));
            await seed.SaveChangesAsync();
        }

        var logServices = new HashSet<string> { "steam" };

        await using (var run = new AppDbContext(options))
        {
            // PRE-FIX: deleting the origin Downloads throws (othercdn child still references it).
            // POST-FIX: the child FK is nulled by DownloadId first, so the delete succeeds.
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.False(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));

            // The cross-service child survived (its Service != 'origin') with its FK nulled.
            var child = await assert.LogEntries.SingleAsync(le => le.Service == "othercdn");
            Assert.Null(child.DownloadId);
        }
    }

    [Fact]
    public async Task Cleanup_OrphanWithSameServiceChild_RemovesAllServiceData()
    {
        using var connection = OpenSharedConnection();
        var options = SqliteOptions(connection);

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            var steam = NewDownload("steam");
            var origin = NewDownload("origin");
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            seed.LogEntries.Add(NewLogEntry("origin", origin.Id));
            seed.ServiceStats.Add(new ServiceStats
            {
                Service = "origin",
                LastActivityUtc = DateTime.UtcNow,
                LastActivityLocal = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var logServices = new HashSet<string> { "steam" };

        await using (var run = new AppDbContext(options))
        {
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.False(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.False(await assert.LogEntries.AnyAsync(le => le.Service == "origin"));
            Assert.False(await assert.ServiceStats.AnyAsync(s => s.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    // A shared, kept-open in-memory Sqlite connection with foreign keys enforced. The database lives
    // only for as long as the connection is open, so every AppDbContext in a test reuses it.
    private static SqliteConnection OpenSharedConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        return connection;
    }

    private static DbContextOptions<AppDbContext> SqliteOptions(SqliteConnection connection)
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

    private static Download NewDownload(string service) => new Download
    {
        Service = service,
        ClientIp = "10.0.0.1",
        StartTimeUtc = DateTime.UtcNow,
        StartTimeLocal = DateTime.UtcNow,
        EndTimeUtc = DateTime.UtcNow,
        EndTimeLocal = DateTime.UtcNow,
        CacheHitBytes = 1,
        CacheMissBytes = 1,
        IsActive = false,
        Datasource = "default"
    };

    private static LogEntryRecord NewLogEntry(string service, long downloadId) => new LogEntryRecord
    {
        Service = service,
        ClientIp = "10.0.0.1",
        Url = "/cache/object",
        Timestamp = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        DownloadId = downloadId
    };
}
