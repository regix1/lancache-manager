using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class UnmappedCacheContractTests
{
    private const string CacheRoot = "/cache";
    private const string DigestOne = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string DigestTwo = "1122334455667788990011223344aabb";
    private const string PathOne = "/cache/f9/e8/0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string PathTwo = "/cache/bb/aa/1122334455667788990011223344aabb";
    private const string ScanAnchor = "2026-08-27T11:00:00+00:00";

    [Fact]
    public void ValidateReport_AcceptsAWellFormedReport()
    {
        var report = BuildReport();

        UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot);

        Assert.Equal(2, report.OrphanCount);
        Assert.Equal(UnmappedScanReport.SupportedContractVersion, report.ContractVersion);
    }

    [Fact]
    public void ValidateReport_RejectsAScanAnchorThatDoesNotMatch()
    {
        var report = BuildReport();
        report.ScanStartedUtc = "2026-08-27T11:00:01+00:00";

        Assert.Throws<InvalidDataException>(
            () => UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot));
    }

    [Fact]
    public void ValidateReport_RejectsAnotherContractVersion()
    {
        var report = BuildReport();
        report.ContractVersion = UnmappedScanReport.SupportedContractVersion + 1;

        Assert.Throws<InvalidDataException>(
            () => UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot));
    }

    [Fact]
    public void ValidateReport_RejectsARepeatedDigest()
    {
        var report = BuildReport();
        report.Services[1].Files[0].Digest = DigestOne;
        report.Services[1].Files[0].Path = PathOne;

        Assert.Throws<InvalidDataException>(
            () => UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot));
    }

    [Fact]
    public void ValidateReport_RejectsACancelledReport()
    {
        var report = BuildReport();
        report.Cancelled = true;

        Assert.Throws<InvalidDataException>(
            () => UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/cache/../etc/passwd")]
    [InlineData("/other/f9/e8/0a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    [InlineData("/cache/e8/f9/0a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    public void ValidateReport_RejectsAPathThatIsNotTheDigestsCachePath(string path)
    {
        var report = BuildReport();
        report.Services[0].Files[0].Path = path;

        Assert.Throws<InvalidDataException>(
            () => UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot));
    }

    [Fact]
    public void ValidateReport_RejectsGroupTotalsThatDoNotMatchTheirFiles()
    {
        var report = BuildReport();
        report.Services[0].TotalBytes = 999;

        Assert.Throws<InvalidDataException>(
            () => UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot));
    }

    [Fact]
    public void ValidateReport_RejectsTopLevelTotalsThatDoNotMatchTheGroups()
    {
        var report = BuildReport();
        report.OrphanCount = 5;

        Assert.Throws<InvalidDataException>(
            () => UnmappedCacheService.ValidateReport(report, ScanAnchor, CacheRoot));
    }

    [Fact]
    public void ScanArguments_CarryTheAnchorAndNeverAskForStdoutProgress()
    {
        var arguments = UnmappedCacheService.BuildScanArguments(
            CacheRoot,
            "/ops/claimed.txt",
            "/ops/progress.json",
            ScanAnchor,
            "monolithic");

        Assert.Equal(
            new[]
            {
                "--key-scheme",
                "monolithic",
                "unmapped-scan",
                CacheRoot,
                "/ops/claimed.txt",
                "/ops/progress.json",
                "--scan-started-utc",
                ScanAnchor
            },
            arguments.ToArray());
        Assert.DoesNotContain("--progress", arguments);
    }

    [Fact]
    public void RemovalArguments_CarryTheClaimedDigestsAndTheEvidenceFile()
    {
        var arguments = UnmappedCacheService.BuildRemovalArguments(
            CacheRoot,
            "/ops/claimed.txt",
            "/ops/progress.json",
            "/ops/evidence.json",
            "bare_metal");

        Assert.Equal(
            new[]
            {
                "--key-scheme",
                "bare_metal",
                "unmapped-remove",
                CacheRoot,
                "/ops/claimed.txt",
                "/ops/progress.json",
                "--evidence-file",
                "/ops/evidence.json"
            },
            arguments.ToArray());
        Assert.DoesNotContain("--progress", arguments);
    }

    [Fact]
    public void ScanAnchor_IsAWholeSecondCarryingItsUtcOffset()
    {
        Assert.Equal(
            ScanAnchor,
            UnmappedCacheService.FormatScanStartedUtc(
                new DateTimeOffset(2026, 8, 27, 11, 0, 0, TimeSpan.Zero)));

        var captured = UnmappedCacheService.CaptureScanStartedUtc();
        Assert.Equal(0L, captured.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(TimeSpan.Zero, captured.Offset);
    }

    [Theory]
    [InlineData(PathOne)]
    [InlineData("C:\\cache\\f9\\e8\\0a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    public void DigestFromStoredPath_ReadsEitherHostsSeparator(string storedPath)
    {
        Assert.Equal(DigestOne, UnmappedCacheService.DigestFromStoredPath(storedPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/cache/f9/e8/not-a-cache-file")]
    [InlineData("/cache/F9/E8/0A1B2C3D4E5F60718293A4B5C6D7E8F9")]
    [InlineData("/cache/f9/e8/0a1b2c3d4e5f60718293a4b5c6d7e8")]
    public void DigestFromStoredPath_RefusesAnythingThatIsNotACacheFileName(string? storedPath)
    {
        Assert.Null(UnmappedCacheService.DigestFromStoredPath(storedPath));
    }

    [Fact]
    public async Task ClaimedDigests_PickUpAFileAttributedAfterTheScanAsync()
    {
        var options = InMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 440,
                GameName = "Team Fortress 2",
                CacheFilePathsJson = JsonSerializer.Serialize(new[] { PathOne })
            });
            await seed.SaveChangesAsync();
        }

        await using (var atScanTime = new AppDbContext(options))
        {
            var writer = new StringWriter();
            var counts = await UnmappedCacheService.WriteClaimedDigestsAsync(
                atScanTime,
                writer,
                CancellationToken.None);

            Assert.Equal(1L, counts.Written);
            Assert.DoesNotContain(DigestTwo, writer.ToString(), StringComparison.Ordinal);
        }

        // A detection run attributes the second file between the scan and the delete.
        await using (var betweenRuns = new AppDbContext(options))
        {
            betweenRuns.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "steam",
                CacheFilePathsJson = JsonSerializer.Serialize(new[] { PathTwo })
            });
            await betweenRuns.SaveChangesAsync();
        }

        await using (var atDeleteTime = new AppDbContext(options))
        {
            var writer = new StringWriter();
            var counts = await UnmappedCacheService.WriteClaimedDigestsAsync(
                atDeleteTime,
                writer,
                CancellationToken.None);

            // Rebuilt from the tables, so the file the remover reads now claims the new digest. [9]
            Assert.Equal(2L, counts.Written);
            Assert.Contains(DigestTwo, writer.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ClaimedDigests_UnionBothTablesAndCountUnreadableNamesSeparatelyAsync()
    {
        var options = InMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 440,
                GameName = "Team Fortress 2",
                CacheFilePathsJson = JsonSerializer.Serialize(new[] { PathOne, "/cache/logs/access.log" })
            });
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "steam",
                CacheFilePathsJson = JsonSerializer.Serialize(new[] { PathOne, PathTwo })
            });
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(options);
        var writer = new StringWriter();
        var counts = await UnmappedCacheService.WriteClaimedDigestsAsync(
            dbContext,
            writer,
            CancellationToken.None);

        // The same file is claimed by a game row and a service row, so it is written twice; the
        // scanner reads the file into a set.
        Assert.Equal(3L, counts.Written);
        Assert.Equal(1L, counts.Skipped);
    }

    [Fact]
    public async Task StoredScan_OnAnotherContractVersion_IsIgnoredAndLeftAloneAsync()
    {
        var options = InMemoryOptions();
        var scanId = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            seed.CachedUnmappedScans.Add(new CachedUnmappedScan
            {
                ScanId = scanId,
                ContractVersion = UnmappedScanReport.SupportedContractVersion + 1,
                CompletedAtUtc = new DateTime(2026, 8, 27, 11, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(options);
        Assert.Null(await UnmappedCacheService.RequireSupportedScanAsync(
            dbContext,
            scanId: null,
            CancellationToken.None));
        Assert.Null(await UnmappedCacheService.RequireSupportedScanAsync(
            dbContext,
            scanId,
            CancellationToken.None));
        Assert.Equal(1, await dbContext.CachedUnmappedScans.CountAsync());
    }

    [Fact]
    public async Task StoredScan_IsReturnedOnlyForItsOwnScanIdAsync()
    {
        var options = InMemoryOptions();
        var scanId = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            seed.CachedUnmappedScans.Add(new CachedUnmappedScan
            {
                ScanId = scanId,
                ContractVersion = UnmappedScanReport.SupportedContractVersion,
                CompletedAtUtc = new DateTime(2026, 8, 27, 11, 0, 0, DateTimeKind.Utc)
            });
            await seed.SaveChangesAsync();
        }

        await using var dbContext = new AppDbContext(options);
        var current = await UnmappedCacheService.RequireSupportedScanAsync(
            dbContext,
            scanId: null,
            CancellationToken.None);
        Assert.Equal(scanId, current?.ScanId);
        Assert.Null(await UnmappedCacheService.RequireSupportedScanAsync(
            dbContext,
            Guid.NewGuid(),
            CancellationToken.None));
    }

    [Fact]
    public async Task CachedScan_ReportsOneRowPerServiceAcrossDatasourcesAsync()
    {
        var options = InMemoryOptions();
        var scanId = Guid.NewGuid();
        await using (var seed = new AppDbContext(options))
        {
            seed.CachedUnmappedScans.Add(new CachedUnmappedScan
            {
                ScanId = scanId,
                ContractVersion = UnmappedScanReport.SupportedContractVersion,
                CompletedAtUtc = new DateTime(2026, 8, 27, 11, 0, 0, DateTimeKind.Utc)
            });
            seed.CachedUnmappedDetections.AddRange(
                StoredGroup(scanId, "steam", "default", 3, 300),
                StoredGroup(scanId, "steam", "secondary", 2, 200),
                StoredGroup(scanId, "epicgames", "default", 1, 50));
            await seed.SaveChangesAsync();
        }

        var response = await NewService(options).GetScanAsync(CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.HasCachedResults);
        Assert.Equal(scanId, response.ScanId);
        Assert.Equal(6L, response.TotalFiles);
        Assert.Equal(550L, response.TotalBytes);

        // Two datasources hold steam files, and the section shows steam once.
        Assert.Equal(
            new[] { "epicgames", "steam" },
            response.Services.Select(row => row.Service).ToArray());
        var steam = response.Services.Single(row => row.Service == "steam");
        Assert.Equal(5L, steam.FileCount);
        Assert.Equal(500L, steam.TotalBytes);
    }

    [Fact]
    public async Task CachedScan_IsNullWhenNothingIsStoredAsync()
    {
        Assert.Null(await NewService(InMemoryOptions()).GetScanAsync(CancellationToken.None));

        // What the endpoint returns instead, so a reader never sees a version it must reject.
        var empty = new UnmappedCacheResponse { HasCachedResults = false };
        Assert.Equal(UnmappedScanReport.SupportedContractVersion, empty.ContractVersion);
        Assert.Empty(empty.Services);
        Assert.Null(empty.ScanId);
        Assert.Null(empty.LastScanTime);
    }

    private static CachedUnmappedDetection StoredGroup(
        Guid scanId,
        string service,
        string datasource,
        long fileCount,
        long totalSizeBytes) => new()
    {
        ScanId = scanId,
        ServiceName = service,
        DatasourceName = datasource,
        FileCount = fileCount,
        TotalSizeBytes = totalSizeBytes,
        FilesJson = "[]"
    };

    private static UnmappedCacheService NewService(DbContextOptions<AppDbContext> options) =>
        new(
            NullLogger<UnmappedCacheService>.Instance,
            pathResolver: null!,
            rustProcessHelper: null!,
            notifications: null!,
            datasourceService: null!,
            new InMemoryDbContextFactory(options),
            operationTracker: null!,
            capabilityService: null!);

    private sealed class InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }

    private static DbContextOptions<AppDbContext> InMemoryOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"unmapped-cache-{Guid.NewGuid():N}")
            .Options;

    private static UnmappedScanReport BuildReport() => new()
    {
        ContractVersion = UnmappedScanReport.SupportedContractVersion,
        Cancelled = false,
        ScanStartedUtc = ScanAnchor,
        CacheRoot = CacheRoot,
        FilesOnDisk = 10,
        ClaimedDigests = 8,
        SkippedNonHashNames = 1,
        OrphanCount = 2,
        OrphanBytes = 3072,
        UnreadableKeys = 1,
        Services =
        [
            new UnmappedServiceGroup
            {
                Service = "steam",
                FileCount = 1,
                TotalBytes = 1024,
                Files =
                [
                    new UnmappedCacheFile
                    {
                        Digest = DigestOne,
                        Path = PathOne,
                        Url = "http://lancache.steamcontent.com/depot/228990/chunk/abc",
                        SizeBytes = 1024
                    }
                ]
            },
            new UnmappedServiceGroup
            {
                Service = "unknown",
                FileCount = 1,
                TotalBytes = 2048,
                Files =
                [
                    new UnmappedCacheFile
                    {
                        Digest = DigestTwo,
                        Path = PathTwo,
                        Url = null,
                        SizeBytes = 2048
                    }
                ]
            }
        ]
    };
}
