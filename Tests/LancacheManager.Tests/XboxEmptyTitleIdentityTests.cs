using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Core.Services.Xbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// An Xbox title is either present or the entry names nothing. The empty string is neither, and the
/// two halves of the codebase disagree about it: GamesOnDiskCalculator.GetDownloadGameKey buckets a
/// download with an empty GameName as steam:0, while the detection queries test the column against
/// null and bucket the same row as a named game. A row stamped with "" is also unreachable by the
/// re-resolution query, which selects on GameName == null, so it can never be given its real title.
/// These tests pin the guards that keep the empty value out of the catalog, off a download, and out
/// of the detection cache.
/// </summary>
public class XboxEmptyTitleIdentityTests
{
    private const string Guid1 = "12345678-90ab-cdef-1234-567890abcdef";
    private const string Guid2 = "abcdef12-3456-7890-abcd-ef1234567890";

    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private static DbContextOptions<AppDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"xbox_empty_title_{System.Guid.NewGuid():N}")
            .Options;

    private static XboxMappingService NewMappingService(DbContextOptions<AppDbContext> options)
    {
        var apiClient = new XboxApiDirectClient(new HttpClient(), NullLogger<XboxApiDirectClient>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();

        return new XboxMappingService(
            new InMemoryDbContextFactory(options),
            notifications,
            apiClient,
            NullLogger<XboxMappingService>.Instance);
    }

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static CdnInfo NewCatalogEntry(string productId, string title, string guid)
        => new()
        {
            AppId = productId,
            Name = title,
            CdnHost = "assets1.xboxlive.com",
            FilePathFragments = new List<string> { $"/filestreamingservice/files/{guid}" }
        };

    private static Download NewWsusDownload(string guid)
        => new()
        {
            Service = "wsus",
            ClientIp = "10.0.0.5",
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
            EndTimeUtc = DateTime.UtcNow.AddMinutes(-5),
            CacheHitBytes = 1024,
            IsActive = false,
            LastUrl = $"http://assets1.xboxlive.com/filestreamingservice/files/{guid}?P1=123"
        };

    // -----------------------------------------------------------------------------------------
    // The catalog write path: CdnInfo.Name defaults to string.Empty and the daemon's get-cdn-info
    // reply leaves it there for a product it has no title for. The update branches already refuse
    // a blank title; the insert branches used to store it.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task MergeDaemonCatalogAsync_RejectsEntryWithNoTitle()
    {
        var options = NewInMemoryOptions();
        var service = NewMappingService(options);

        await service.MergeDaemonCatalogAsync(new List<CdnInfo>
        {
            NewCatalogEntry("9NBLGGH537DL", string.Empty, Guid1)
        });

        await using var db = new AppDbContext(options);
        Assert.Empty(await db.XboxGameMappings.ToListAsync());
        Assert.Empty(await db.XboxCdnPatterns.ToListAsync());
    }

    [Fact]
    public async Task MergeDaemonCatalogAsync_RejectsWhitespaceTitle()
    {
        var options = NewInMemoryOptions();
        var service = NewMappingService(options);

        await service.MergeDaemonCatalogAsync(new List<CdnInfo>
        {
            NewCatalogEntry("9NBLGGH537DL", "   ", Guid1)
        });

        await using var db = new AppDbContext(options);
        Assert.Empty(await db.XboxGameMappings.ToListAsync());
        Assert.Empty(await db.XboxCdnPatterns.ToListAsync());
    }

    [Fact]
    public async Task MergeDaemonCatalogAsync_KeepsEntryWithRealTitle()
    {
        var options = NewInMemoryOptions();
        var service = NewMappingService(options);

        await service.MergeDaemonCatalogAsync(new List<CdnInfo>
        {
            NewCatalogEntry("9NBLGGH537DL", "Halo Infinite", Guid1),
            NewCatalogEntry("9NTITLELESS1", string.Empty, Guid2)
        });

        await using var db = new AppDbContext(options);
        var mapping = Assert.Single(await db.XboxGameMappings.ToListAsync());
        Assert.Equal("Halo Infinite", mapping.Title);

        var pattern = Assert.Single(await db.XboxCdnPatterns.ToListAsync());
        Assert.Equal("Halo Infinite", pattern.Title);
        Assert.Equal($"/filestreamingservice/files/{Guid1}", pattern.UrlFragment);
    }

    // -----------------------------------------------------------------------------------------
    // The download write path: a pattern with no title must not be stamped onto a download. The
    // re-resolution query selects on GameName == null, so a row stamped with "" is stuck forever.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ResolveDownloadsAsync_LeavesDownloadGenericWhenThePatternHasNoTitle()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.XboxCdnPatterns.Add(new XboxCdnPattern
            {
                ProductId = "9NBLGGH537DL",
                Title = string.Empty,
                UrlFragment = $"/filestreamingservice/files/{Guid1}",
                CdnHost = "assets1.xboxlive.com"
            });
            seed.Downloads.Add(NewWsusDownload(Guid1));
            await seed.SaveChangesAsync();
        }

        var resolved = await NewMappingService(options).ResolveDownloadsAsync();

        Assert.Equal(0, resolved);

        await using var db = new AppDbContext(options);
        var download = Assert.Single(await db.Downloads.ToListAsync());

        // Null, not "": the candidate query above selects on GameName == null, so the row is still
        // picked up once the daemon contributes a real title.
        Assert.Null(download.GameName);
        Assert.Equal("wsus", download.Service);
        Assert.Null(download.XboxProductId);
    }

    [Fact]
    public async Task ResolveDownloadsAsync_StampsARealTitleOntoTheDownload()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.XboxCdnPatterns.Add(new XboxCdnPattern
            {
                ProductId = "9NBLGGH537DL",
                Title = "Halo Infinite",
                UrlFragment = $"/filestreamingservice/files/{Guid1}",
                CdnHost = "assets1.xboxlive.com"
            });
            seed.Downloads.Add(NewWsusDownload(Guid1));
            await seed.SaveChangesAsync();
        }

        var resolved = await NewMappingService(options).ResolveDownloadsAsync();

        Assert.Equal(1, resolved);

        await using var db = new AppDbContext(options);
        var download = Assert.Single(await db.Downloads.ToListAsync());
        Assert.Equal("Halo Infinite", download.GameName);
        Assert.Equal("xbox", download.Service);
        Assert.Equal("9NBLGGH537DL", download.XboxProductId);
    }

    // -----------------------------------------------------------------------------------------
    // Recovery of a name that was wiped after the row had already been canonicalized to
    // Service = "xbox". A full Steam PICS scan and the SteamDepotMappings arm of the database reset
    // both clear GameName on every Downloads row, Xbox rows included. Such a row keeps its
    // XboxProductId but the Rust detection queries only count a Download as a named game when
    // GameName IS NOT NULL, so it has to be offered to the resolver again.
    // -----------------------------------------------------------------------------------------

    // An assets1.xboxlive.com package path: no /filestreamingservice/ marker, but two canonical
    // GUIDs, which is the second shape IsValidFragment accepts.
    private const string PackageFragment = "/4/" + Guid1 + "/" + Guid2 + "/1.0.23.1";

    private static Download NewNamelessXboxDownload(bool isActive)
        => new()
        {
            Service = "xbox",
            GameName = null,
            XboxProductId = "C19N0723PHFL",
            ClientIp = "10.0.0.5",
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
            EndTimeUtc = DateTime.UtcNow.AddMinutes(-5),
            CacheHitBytes = 1024,
            IsActive = isActive,
            LastUrl = $"http://assets1.xboxlive.com{PackageFragment}.{Guid1}/bo4-ww-en-fr_1.0.23.1_x64__ht1qfjb0gaftw"
        };

    private static void SeedBlackOps4Catalog(AppDbContext db)
    {
        // ImageUrl is set so the banner-art pass has nothing to fetch. Left empty it would send a
        // real DisplayCatalog request from the test.
        db.XboxGameMappings.Add(new XboxGameMapping
        {
            ProductId = "C19N0723PHFL",
            Title = "Call of Duty®: Black Ops 4",
            ImageUrl = "https://store-images.microsoft.com/image/apps.1.jpg"
        });
        db.XboxCdnPatterns.Add(new XboxCdnPattern
        {
            ProductId = "C19N0723PHFL",
            Title = "Call of Duty®: Black Ops 4",
            UrlFragment = PackageFragment,
            CdnHost = "assets1.xboxlive.com"
        });
    }

    [Fact]
    public async Task ResolveDownloadsAsync_RenamesAnXboxRowWhoseNameWasWiped()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            SeedBlackOps4Catalog(seed);
            seed.Downloads.Add(NewNamelessXboxDownload(isActive: false));
            await seed.SaveChangesAsync();
        }

        var resolved = await NewMappingService(options).ResolveDownloadsAsync();

        Assert.Equal(1, resolved);

        await using var db = new AppDbContext(options);
        var download = Assert.Single(await db.Downloads.ToListAsync());
        Assert.Equal("Call of Duty®: Black Ops 4", download.GameName);
        Assert.Equal("xbox", download.Service);
        Assert.Equal("C19N0723PHFL", download.XboxProductId);
    }

    [Fact]
    public async Task ResolveDownloadsAsync_LeavesANamelessActiveXboxRowAlone()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            SeedBlackOps4Catalog(seed);
            seed.Downloads.Add(NewNamelessXboxDownload(isActive: true));
            await seed.SaveChangesAsync();
        }

        var resolved = await NewMappingService(options).ResolveDownloadsAsync();

        // The Rust ingest path owns an active row; naming it here would split the in-flight download.
        Assert.Equal(0, resolved);

        await using var db = new AppDbContext(options);
        var download = Assert.Single(await db.Downloads.ToListAsync());
        Assert.Null(download.GameName);
    }

    // -----------------------------------------------------------------------------------------
    // The detection cache: a game with no Steam id, no Epic id and no name fails the named test,
    // takes the Steam arm, and claims the GameAppId 0 slot - overwriting an unrelated App-0 row.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveGamesAsync_BlankNamedGameDoesNotOverwriteTheAppZeroRow()
    {
        var options = NewInMemoryOptions();
        var service = NewDataService(options);

        await service.SaveGamesAsync(
            new List<GameCacheInfo>
            {
                NewGame(0, "steam", "Steam App 0"),
                NewGame(0, "xbox", string.Empty)
            },
            incremental: false);

        await using var db = new AppDbContext(options);
        var row = Assert.Single(await db.CachedGameDetections.ToListAsync());

        // The blank Xbox row is dropped. Kept, it would take the same GameAppId 0 slot and flip
        // this row's service and sizes to its own.
        Assert.Equal("Steam App 0", row.GameName);
        Assert.Equal("steam", row.Service);
    }

    [Fact]
    public async Task SaveGamesAsync_NamedXboxGameStillPersists()
    {
        var options = NewInMemoryOptions();
        var service = NewDataService(options);

        await service.SaveGamesAsync(
            new List<GameCacheInfo> { NewGame(0, "xbox", "Halo Infinite") },
            incremental: false);

        await using var db = new AppDbContext(options);
        var row = Assert.Single(await db.CachedGameDetections.ToListAsync());
        Assert.Equal("Halo Infinite", row.GameName);
        Assert.Equal("xbox", row.Service);
    }

    private static GameCacheInfo NewGame(long appId, string service, string gameName)
        => new()
        {
            GameAppId = appId,
            GameName = gameName,
            Service = service,
            CacheFilesFound = 1,
            TotalSizeBytes = 1024,
            CacheFilePaths = new List<string> { $"/cache/{service}/{gameName}" },
            Datasources = new List<string> { "default" }
        };
}
