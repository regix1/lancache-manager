using System.Collections.Concurrent;
using System.Net;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace LancacheManager.Tests;

// The fixture holds the image fetch's process-wide execution lock for each test's lifetime.
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class DepotImportTests
{
    [Theory]
    [InlineData(RunTrigger.Manual)]
    [InlineData(RunTrigger.Scheduled)]
    public async Task SnapshotCombinesWithPopulatedCatalogAndCommitsBeforeSchedulePublication(RunTrigger trigger)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.Downloads.Add(new Download
            {
                DepotId = 10,
                GameAppId = 1,
                GameName = "Old owner",
                GameImageUrl = "https://example.com/old.jpg"
            });
            await initial.SaveChangesAsync();
        }

        Assert.True(await fixture.Service.ImportFromGitHubAsync(trigger: trigger));

        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.OrderBy(row => row.DepotId).ThenBy(row => row.AppId).ToListAsync();
        // The snapshot adds to what the table held: the old owner of depot 10 stays as a reference and
        // depots the snapshot does not list keep their rows.
        Assert.Equal(new long[] { 1, 2, 3, 4, 9 }, rows.Select(row => row.AppId));
        Assert.Equal("New owner", rows.Single(row => row.IsOwner && row.DepotId == 10).AppName);
        Assert.Single(rows, row => row.IsOwner && row.DepotId == 10);
        Assert.True(rows.Single(row => row.DepotId == 20).IsOwner);
        Assert.Equal("orphan-resolved", rows.Single(row => row.DepotId == 90).Source);
        var download = await db.Downloads.SingleAsync();
        Assert.Equal(2, download.GameAppId);
        Assert.Equal("New owner", download.GameName);
        Assert.Equal(2u, Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners")[10]);
        Assert.True(Get<ConcurrentDictionary<uint, HashSet<uint>>>(fixture.Service, "_depotToAppMappings").ContainsKey(20));
        Assert.True(Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames").ContainsKey(1));
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.True(fixture.State.GetState().HasDataLoaded);
        Assert.True(fixture.State.GetLastPicsCrawl() > fixture.PreviousCrawl);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastFullPicsCrawl());
        Assert.Null(fixture.State.GetState().LastViabilityCheck);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.Contains(fixture.Events.Snapshots, item => item.Event == "SchedulesUpdated"
            && item.Content.ToString().Contains("\"PendingFullScan\":null", StringComparison.Ordinal));
        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        Assert.True(terminal.Content.GetProperty("Success").GetBoolean());
        Assert.False(terminal.Content.GetProperty("Canceled").GetBoolean());
        // An import passed no notice builds one from the service mode and its own trigger.
        var notice = fixture.Tracker.GetOperation(terminal.Content.GetProperty("OperationId").GetGuid())!.Notice!;
        Assert.Equal(NotificationMode.Manual, notice.Mode);
        Assert.Equal(trigger, notice.Trigger);
        using var published = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.MappingFile));
        Assert.Equal("retained", published.RootElement.GetProperty("futureField").GetString());
        Assert.Equal("2.8", published.RootElement.GetProperty("metadata").GetProperty("version").GetString());
    }

    [Fact]
    public async Task AGitHubImportKeepsTheDepotsPrefillMapped()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 77,
                DepotId = 770,
                ManifestId = 1,
                AppName = "Prefill only game",
                CachedAtUtc = DateTime.UtcNow
            });
            initial.SteamDepotMappings.Add(new SteamDepotMapping
            {
                DepotId = 770,
                AppId = 77,
                AppName = "Prefill only game",
                IsOwner = true,
                Source = "Prefill",
                DiscoveredAt = fixture.PreviousCrawl
            });
            await initial.SaveChangesAsync();
        }

        Assert.True(await fixture.Service.ImportFromGitHubAsync());

        await using var db = fixture.Database.Factory.CreateDbContext();
        var restored = await db.SteamDepotMappings.SingleAsync(row => row.DepotId == 770);
        Assert.Equal(77, restored.AppId);
        Assert.Equal("Prefill only game", restored.AppName);
        Assert.Equal("Prefill", restored.Source);
    }

    [Fact]
    public async Task AGitHubImportCanceledDuringTheRestoreKeepsTheDepotsPrefillMapped()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 77,
                DepotId = 770,
                ManifestId = 1,
                AppName = "Prefill only game",
                CachedAtUtc = DateTime.UtcNow
            });
            initial.SteamDepotMappings.Add(new SteamDepotMapping
            {
                DepotId = 770,
                AppId = 77,
                AppName = "Prefill only game",
                IsOwner = true,
                Source = "Prefill",
                DiscoveredAt = fixture.PreviousCrawl
            });
            await initial.SaveChangesAsync();
        }

        // The user's Cancel lands while the restore reads what prefill cached.
        using var cancellation = new CancellationTokenSource();
        fixture.CancelOnPrefillRead = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.ImportFromGitHubAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(cancellation.IsCancellationRequested);
        await using var db = fixture.Database.Factory.CreateDbContext();
        var restored = Assert.Single(await db.SteamDepotMappings.Where(row => row.DepotId == 770).ToListAsync());
        Assert.Equal(77, restored.AppId);
        Assert.Equal("Prefill only game", restored.AppName);
        Assert.Equal("Prefill", restored.Source);
    }

    [Fact]
    public async Task AGitHubImportKeepsTheSnapshotOwnerOfAPrefillDepot()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.PrefillCachedDepots.AddRange(
                new PrefillCachedDepot
                {
                    AppId = 55,
                    DepotId = 10,
                    ManifestId = 1,
                    AppName = "Older prefill name",
                    CachedAtUtc = DateTime.UtcNow.AddDays(-2)
                },
                new PrefillCachedDepot
                {
                    AppId = 55,
                    DepotId = 10,
                    ManifestId = 2,
                    AppName = "Newer prefill name",
                    CachedAtUtc = DateTime.UtcNow.AddDays(-1)
                });
            await initial.SaveChangesAsync();
        }

        Assert.True(await fixture.Service.ImportFromGitHubAsync());

        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.Where(row => row.DepotId == 10).ToListAsync();
        Assert.Equal(2, rows.Single(row => row.IsOwner).AppId);
        var prefill = rows.Single(row => row.AppId == 55);
        Assert.False(prefill.IsOwner);
        Assert.Equal("Newer prefill name", prefill.AppName);
    }

    [Fact]
    public async Task AGitHubImportWhosePrefillRestoreFailsEndsFailedButKeepsTheImport()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.PrefillCachedDepots.AddRange(
                new PrefillCachedDepot { AppId = 77, DepotId = 770, ManifestId = 1, AppName = "First prefill game", CachedAtUtc = DateTime.UtcNow },
                new PrefillCachedDepot { AppId = 78, DepotId = 780, ManifestId = 1, AppName = "Rejected prefill game", CachedAtUtc = DateTime.UtcNow },
                new PrefillCachedDepot { AppId = 79, DepotId = 790, ManifestId = 1, AppName = "Third prefill game", CachedAtUtc = DateTime.UtcNow });
            initial.Downloads.Add(new Download { DepotId = 10 });
            await initial.SaveChangesAsync();
            await initial.Database.ExecuteSqlRawAsync("ALTER TABLE \"SteamDepotMappings\" ADD CONSTRAINT reject_depot CHECK (\"DepotId\" <> 780)");
        }

        Assert.False(await fixture.Service.ImportFromGitHubAsync());

        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        Assert.False(terminal.Content.GetProperty("Success").GetBoolean());
        Assert.Equal("signalr.depotMapping.github.prefillNamesNotRestored", terminal.Content.GetProperty("StageKey").GetString());
        // The card words the failure from the key above; an error would show ahead of it, so none is sent.
        Assert.Null(terminal.Content.GetProperty("Error").GetString());
        var operation = fixture.Tracker.GetOperation(terminal.Content.GetProperty("OperationId").GetGuid())!;
        Assert.Equal("signalr.depotMapping.github.prefillNamesNotRestored", operation.Message);
        Assert.Equal("signalr.depotMapping.github.prefillNamesNotRestored", operation.FailureReason!.StageKey);
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.True(fixture.State.GetLastPicsCrawl() > fixture.PreviousCrawl);
        using (var published = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.MappingFile)))
        {
            Assert.Equal(200u, published.RootElement.GetProperty("metadata").GetProperty("lastChangeNumber").GetUInt32());
        }
        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.ToListAsync();
        Assert.Equal(new long[] { 1, 2, 3 }, rows.Where(row => row.DepotId == 10).Select(row => row.AppId).Order());
        Assert.Equal(4, Assert.Single(rows, row => row.DepotId == 20).AppId);
        Assert.Equal(77, Assert.Single(rows, row => row.DepotId == 770).AppId);
        Assert.Equal(79, Assert.Single(rows, row => row.DepotId == 790).AppId);
        Assert.DoesNotContain(rows, row => row.DepotId == 780);
        Assert.Equal("New owner", (await db.Downloads.SingleAsync()).GameName);
    }

    [Fact]
    public async Task AGitHubImportCommitsThePrefillOnlyMappingsWithTheImport()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 77,
                DepotId = 770,
                ManifestId = 1,
                AppName = "Prefill only game",
                CachedAtUtc = DateTime.UtcNow
            });
            initial.SteamDepotMappings.Add(new SteamDepotMapping
            {
                DepotId = 770,
                AppId = 77,
                AppName = "Prefill only game",
                IsOwner = true,
                Source = "Prefill",
                DiscoveredAt = fixture.PreviousCrawl
            });
            await initial.SaveChangesAsync();
        }

        // A detection that reads the mappings right after the import commits must still find the
        // prefill-only depot. The import's commit is the first one after which the snapshot's pair
        // (10, 2) exists.
        List<SteamDepotMapping>? atCommit = null;
        fixture.AfterCommit = () =>
        {
            if (atCommit != null) return;
            using var reader = fixture.Database.Factory.CreateDbContext();
            if (!reader.SteamDepotMappings.Any(row => row.DepotId == 10 && row.AppId == 2)) return;
            atCommit = reader.SteamDepotMappings.AsNoTracking().Where(row => row.DepotId == 770).ToList();
        };

        Assert.True(await fixture.Service.ImportFromGitHubAsync());

        Assert.NotNull(atCommit);
        var committed = Assert.Single(atCommit);
        Assert.Equal(77, committed.AppId);
        Assert.True(committed.IsOwner);
        await using var db = fixture.Database.Factory.CreateDbContext();
        var restored = Assert.Single(await db.SteamDepotMappings.Where(row => row.DepotId == 770).ToListAsync());
        Assert.Equal(77, restored.AppId);
        Assert.True(restored.IsOwner);
    }

    [Fact]
    public async Task ASteamCrawlImportGivesAnOwnerlessPrefillDepotItsOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 77,
                DepotId = 770,
                ManifestId = 1,
                AppName = "Prefill only game",
                CachedAtUtc = DateTime.UtcNow
            });
            await initial.SaveChangesAsync();
        }

        await (Task)Invoke(fixture.Service, "ImportJsonToDatabaseAsync", CancellationToken.None)!;

        await using var db = fixture.Database.Factory.CreateDbContext();
        var restored = await db.SteamDepotMappings.SingleAsync(row => row.DepotId == 770);
        Assert.Equal(77, restored.AppId);
        Assert.True(restored.IsOwner);
    }

    [Fact]
    public async Task GitHubThenFullCrawlThenGitHubKeepsEveryPairOnceAndEverySourcesDepots()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.Downloads.AddRange(
                new Download { DepotId = 20, GameAppId = 4, GameName = "Removed app", GameImageUrl = "https://example.com/removed.jpg" },
                new Download { DepotId = 30 });
            await initial.SaveChangesAsync();
        }

        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        // The crawl sees depot 10 as the file does and finds depot 30, which the file lacks.
        await FinishFullCrawlAsync(fixture, 210,
            (10, 2, new uint[] { 2, 3 }, "New owner"),
            (30, 5, new uint[] { 5 }, "Crawl only game"));
        fixture.Source = Snapshot(210);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());

        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.AsNoTracking().ToListAsync();
        Assert.All(rows.GroupBy(row => (row.DepotId, row.AppId)), pair => Assert.Single(pair));
        Assert.Equal(
            new (long, long)[] { (10, 1), (10, 2), (10, 3), (20, 4), (30, 5), (90, 9) },
            rows.Select(row => (row.DepotId, row.AppId)).Order());
        Assert.All(rows.GroupBy(row => row.DepotId), depot => Assert.Single(depot, row => row.IsOwner));
        var downloads = await db.Downloads.AsNoTracking().ToListAsync();
        var crawlOnly = downloads.Single(download => download.DepotId == 30);
        Assert.Equal(5, crawlOnly.GameAppId);
        Assert.Equal("Crawl only game", crawlOnly.GameName);
        var seeded = downloads.Single(download => download.DepotId == 20);
        Assert.Equal(4, seeded.GameAppId);
        Assert.Equal("Removed app", seeded.GameName);
        Assert.Equal("https://example.com/removed.jpg", seeded.GameImageUrl);
    }

    [Fact]
    public async Task AnOwnerChangedByAnotherSourceLeavesOneOwnerAndRenamesTheDownload()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.Downloads.Add(new Download { DepotId = 10, GameAppId = 1, GameName = "Old owner" });
            await initial.SaveChangesAsync();
        }

        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        // The crawl names app 7 as the owner of depot 10 and does not list app 2 for it.
        await FinishFullCrawlAsync(fixture, 210, (10, 7, new uint[] { 7 }, "Crawl owner"));

        await using (var afterCrawl = fixture.Database.Factory.CreateDbContext())
        {
            var depot = await afterCrawl.SteamDepotMappings.AsNoTracking().Where(row => row.DepotId == 10).ToListAsync();
            Assert.Equal(7, Assert.Single(depot, row => row.IsOwner).AppId);
            var download = await afterCrawl.Downloads.AsNoTracking().SingleAsync();
            Assert.Equal(7, download.GameAppId);
            Assert.Equal("Crawl owner", download.GameName);
        }

        fixture.Source = Snapshot(210);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());

        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.AsNoTracking().Where(row => row.DepotId == 10).ToListAsync();
        Assert.Equal(new long[] { 1, 2, 3, 7 }, rows.Select(row => row.AppId).Order());
        Assert.Equal(2, Assert.Single(rows, row => row.IsOwner).AppId);
        var renamed = await db.Downloads.AsNoTracking().SingleAsync();
        Assert.Equal(2, renamed.GameAppId);
        Assert.Equal("New owner", renamed.GameName);
    }

    [Fact]
    public async Task AnIncrementalCrawlKeepsTheTablesOwnerOverAStaleMemoryOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        // Memory still holds the owner an earlier crawl found, as when the import's reload was canceled.
        Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners")[10] = 7;
        Get<ConcurrentDictionary<uint, HashSet<uint>>>(fixture.Service, "_depotToAppMappings")[10].Add(7);
        Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames")[7] = "Stale owner";

        await (Task)Invoke(fixture.Service, "LoadDepotMappingsAsync", false, CancellationToken.None)!;
        Set(fixture.Service, "_lastChangeNumberSeen", 210u);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
        await reporter.StartAsync();
        await (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", true, reporter.Token)!;

        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.AsNoTracking().Where(row => row.DepotId == 10).ToListAsync();
        Assert.Equal(2, Assert.Single(rows, row => row.IsOwner).AppId);
    }

    [Fact]
    public async Task AGitHubImportWhoseStoreLookupFailsKeepsTheDownloadsNameAndImage()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.Downloads.Add(new Download
            {
                DepotId = 10, GameAppId = 2, GameName = "New owner", GameImageUrl = "https://example.com/stored.jpg"
            });
            await initial.SaveChangesAsync();
        }
        fixture.StoreAnswer = "{}";
        fixture.Source = Snapshot(200);

        Assert.True(await fixture.Service.ImportFromGitHubAsync());

        await using var db = fixture.Database.Factory.CreateDbContext();
        var download = await db.Downloads.AsNoTracking().SingleAsync();
        Assert.Equal(2, download.GameAppId);
        Assert.Equal("New owner", download.GameName);
        Assert.Equal("https://example.com/stored.jpg", download.GameImageUrl);
    }

    [Fact]
    public async Task AGitHubImportLeavesADownloadNoMappingNamesAsItWas()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.Downloads.Add(new Download
            {
                DepotId = 555, GameAppId = 555, GameName = "Ingest name", GameImageUrl = "https://example.com/ingest.jpg"
            });
            await initial.SaveChangesAsync();
        }
        fixture.Source = Snapshot(200);

        Assert.True(await fixture.Service.ImportFromGitHubAsync());

        await using var db = fixture.Database.Factory.CreateDbContext();
        var download = await db.Downloads.AsNoTracking().SingleAsync();
        Assert.Equal(555, download.GameAppId);
        Assert.Equal("Ingest name", download.GameName);
        Assert.Equal("https://example.com/ingest.jpg", download.GameImageUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedOrCanceledFullCrawlKeepsEveryDownloadsName(bool cancel)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var initial = fixture.Database.Factory.CreateDbContext())
        {
            initial.Downloads.Add(new Download
            {
                DepotId = 10, GameAppId = 1, GameName = "Old owner", GameImageUrl = "https://example.com/old.jpg"
            });
            await initial.SaveChangesAsync();
        }
        using var run = new CancellationTokenSource();
        if (cancel)
        {
            // The run is canceled while the crawl lists Steam's apps.
            fixture.Source = """{"applist":{"apps":[{"appid":10,"name":"Counter-Strike"}]}}""";
            fixture.Download = _ =>
            {
                run.Cancel();
                return Task.FromResult(fixture.Source);
            };
            Set(fixture.Service, "_steamApps", new SteamKit2.SteamClient().GetHandler<SteamKit2.SteamApps>()!);
        }

        // Without a Steam session the listing fails; with one, the cancel above stops it.
        var preparing = (Task)Invoke(fixture.Service, "PrepareForScanAsync", run.Token, false)!;
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);

        await using var db = fixture.Database.Factory.CreateDbContext();
        var download = await db.Downloads.AsNoTracking().SingleAsync();
        Assert.Equal(1, download.GameAppId);
        Assert.Equal("Old owner", download.GameName);
        Assert.Equal("https://example.com/old.jpg", download.GameImageUrl);
    }

    // Ends a full crawl that found the given depots, the way the crawl leaves memory before it saves:
    // the four maps hold only what it found.
    private static async Task FinishFullCrawlAsync(
        Fixture fixture, uint changeNumber, params (uint DepotId, uint OwnerId, uint[] AppIds, string OwnerName)[] found)
    {
        var mappings = Get<ConcurrentDictionary<uint, HashSet<uint>>>(fixture.Service, "_depotToAppMappings");
        var owners = Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners");
        var appNames = Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames");
        mappings.Clear();
        owners.Clear();
        appNames.Clear();
        Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_depotNames").Clear();
        foreach (var depot in found)
        {
            mappings[depot.DepotId] = new HashSet<uint>(depot.AppIds);
            owners[depot.DepotId] = depot.OwnerId;
            appNames[depot.OwnerId] = depot.OwnerName;
        }
        Set(fixture.Service, "_lastChangeNumberSeen", changeNumber);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
        await reporter.StartAsync();
        await (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", false, reporter.Token)!;
    }

    [Fact]
    public async Task AGitHubImportStartedFromTheDepotPageUnderHiddenGivesAHiddenRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        fixture.Service.SetNotificationMode(NotificationMode.Hidden);
        var controller = NewController(fixture);

        await controller.ImportDepotMappingsAsync("github", CancellationToken.None);

        var run = Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationType == "depotMapping");
        Assert.Equal("completed", run.Status);
        Assert.Equal(RunVisibility.Hidden, run.Visibility);
    }

    /// <summary>
    /// The rebuild and the GitHub import check conflicts and start under the queue's lock, so a cache
    /// clear sent at the same moment cannot start beside them. A conflict the queue finds is answered
    /// 409 as before, and the service is never asked to start.
    /// </summary>
    [Fact]
    public async Task ARebuildAndAGitHubImportThatMeetARunningClearAnswer409AndStartNothingAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        var conflict = ClearConflict();
        var queue = new RecordingQueue { Conflict = conflict };
        fixture.Queue = queue;
        var controller = NewController(fixture);

        var rebuild = Assert.IsType<ConflictObjectResult>(await controller.StartDepotRebuildAsync(CancellationToken.None));
        var import = await controller.ImportDepotMappingsAsync("github", CancellationToken.None);

        Assert.Same(conflict, rebuild.Value);
        Assert.Same(conflict, Assert.IsType<ConflictObjectResult>(import.Result).Value);
        Assert.Equal([OperationType.DepotMapping, OperationType.DepotMapping], queue.Gated);
        Assert.Empty(fixture.Tracker.GetRuns().Runs);
        Assert.False(fixture.Service.IsRebuildRunning);
    }

    /// <summary>
    /// An import from the depot file on disk sent while a database reset runs is refused with the
    /// reset as the conflict, and writes no mapping row beside it.
    /// </summary>
    [Fact]
    public async Task ALocalImportSentDuringADatabaseResetAnswers409AndWritesNothingAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var reset = fixture.Tracker.RegisterOperation(OperationType.DatabaseReset, "Database reset", new CancellationTokenSource());
        var controller = NewController(fixture);

        var import = await controller.ImportDepotMappingsAsync("local", CancellationToken.None);

        var conflict = Assert.IsType<OperationConflictResponse>(Assert.IsType<ConflictObjectResult>(import.Result).Value);
        Assert.Equal(reset, conflict.ActiveOperationId);
        Assert.False(fixture.Service.IsRebuildRunning);
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Equal(new long[] { 1, 4, 9 }, await db.SteamDepotMappings.OrderBy(row => row.AppId).Select(row => row.AppId).ToArrayAsync());
    }

    /// <summary>
    /// An import from the depot file on disk starts through the queue's gated start and runs as one
    /// depot run, so it draws a card and ends Completed once its rows are written.
    /// </summary>
    [Fact]
    public async Task ALocalImportRegistersOneDepotRunThatEndsCompletedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var queue = new RecordingQueue();
        fixture.Queue = queue;
        var controller = NewController(fixture);

        var import = await controller.ImportDepotMappingsAsync("local", CancellationToken.None);

        Assert.IsType<OkObjectResult>(import.Result);
        Assert.Equal([OperationType.DepotMapping], queue.Gated);
        var run = Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationType == "depotMapping");
        Assert.Equal("completed", run.Status);
        Assert.Contains(fixture.Events.Snapshots, sent => sent.Event == "DepotMappingStarted");
        Assert.False(fixture.Service.IsRebuildRunning);
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Contains(2L, await db.SteamDepotMappings.Select(row => row.AppId).ToArrayAsync());
    }

    /// <summary>
    /// The published depot file names an app it has no name for "App {id}". That exact placeholder is
    /// stored as no name, while a real title that only starts with "App " is stored as it is.
    /// </summary>
    [Fact]
    public async Task AnImportedAppPlaceholderIsStoredUnnamedAndARealNameIsKeptAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Pics.WritePicsJsonFile("""
            {"metadata":{"lastChangeNumber":100,"version":"2.8","lastUpdated":"2026-01-01T00:00:00Z"},
             "depotMappings":{"30":{"ownerId":7,"appIds":[7,8],"appNames":["App 7","App Builder"],"discoveredAt":"2026-01-01T00:00:00Z"}}}
            """);

        await fixture.Pics.ImportToDatabaseAsync();

        await using var db = fixture.Database.Factory.CreateDbContext();
        var rows = await db.SteamDepotMappings.Where(row => row.DepotId == 30).OrderBy(row => row.AppId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].AppName);
        Assert.Equal("App Builder", rows[1].AppName);
    }

    /// <summary>
    /// The same placeholder read from the depot file into the in-memory names is not taken as a name,
    /// so it never reaches the rows written from those names.
    /// </summary>
    [Fact]
    public async Task AnAppPlaceholderInTheDepotFileIsNotTakenAsAnAppNameAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var names = Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames");
        names.Clear();

        Invoke(fixture.Service, "MergeDepotMappingsFromJson", new PicsJsonData
        {
            DepotMappings = new Dictionary<string, PicsDepotMapping>
            {
                ["30"] = new() { AppIds = [7, 8], AppNames = ["App 7", "App Builder"] }
            }
        });

        Assert.False(names.ContainsKey(7));
        Assert.Equal("App Builder", names[8]);
    }

    /// <summary>
    /// An import that has no name for a pair keeps the app and depot names another source stored, so
    /// after a restart a new download of that game is still named from the table.
    /// </summary>
    [Fact]
    public async Task AnImportWithNoNameKeepsTheStoredNamesAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var seed = fixture.Database.Factory.CreateDbContext())
        {
            await seed.SteamDepotMappings.Where(row => row.DepotId == 10 && row.AppId == 1)
                .ExecuteUpdateAsync(row => row.SetProperty(mapping => mapping.DepotName, "Old owner Content"));
        }

        await fixture.Pics.ImportToDatabaseAsync(new PicsJsonData
        {
            Metadata = new PicsMetadata { LastChangeNumber = 200 },
            DepotMappings = new Dictionary<string, PicsDepotMapping>
            {
                ["10"] = new() { OwnerId = 1, AppIds = [1], AppNames = ["App 1"], DiscoveredAt = fixture.PreviousCrawl }
            }
        }, CancellationToken.None);

        long id;
        await using (var db = fixture.Database.Factory.CreateDbContext())
        {
            var row = await db.SteamDepotMappings.SingleAsync(mapping => mapping.DepotId == 10 && mapping.AppId == 1);
            Assert.Equal("Old owner", row.AppName);
            Assert.Equal("Old owner Content", row.DepotName);
            var download = new Download { Service = "steam", DepotId = 10, IsActive = false };
            db.Downloads.Add(download);
            await db.SaveChangesAsync();
            id = download.Id;
        }

        // A restart starts with no names in memory and reads them from the table; the store does not
        // answer for app 1, so the download's name can only come from the stored row.
        Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames").Clear();
        Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_depotNames").Clear();
        await fixture.Service.ManuallyApplyDepotMappingsAsync();

        Assert.Equal("Old owner", Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames")[1]);
        await using var after = fixture.Database.Factory.CreateDbContext();
        Assert.Equal("Old owner", (await after.Downloads.SingleAsync(download => download.Id == id)).GameName);
    }

    /// <summary>
    /// A real title that starts with "App " names the download when the store lookup fails; only the
    /// exact "App {id}" placeholder is refused.
    /// </summary>
    [Fact]
    public async Task APicsTitleStartingWithAppNamesTheDownloadWhenTheStoreFailsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        long id;
        await using (var seed = fixture.Database.Factory.CreateDbContext())
        {
            // Depot 10 belongs to app 1, which the store does not answer for.
            await seed.SteamDepotMappings.Where(row => row.AppId == 1)
                .ExecuteUpdateAsync(row => row.SetProperty(mapping => mapping.AppName, "App Builder"));
            var download = new Download { Service = "steam", DepotId = 10, IsActive = false };
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
            id = download.Id;
        }

        await fixture.Service.ManuallyApplyDepotMappingsAsync();

        await using var db = fixture.Database.Factory.CreateDbContext();
        var named = await db.Downloads.SingleAsync(download => download.Id == id);
        Assert.Equal(1, named.GameAppId);
        Assert.Equal("App Builder", named.GameName);
    }

    /// <summary>
    /// The depot schedule's tick, in crawl and in GitHub mode, starts its run inside the queue's gated
    /// start. Blocked by a cache clear, it waits on the queue's card instead and starts no run now.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ADepotTickStartsThroughTheQueueAndWaitsWhenBlockedAsync(bool githubMode, bool blocked)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        var queue = new RecordingQueue { Conflict = blocked ? ClearConflict() : null };
        fixture.Queue = queue;
        Set(fixture.Service, "_initialized", true);
        Set(fixture.Service, "_isRunning", true);
        Set(fixture.Service, "_crawlIncrementalMode", githubMode ? "github" : (object)false);
        // A crawl fails at its started event, so it never connects to Steam; it is registered by then.
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (!githubMode && name == "DepotMappingStarted") throw new IOException("Stopped before connecting");
        };

        await ((Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(OperationType.DepotMapping, queue.Gated[0]);
        if (blocked)
        {
            Assert.Equal(OperationType.DepotMapping, queue.Parked);
            Assert.Equal(ConflictScope.Bulk(), queue.ParkedScope);
            Assert.Empty(fixture.Tracker.GetRuns().Runs);
        }
        else
        {
            Assert.Null(queue.Parked);
            Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationType == "depotMapping");
        }
    }

    /// <summary>
    /// A full crawl asked for while an incremental one waits does not join it: it waits on its own card and
    /// runs after. The same crawl asked again joins the card already waiting.
    /// </summary>
    [Fact]
    public async Task AFullCrawlDoesNotJoinAWaitingIncrementalCrawlAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var clearing = fixture.Tracker.RegisterOperation(
            OperationType.CacheClearing, "Cache Clear (All)", new CancellationTokenSource());
        static RunNotice Click() => new(NotificationMode.Manual, RunTrigger.Manual);

        Assert.False(await fixture.Service.StartRebuildThroughQueueAsync(true, Click(), CancellationToken.None));
        Assert.False(await fixture.Service.StartRebuildThroughQueueAsync(true, Click(), CancellationToken.None));
        Assert.False(await fixture.Service.StartRebuildThroughQueueAsync(false, Click(), CancellationToken.None));

        var waiting = fixture.Tracker.GetWaitingOperations().Where(operation => operation.Type == OperationType.DepotMapping).ToList();
        Assert.Equal(2, waiting.Count);

        foreach (var operation in waiting)
            fixture.Tracker.CancelOperation(operation.Id);
        fixture.Tracker.CompleteOperation(clearing, success: true);
    }

    /// <summary>
    /// A full crawl started from the Depots page answers a full crawl asked for while it runs (a sign-in that
    /// starts a crawl, a schedule tick): no second crawl waits to run after it. An incremental crawl is other
    /// work and still waits.
    /// </summary>
    [Fact]
    public async Task ACrawlAskedDuringAPageCrawlOfTheSameModeJoinsItAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        // The crawl waits for a Steam session here, so it stays registered until the test cancels it.
        var session = Get<SemaphoreSlim>(fixture.Service, "_sessionGate");
        await session.WaitAsync();
        static RunNotice Click() => new(NotificationMode.Manual, RunTrigger.Manual);

        try
        {
            var accepted = Assert.IsType<AcceptedResult>(
                await NewController(fixture).StartDepotRebuildAsync(CancellationToken.None, incremental: false));
            Assert.True(Assert.IsType<DepotRebuildStartResponse>(accepted.Value).Started);

            Assert.False(await fixture.Service.StartRebuildThroughQueueAsync(false, Click(), CancellationToken.None));
            Assert.DoesNotContain(fixture.Tracker.GetWaitingOperations(), operation => operation.Type == OperationType.DepotMapping);
            Assert.False(await fixture.Service.StartRebuildThroughQueueAsync(true, Click(), CancellationToken.None));
            Assert.Single(fixture.Tracker.GetWaitingOperations(), operation => operation.Type == OperationType.DepotMapping);
        }
        finally
        {
            foreach (var waiting in fixture.Tracker.GetWaitingOperations().ToList())
                fixture.Tracker.CancelOperation(waiting.Id);
            await fixture.Service.CancelRebuildAsync();
            await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));
            session.Release();
        }
    }

    /// <summary>
    /// A GitHub import started from the Depots page answers a GitHub import the schedule asks for while it
    /// downloads: the tick waits on no card of its own.
    /// </summary>
    [Fact]
    public async Task AGitHubTickDuringAPageImportJoinsItAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Download = async _ =>
        {
            downloading.TrySetResult();
            await release.Task;
            return fixture.Source;
        };

        var import = NewController(fixture).ImportDepotMappingsAsync("github", CancellationToken.None);
        try
        {
            await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var running = Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.DepotMapping));

            // The request the GitHub tick sends when it finds the queue busy after its own running check.
            var tick = await fixture.Queue.EnqueueAsync(
                OperationType.DepotMapping, ConflictScope.Bulk(), "Depot Mapping",
                () => Task.FromResult<Guid?>(null), CancellationToken.None, new DepotMappingRequestKey(DepotMappingWork.GitHubImport));

            Assert.Equal(OperationStartStatus.AlreadyRunning, tick.Status);
            Assert.Equal(running.Id, tick.OperationId);
            Assert.DoesNotContain(fixture.Tracker.GetWaitingOperations(), operation => operation.Type == OperationType.DepotMapping);
        }
        finally
        {
            foreach (var waiting in fixture.Tracker.GetWaitingOperations().ToList())
                fixture.Tracker.CancelOperation(waiting.Id);
            release.TrySetResult();
        }
        Assert.IsType<OkObjectResult>((await import.WaitAsync(TimeSpan.FromSeconds(10))).Result);
    }

    /// <summary>
    /// A full crawl asked for while a full crawl started the same way runs (a sign-in that starts a crawl
    /// during a scheduled one) answers with that crawl: no second crawl waits to run after it.
    /// </summary>
    [Fact]
    public async Task AFullCrawlAskedDuringAFullCrawlJoinsItAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        // The crawl waits for a Steam session here, so it stays registered until the test cancels it.
        var session = Get<SemaphoreSlim>(fixture.Service, "_sessionGate");
        await session.WaitAsync();
        static RunNotice Click() => new(NotificationMode.Manual, RunTrigger.Manual);

        try
        {
            Assert.True(await fixture.Service.StartRebuildThroughQueueAsync(false, Click(), CancellationToken.None));
            var running = Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.DepotMapping));

            Assert.False(await fixture.Service.StartRebuildThroughQueueAsync(false, Click(), CancellationToken.None));

            Assert.DoesNotContain(fixture.Tracker.GetWaitingOperations(), operation => operation.Type == OperationType.DepotMapping);
            Assert.Equal(running.Id, Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.DepotMapping)).Id);
        }
        finally
        {
            foreach (var waiting in fixture.Tracker.GetWaitingOperations().ToList())
                fixture.Tracker.CancelOperation(waiting.Id);
            await fixture.Service.CancelRebuildAsync();
            await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));
            session.Release();
        }
    }

    /// <summary>
    /// A GitHub tick the queue finds busy, whose blocker ends before the queue parks it, starts its import at
    /// once. The tick then ends only when that import ends, as an unblocked tick does, so the schedule does
    /// not stamp the run finished while the import still downloads.
    /// </summary>
    [Fact]
    public async Task AGitHubTickStartedByTheQueueRecheckEndsWithItsImportAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Queue = new RecordingQueue { Conflict = ClearConflict(), StartWhenParked = true };
        Set(fixture.Service, "_initialized", true);
        Set(fixture.Service, "_isRunning", true);
        Set(fixture.Service, "_crawlIncrementalMode", "github");
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Download = async _ =>
        {
            downloading.TrySetResult();
            await release.Task;
            return fixture.Source;
        };

        var tick = (Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!;
        try
        {
            await downloading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAny(tick, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.False(tick.IsCompleted);
            Assert.True(fixture.Service.IsRebuildRunning);
        }
        finally
        {
            release.TrySetResult();
        }

        await tick.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Service.IsRebuildRunning);
        Assert.Equal("completed", Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationType == "depotMapping").Status);
    }

    /// <summary>
    /// A crawl whose started event cannot be sent ends before it does any work, so neither door reports it
    /// as started: the sign-in and schedule door answers false, and the Depots page answers Started false.
    /// </summary>
    [Fact]
    public async Task ACrawlWhoseStartFailsIsNotReportedStartedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (name == "DepotMappingStarted") throw new IOException("Stopped before connecting");
        };

        Assert.False(await fixture.Service.StartRebuildThroughQueueAsync(
            false, new RunNotice(NotificationMode.Manual, RunTrigger.Manual), CancellationToken.None));
        await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));

        var accepted = Assert.IsType<AcceptedResult>(
            await NewController(fixture).StartDepotRebuildAsync(CancellationToken.None, incremental: false));
        await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Assert.IsType<DepotRebuildStartResponse>(accepted.Value).Started);
    }

    /// <summary>
    /// A crawl started from the Depots page keeps running after the request that started it ends: the
    /// browser closing the connection after the 202 does not cancel it.
    /// </summary>
    [Fact]
    public async Task APageCrawlOutlivesItsRequestAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        // The crawl waits for a Steam session here, so it stays registered until the test cancels it.
        var session = Get<SemaphoreSlim>(fixture.Service, "_sessionGate");
        await session.WaitAsync();
        using var request = new CancellationTokenSource();

        try
        {
            var accepted = Assert.IsType<AcceptedResult>(
                await NewController(fixture).StartDepotRebuildAsync(request.Token, incremental: false));
            Assert.True(Assert.IsType<DepotRebuildStartResponse>(accepted.Value).Started);
            var crawl = Get<Task>(fixture.Service, "_currentBuildTask");

            request.Cancel();
            await Task.WhenAny(crawl, Task.Delay(TimeSpan.FromSeconds(1)));

            Assert.False(crawl.IsCompleted);
            Assert.Single(fixture.Tracker.GetActiveOperations(OperationType.DepotMapping));
        }
        finally
        {
            await fixture.Service.CancelRebuildAsync();
            await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));
            session.Release();
        }
    }

    /// <summary>
    /// An import of the depot file on disk is not labeled as a GitHub download, and Cancel stops it even
    /// after an earlier crawl committed its baseline.
    /// </summary>
    [Fact]
    public async Task ALocalImportIsNotLabeledGitHubAndCanBeCanceledAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        // As a finished crawl leaves it.
        Set(fixture.Service, "_baselineCommitted", true);
        Task<bool>? cancelAnswer = null;
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (name == "DepotMappingStarted") cancelAnswer = fixture.Service.CancelRebuildAsync();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ImportFromLocalFileAsync(
            CancellationToken.None, new RunNotice(NotificationMode.Manual, RunTrigger.Manual), new TaskCompletionSource<Guid?>()))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(cancelAnswer);
        Assert.True(await cancelAnswer);
        Assert.Equal("canceled", Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationType == "depotMapping").Status);
        var started = Assert.Single(fixture.Events.Snapshots, sent => sent.Event == "DepotMappingStarted").Content;
        var scanMode = started.EnumerateObject().Single(property => property.NameEquals("scanMode") || property.NameEquals("ScanMode")).Value;
        Assert.Equal(JsonSerializer.SerializeToElement(DepotScanMode.Incremental).ToString(), scanMode.ToString());
    }

    /// <summary>
    /// An owner app PICS returns with no name and no depots is fetched once per crawl: a later app whose
    /// depot names the same owner does not queue it again.
    /// </summary>
    [Fact]
    public async Task ANamelessOwnerWithNoDepotsIsFetchedOncePerCrawlAsync()
    {
        await using var fixture = await Fixture.CreateAsync();

        List<uint> Process(uint id, string keyValues) =>
            (List<uint>)Invoke(fixture.Service, "ProcessAppDepots", PicsApp(id, keyValues))!;

        Assert.Equal([500u], Process(100, """ "appinfo" { "common" { "name" "Game A" } "depots" { "101" { "depotfromapp" "500" } } } """));
        Assert.Empty(Process(500, """ "appinfo" { "common" { "type" "tool" } } """));
        Assert.Empty(Process(200, """ "appinfo" { "common" { "name" "Game B" } "depots" { "201" { "depotfromapp" "500" } } } """));
    }

    private static SteamKit2.SteamApps.PICSProductInfoCallback.PICSProductInfo PicsApp(uint id, string keyValues)
    {
        var app = (SteamKit2.SteamApps.PICSProductInfoCallback.PICSProductInfo)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(SteamKit2.SteamApps.PICSProductInfoCallback.PICSProductInfo));
        // SteamKit2 builds this only from a Steam response, so the test sets its auto-property fields.
        CacheScanGateHarness.SetField(app, "<ID>k__BackingField", id);
        CacheScanGateHarness.SetField(app, "<KeyValues>k__BackingField", SteamKit2.KeyValue.LoadFromString(keyValues)!);
        return app;
    }

    private static DepotsController NewController(Fixture fixture) =>
        new(fixture.Service, fixture.Pics, fixture.State,
            NullLogger<DepotsController>.Instance,
            OperationConflictTestServices.Create(fixture.Tracker, NullLogger<OperationConflictChecker>.Instance),
            fixture.Queue);

    private static OperationConflictResponse ClearConflict() => new()
    {
        StageKey = "errors.conflict.globalOperationActive",
        Error = "blocked",
        ActiveOperationId = Guid.NewGuid(),
        ActiveOperationType = nameof(OperationType.CacheClearing)
    };

    /// <summary>
    /// Answers every gated start with <see cref="Conflict"/> when one is set, else runs the start, and
    /// records each gated type and the park it is then asked for.
    /// </summary>
    private sealed class RecordingQueue : IOperationWaitlist
    {
        public OperationConflictResponse? Conflict { get; init; }

        /// <summary>Answers a park as the queue's re-check does when the blocker ended meanwhile: it starts at once.</summary>
        public bool StartWhenParked { get; init; }
        public List<OperationType> Gated { get; } = [];
        public OperationType? Parked { get; private set; }
        public ConflictScope? ParkedScope { get; private set; }

        public async Task<OperationStartResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            OperationRequestKey requestKey,
            bool reportRefusal = false,
            RunNotice? notice = null,
            string? target = null)
        {
            if (StartWhenParked && await start() is { } startedId)
            {
                return new OperationStartResponse
                {
                    OperationId = startedId,
                    Status = OperationStartStatus.Started
                };
            }

            Parked = type;
            ParkedScope = scope;
            return new OperationStartResponse
            {
                OperationId = Guid.NewGuid(),
                Status = OperationStartStatus.Waiting
            };
        }

        public async Task<OperationConflictResponse?> StartUnlessBusyAsync(
            OperationType type, ConflictScope scope, Func<Task<Guid?>> start, CancellationToken ct, OperationRequestKey requestKey)
        {
            Gated.Add(type);
            if (Conflict != null)
            {
                return Conflict;
            }

            await start();
            return null;
        }
    }

    [Fact]
    public async Task AScheduledPicsRebuildRegistersTheNoticeItWasAdmittedWith()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admitted = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);

        await StartRebuildAndStopBeforeConnectingAsync(fixture,
            () => fixture.Service.TryStartRebuild(trigger: RunTrigger.Scheduled, notice: admitted));

        Assert.Same(admitted, SingleRunNotice(fixture));
    }

    [Theory]
    [InlineData(NotificationMode.Manual)]
    [InlineData(NotificationMode.Hidden)]
    public async Task ARestRebuildWithNoNoticeRegistersAManualNoticeInTheServiceMode(NotificationMode mode)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Service.SetNotificationMode(mode);

        await StartRebuildAndStopBeforeConnectingAsync(fixture, () => fixture.Service.TryStartRebuild());

        var notice = SingleRunNotice(fixture);
        Assert.Equal(mode, notice.Mode);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
    }

    [Fact]
    public async Task AScheduledGitHubImportRegistersTheNoticeItWasAdmittedWith()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        var admitted = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);

        Assert.True(await fixture.Service.ImportFromGitHubAsync(CancellationToken.None, RunTrigger.Scheduled, admitted));

        Assert.Same(admitted, SingleRunNotice(fixture));
    }

    [Fact]
    public async Task ApplyingMappingsRegistersAManualNoticeInTheServiceMode()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Service.SetNotificationMode(NotificationMode.Hidden);

        await fixture.Service.ManuallyApplyDepotMappingsAsync();

        var notice = SingleRunNotice(fixture);
        Assert.Equal(NotificationMode.Hidden, notice.Mode);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
    }

    [Fact]
    public async Task ASkippedDepotRunRegistersTheNoticeItWasAdmittedWith()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admitted = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);
        fixture.Service.SelectRunNotice(admitted);

        await (Task)Invoke(fixture.Service, "ReportRunSkippedAsync",
            "signalr.depotMapping.scan.skippedFullRequired", CancellationToken.None)!;

        Assert.Equal("skipped", Assert.Single(fixture.Tracker.GetRuns().Runs).Status);
        Assert.Same(admitted, SingleRunNotice(fixture));
    }

    [Fact]
    public async Task AFailedDepotRunRegistersTheNoticeItWasAdmittedWith()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admitted = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);
        fixture.Service.SelectRunNotice(admitted);

        await (Task)Invoke(fixture.Service, "ReportRunFailedAsync", "Steam is unreachable", CancellationToken.None)!;

        Assert.Equal("failed", Assert.Single(fixture.Tracker.GetRuns().Runs).Status);
        Assert.Same(admitted, SingleRunNotice(fixture));
    }

    [Fact]
    public async Task ADepotRunThatCouldNotReachSteamEndsFailedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        fixture.CheckChanges = (_, _) => throw new IOException("Steam is unreachable");
        Set(fixture.Service, "_initialized", true);
        Set(fixture.Service, "_isRunning", true);
        Set(fixture.Service, "_crawlIncrementalMode", true);

        await (Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!;

        var run = Assert.Single(fixture.Tracker.GetRuns().Runs);
        Assert.Equal("failed", run.Status);
        Assert.Equal("signalr.depotMapping.skippedSteamUnreachable", run.Error);
        Assert.Contains(fixture.Events.Snapshots, snapshot =>
            snapshot.Content.ValueKind == JsonValueKind.Object
            && snapshot.Content.TryGetProperty("StageKey", out var stageKey)
            && stageKey.GetString() == "signalr.depotMapping.skippedSteamUnreachable");
    }

    // The setup wizard runs the first depot mapping itself, so an automatic tick before setup is
    // finished waits without a card instead of reporting a run that could not start.
    [Fact]
    public async Task AScheduledTickBeforeSetupIsFinishedMakesNoRunAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.SetupCompleted = false);
        Set(fixture.Service, "_initialized", true);
        Set(fixture.Service, "_isRunning", true);

        await (Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!;

        Assert.Empty(fixture.Tracker.GetRuns().Runs);
    }

    [Fact]
    public async Task AViabilityCheckSteamDidNotAnswerReadsAsUnreachableAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        fixture.CheckChanges = (_, _) => throw new TimeoutException("Steam did not answer the PICS request within 5 minutes");

        var result = await fixture.Service.CheckViabilityAsync(CancellationToken.None);

        Assert.False(result.WillTriggerFullScan);
        Assert.False(result.IsLargeGap);
        Assert.Equal("errors.steam.connectionFailed", result.StageKey);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public async Task AViabilityCheckThatFailsWhileACrawlRunsLeavesItsSessionAloneAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        Set(fixture.Service, "_rebuildActive", 1);
        Set(fixture.Service, "_picsCrawlRunning", true);
        Set(fixture.Service, "_isLoggedOn", true);
        fixture.CheckChanges = (_, _) => throw new InvalidOperationException("The depot database is not available");

        var result = await fixture.Service.CheckViabilityAsync(CancellationToken.None);

        Assert.Equal("errors.steam.connectionFailed", result.StageKey);
        Assert.True(Get<bool>(fixture.Service, "_isLoggedOn"));
    }

    [Fact]
    public async Task AViabilityCheckThatFailsDuringAGitHubImportResetsTheConnectionFlagAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        Set(fixture.Service, "_rebuildActive", 1);
        Set(fixture.Service, "_isLoggedOn", true);
        fixture.CheckChanges = (_, _) => throw new TimeoutException("Steam did not answer the PICS request within 5 minutes");

        var result = await fixture.Service.CheckViabilityAsync(CancellationToken.None);

        Assert.Equal("errors.steam.connectionFailed", result.StageKey);
        Assert.False(Get<bool>(fixture.Service, "_isLoggedOn"));
    }

    [Fact]
    public async Task AViabilityCheckThatFailsDuringASteamSignInLeavesItsSessionAloneAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        Set(fixture.Service, "_loginActive", 1);
        Set(fixture.Service, "_isLoggedOn", true);
        fixture.CheckChanges = (_, _) => throw new InvalidOperationException("The depot database is not available");

        var result = await fixture.Service.CheckViabilityAsync(CancellationToken.None);

        Assert.Equal("errors.steam.connectionFailed", result.StageKey);
        Assert.True(Get<bool>(fixture.Service, "_isLoggedOn"));
    }

    [Fact]
    public async Task ADepotScanCanceledDuringItsWebApiListingStopsAsACancelAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Source = """{"applist":{"apps":[{"appid":10,"name":"Counter-Strike"}]}}""";
        using var run = new CancellationTokenSource();
        fixture.Download = _ =>
        {
            run.Cancel();
            return Task.FromResult(fixture.Source);
        };
        Set(fixture.Service, "_steamApps", new SteamKit2.SteamClient().GetHandler<SteamKit2.SteamApps>()!);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => (Task)Invoke(fixture.Service, "GetAppIdsViaPicsAsync", run.Token, false)!);
    }

    [Fact]
    public async Task ACanceledCrawlEndsOnItsCancelKeyWithNoSentenceOfItsOwnAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        // The crawl's run is registered by its started event, so a cancel there ends a real run.
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (name == "DepotMappingStarted") throw new OperationCanceledException();
        };

        Assert.True(fixture.Service.TryStartRebuild());
        await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));

        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        Assert.True(terminal.Content.GetProperty("Canceled").GetBoolean());
        Assert.Equal("signalr.depotMapping.canceled", terminal.Content.GetProperty("StageKey").GetString());
        Assert.False(terminal.Content.TryGetProperty("Message", out _));
        Assert.False(terminal.Content.GetProperty("Context").TryGetProperty("message", out _));
    }

    /// <summary>
    /// A crawl that ends before it reaches the depot scan (canceled or failed while it connects) leaves its
    /// own end state on the progress answer, never the phase an earlier run finished with: the browser reads
    /// a polled "completed" as a finished crawl.
    /// </summary>
    [Theory]
    [InlineData(true, DepotScanPhase.Canceled)]
    [InlineData(false, DepotScanPhase.Error)]
    public async Task ACrawlThatEndsBeforeTheScanReportsItsOwnEndStateAsync(bool canceled, DepotScanPhase expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        Set(fixture.Service, "_currentStatus", DepotScanPhase.Completed);
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (name != "DepotMappingStarted") return;
            if (canceled) throw new OperationCanceledException();
            throw new InvalidOperationException("Steam is unreachable");
        };

        Assert.True(fixture.Service.TryStartRebuild());
        await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));

        var progress = fixture.Service.GetProgress();
        Assert.False(progress.IsProcessing);
        Assert.Equal(expected, progress.Status);
    }

    /// <summary>
    /// A depot run's events name each step by stage key, and the browser builds the sentence from it, so
    /// no event carries an English sentence of its own beside the key.
    /// </summary>
    [Theory]
    [InlineData("github")]
    [InlineData("local")]
    [InlineData("apply")]
    [InlineData("crawl")]
    public async Task EveryDepotRunEventCarriesItsStageKeyAndNoSentenceAsync(string run)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        switch (run)
        {
            case "github":
                Assert.True(await fixture.Service.ImportFromGitHubAsync());
                break;
            case "local":
                fixture.Queue = new RecordingQueue();
                Assert.IsType<OkObjectResult>(
                    (await NewController(fixture).ImportDepotMappingsAsync("local", CancellationToken.None)).Result);
                break;
            case "apply":
                await fixture.Service.ManuallyApplyDepotMappingsAsync();
                break;
            case "crawl":
                Set(fixture.Service, "_lastChangeNumberSeen", 210u);
                using (var source = new CancellationTokenSource())
                {
                    await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
                        new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
                    await reporter.StartAsync();
                    await (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", true, reporter.Token)!;
                }
                break;
        }

        var sent = fixture.Events.Snapshots.Where(item => item.Event.StartsWith("DepotMapping", StringComparison.Ordinal)).ToList();
        Assert.Contains(sent, item => item.Event == "DepotMappingStarted");
        Assert.Contains(sent, item => item.Event == "DepotMappingComplete");
        Assert.All(sent, item =>
        {
            Assert.StartsWith("signalr.depotMapping.", item.Content.GetProperty("StageKey").GetString());
            Assert.False(item.Content.TryGetProperty("Message", out _), $"{item.Event} carries a Message");
            var context = item.Content.GetProperty("Context");
            Assert.False(
                context.ValueKind == JsonValueKind.Object && context.TryGetProperty("message", out _),
                $"{item.Event} carries a context message");
            Assert.False(
                context.ValueKind == JsonValueKind.Object && context.TryGetProperty("status", out _),
                $"{item.Event} carries a context status");
            if (item.Event == "DepotMappingProgress")
                Assert.Equal("running", item.Content.GetProperty("Status").GetString());
        });
    }

    // The crawl fails at its started event, so it never connects to Steam; its operation is already
    // registered by then.
    private static async Task StartRebuildAndStopBeforeConnectingAsync(Fixture fixture, Func<bool> start)
    {
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (name == "DepotMappingStarted") throw new IOException("Stopped before connecting");
        };
        Assert.True(start());
        await Get<Task>(fixture.Service, "_currentBuildTask").WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static RunNotice SingleRunNotice(Fixture fixture) =>
        fixture.Tracker.GetOperation(Assert.Single(fixture.Tracker.GetRuns().Runs).OperationId)!.Notice!;

    /// <summary>
    /// A crawl that lost Steam mid-batch says so by its own stage key, carrying the attempt numbers its
    /// sentence names, so every screen can say the scan is reconnecting.
    /// </summary>
    [Fact]
    public async Task AReconnectDuringACrawlIsNamedByItsOwnStageKeyAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
        await reporter.StartAsync();
        var send = typeof(SteamKit2Service).GetMethod("SendDepotMappingProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var arguments = send.GetParameters()
            .Select(parameter => parameter.Name switch
            {
                "isReconnecting" => (object?)true,
                "reconnectAttempt" => 2,
                _ => "Reconnecting to Steam..."
            })
            .ToArray();

        await (Task)send.Invoke(fixture.Service, arguments)!;

        var progress = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingProgress").Content;
        Assert.Equal("signalr.depotMapping.reconnecting", progress.GetProperty("StageKey").GetString());
        Assert.Equal(2, progress.GetProperty("Context").GetProperty("reconnectAttempt").GetInt32());
        Assert.Equal(3, progress.GetProperty("Context").GetProperty("maxReconnectAttempts").GetInt32());
    }

    [Fact]
    public async Task EqualCursorCanBeImportedAgainAndOlderCursorCannotReplaceIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        var completed = fixture.State.GetLastPicsCrawl();
        fixture.Source = Snapshot(199);
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(completed, fixture.State.GetLastPicsCrawl());
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Equal(5, await db.SteamDepotMappings.CountAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":0},\"depotMappings\":{\"10\":{\"appIds\":[2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":4294967296},\"depotMappings\":{\"10\":{\"appIds\":[2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"bad\":{\"appIds\":[2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[0]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[2,2]}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[2],\"ownerId\":3}}}")]
    [InlineData("{\"metadata\":{\"lastChangeNumber\":200},\"depotMappings\":{\"10\":{\"appIds\":[2],\"appNames\":[]}}}")]
    public async Task InvalidSnapshotDoesNotMutateCatalogOrCheckpoint(string source)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = source;
        var before = await File.ReadAllTextAsync(fixture.MappingFile);

        Assert.False(await fixture.Service.ImportFromGitHubAsync());

        Assert.Equal(before, await File.ReadAllTextAsync(fixture.MappingFile));
        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task ADownloadThatIsNotJsonEndsWithItsOwnStageKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = "not json";

        Assert.False(await fixture.Service.ImportFromGitHubAsync());

        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        Assert.False(terminal.Content.GetProperty("Success").GetBoolean());
        Assert.Equal("signalr.depotMapping.github.invalidJson", terminal.Content.GetProperty("StageKey").GetString());
        Assert.Null(terminal.Content.GetProperty("Error").GetString());
        var run = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationType == "depotMapping");
        Assert.Equal("signalr.depotMapping.github.invalidJson", run.Message);
        Assert.Equal("signalr.depotMapping.github.invalidJson", run.FailureReason!.StageKey);
    }

    [Fact]
    public async Task AnHttpFailureEndsWithItsKeyAndTheStatusItsSentenceNames()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.StatusCode = HttpStatusCode.InternalServerError;

        Assert.False(await fixture.Service.ImportFromGitHubAsync());

        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        // The key travels as the stage key with the status as its value; no error shows ahead of it.
        Assert.Equal("signalr.depotMapping.github.httpFailed", terminal.Content.GetProperty("StageKey").GetString());
        Assert.Equal("500", terminal.Content.GetProperty("Context").GetProperty("errorDetail").ToString());
        Assert.Null(terminal.Content.GetProperty("Error").GetString());
        var run = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationType == "depotMapping");
        Assert.Equal("signalr.depotMapping.github.httpFailed", run.Message);
        Assert.Equal("signalr.depotMapping.github.httpFailed", run.FailureReason!.StageKey);
        Assert.Equal("500", run.FailureReason.Context["errorDetail"]);
    }

    [Fact]
    public async Task DatabaseCancellationRollsBackInsertedBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Pics.WritePicsJsonFile(Snapshot(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Pics.ImportToDatabaseAsync(
            cancellation.Token, progress =>
            {
                if (progress == 95) cancellation.Cancel();
                return Task.CompletedTask;
            }));

        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task DatabaseErrorRollsBackOnPopulatedTable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await using (var db = fixture.Database.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"SteamDepotMappings\" ADD CONSTRAINT reject_app CHECK (\"AppId\" <> 2)");
        }
        fixture.Source = Snapshot(200);

        Assert.False(await fixture.Service.ImportFromGitHubAsync());

        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task StateSaveFailureKeepsCheckpointAndRestartRefusesPublishedCursorUntilRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        var temporaryState = fixture.StateFile + ".tmp";
        Directory.CreateDirectory(temporaryState);

        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.NotNull(fixture.Service.PendingFullScan);
        Assert.Equal(200u, (await fixture.Pics.LoadFromJsonAsync())!.Metadata!.LastChangeNumber);
        var persisted = JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(fixture.StateFile))!;
        Assert.Equal(100u, persisted.LastPicsChangeNumber);
        var baseline = await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync");
        Assert.Equal(100u, baseline.LastChangeNumber);
        Assert.False(baseline.HasUsableBaseline);

        Directory.Delete(temporaryState);
        var restartedState = StateTestMethods.CreateStateService(fixture.Root);
        Assert.Equal(100u, restartedState.GetState().LastPicsChangeNumber);
        using (var restarted = fixture.Restart())
        {
            var restartedBaseline = await InvokeAsync<SteamKit2Service.DepotBaseline>(restarted, "GetDepotBaselineAsync");
            Assert.Equal(100u, restartedBaseline.LastChangeNumber);
            Assert.False(restartedBaseline.HasUsableBaseline);
            await (Task)Invoke(restarted, "LoadPicsMetadataAsync")!;
            Assert.Equal(fixture.PreviousCrawl, Get<DateTime>(restarted, "_lastCrawlTime"));
        }
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
    }

    [Fact]
    public async Task DuplicateAdmissionDoesNotClaimSuccessAndCancellationDoesNotStampFreshness()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Download = async token =>
        {
            arrived.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Snapshot(200);
        };
        using var cancellation = new CancellationTokenSource();
        var import = fixture.Service.ImportFromGitHubAsync(cancellation.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ConflictException>(() => fixture.Service.ImportFromGitHubAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        await fixture.AssertPreviousAsync();
        Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        fixture.Download = null;
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
    }

    [Fact]
    public async Task LegacyCursorIsPinnedBeforeFailedImportAndZeroNeverFallsBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastPicsChangeNumber = null);
        fixture.Source = Snapshot(200);
        fixture.Events.BeforeSend = (name, content) =>
        {
            if (name == "DepotMappingProgress"
                && content.GetProperty("StageKey").GetString() == "signalr.depotMapping.applyingToDownloads")
                throw new IOException("Application unavailable");
        };
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        fixture.State.UpdateState(state => state.LastPicsChangeNumber = 0);
        fixture.Pics.WritePicsJsonFile(Snapshot(200));
        var baseline = await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync");
        Assert.Equal(0u, baseline.LastChangeNumber);
        Assert.False(baseline.HasUsableBaseline);
    }

    [Fact]
    public async Task CommittedBaselineRequiresBothCatalogsAndMatchingCursor()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        Assert.True((await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync")).HasUsableBaseline);
        await using (var db = fixture.Database.Factory.CreateDbContext())
            await db.SteamDepotMappings.ExecuteDeleteAsync();
        Assert.False((await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync")).HasUsableBaseline);
        await fixture.SeedAsync();
        File.Delete(fixture.MappingFile);
        Assert.False((await InvokeAsync<SteamKit2Service.DepotBaseline>(fixture.Service, "GetDepotBaselineAsync")).HasUsableBaseline);
    }

    [Fact]
    public async Task CompletionRejectsAnOldViabilityWriteAndOnlyFullCrawlStampsFullTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        var version = Get<long>(fixture.Service, "_baselineVersion");
        fixture.Service.UpdateLastCrawlTime(200);
        Assert.False((bool)Invoke(fixture.Service, "CacheViabilityResult", true, 100u, 99u, version)!);
        Assert.Null(fixture.State.GetState().LastViabilityCheck);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastFullPicsCrawl());
        fixture.Service.UpdateLastCrawlTime(201, fullScan: true);
        Assert.True(fixture.State.GetLastFullPicsCrawl() > fixture.PreviousCrawl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededViabilityResponseCannotRestoreTheOldRequirement(bool scheduled)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedCursors = new ConcurrentQueue<uint>();
        fixture.CheckChanges = async (cursor, token) =>
        {
            checkedCursors.Enqueue(cursor);
            if (cursor == 100)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return (300u, true);
            }
            return (300u, false);
        };
        Set(fixture.Service, "_initialized", true);
        Set(fixture.Service, "_isRunning", true);
        Set(fixture.Service, "_crawlIncrementalMode", true);
        Task<IncrementalViabilityCheck>? check = scheduled ? null : fixture.Service.CheckViabilityAsync(CancellationToken.None);
        var pending = scheduled
            ? (Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!
            : check!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        if (check != null)
        {
            Assert.True((await check).IsViable);
            Assert.Equal(200u, (await check).LastChangeNumber);
        }
        Assert.Equal(new uint[] { 100, 200 }, checkedCursors);
        Assert.False(fixture.State.GetState().RequiresFullScan);
        Assert.Equal(200u, fixture.State.GetState().LastViabilityCheckChangeNumber);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.False(fixture.Service.IsRebuildRunning);
    }

    [Fact]
    public async Task RestartUsesCommittedCursorAndLocalCompletionTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        using var restarted = fixture.Restart();
        await (Task)Invoke(restarted, "LoadPicsMetadataAsync")!;
        Assert.Equal(fixture.State.GetLastPicsCrawl(), Get<DateTime>(restarted, "_lastCrawlTime"));
        var result = await restarted.CheckViabilityAsync(CancellationToken.None);
        Assert.Equal(200u, result.LastChangeNumber);
        Assert.True(result.IsViable);
    }

    [Fact]
    public async Task OlderViabilityCompletionPreservesANewerFullScanRequirement()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastViabilityCheck = null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var logger = new CapturingLogger<SteamKit2Service>();
        logger.OnLogged = entry =>
        {
            if (!entry.Message.StartsWith("Cached viability check result in state.json", StringComparison.Ordinal)) return;
            logger.OnLogged = null;
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Set(fixture.Service, "_logger", logger);
        var olderCheck = Task.Run(() => fixture.Service.CheckViabilityAsync(CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(await fixture.Service.ImportFromGitHubAsync());
            fixture.CheckChanges = (cursor, _) => Task.FromResult((cursor + 500000, true));
            Set(fixture.Service, "_initialized", true);
            Set(fixture.Service, "_isRunning", true);
            Set(fixture.Service, "_crawlIncrementalMode", true);
            await (Task)Invoke(fixture.Service, "ExecuteWorkAsync", CancellationToken.None)!;
            Assert.NotNull(fixture.Service.PendingFullScan);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await olderCheck).IsViable);
        Assert.Equal(200u, fixture.State.GetState().LastViabilityCheckChangeNumber);
        Assert.True(fixture.State.GetState().RequiresFullScan);
        Assert.NotNull(fixture.Service.PendingFullScan);
    }

    [Fact]
    public async Task RestartReusesFreshViabilityForTheSameCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state =>
        {
            state.RequiresFullScan = false;
            state.LastViabilityCheck = DateTime.UtcNow;
            state.LastViabilityCheckChangeNumber = 100;
            state.ViabilityChangeGap = 2;
        });
        var queries = 0;
        fixture.CheckChanges = (cursor, _) =>
        {
            queries++;
            return Task.FromResult((cursor + 2, false));
        };

        using var restarted = fixture.Restart();
        await (Task)Invoke(restarted, "InitializeAsync", CancellationToken.None)!;
        var result = await restarted.CheckViabilityAsync(CancellationToken.None);

        Assert.Equal(0, queries);
        Assert.True(result.IsViable);
        Assert.False(result.WillTriggerFullScan);
        Assert.Equal(2u, result.ChangeGap);
        Assert.Null(restarted.PendingFullScan);
    }

    [Fact]
    public async Task CancellationAfterDatabaseCommitDoesNotCommitFreshness()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        using var cancellation = new CancellationTokenSource();
        fixture.Events.BeforeSend = (name, content) =>
        {
            // The import's last progress line: every earlier GitHub download step reports at most 90 percent.
            if (name == "DepotMappingProgress"
                && content.GetProperty("StageKey").GetString() == "signalr.depotMapping.github.downloading"
                && content.GetProperty("PercentComplete").GetDouble() > 90) cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ImportFromGitHubAsync(cancellation.Token));
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.NotNull(fixture.Service.PendingFullScan);
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Contains(await db.SteamDepotMappings.ToListAsync(), row => row.AppId == 2);
    }

    [Fact]
    public async Task ReloadFailurePreservesLiveMappingsAndRetryNeedsNoReset()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        fixture.Events.BeforeSend = (name, content) =>
        {
            if (name == "DepotMappingProgress"
                && content.GetProperty("StageKey").GetString() == "signalr.depotMapping.applyingToDownloads") fixture.FailReload = true;
        };
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(1u, Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners")[10]);
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.NotNull(fixture.Service.PendingFullScan);
        fixture.Events.BeforeSend = null;
        fixture.FailReload = false;
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(2u, Get<ConcurrentDictionary<uint, uint>>(fixture.Service, "_depotOwners")[10]);
    }

    [Fact]
    public async Task FilePublicationFailureDoesNotAdvanceCheckpoint()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        fixture.FailPublication = true;
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.NotNull(fixture.Service.PendingFullScan);
        fixture.FailPublication = false;
        Assert.True(await fixture.Service.ImportFromGitHubAsync());
    }

    [Fact]
    public async Task LateCancellationCannotChangeCommittedImportTerminal()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Source = Snapshot(200);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Events.BeforeSend = (name, _) =>
        {
            if (name != "DepotMappingComplete") return;
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        using var cancellation = new CancellationTokenSource();
        var import = Task.Run(() => fixture.Service.ImportFromGitHubAsync(cancellation.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Null(fixture.Service.PendingFullScan);
        Assert.False(await fixture.Service.CancelRebuildAsync());
        cancellation.Cancel();
        release.Set();
        Assert.True(await import);
        var terminal = Assert.Single(fixture.Events.Snapshots, item => item.Event == "DepotMappingComplete");
        Assert.True(terminal.Content.GetProperty("Success").GetBoolean());
        Assert.False(terminal.Content.GetProperty("Canceled").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizationCommitsTheCandidateAndOnlyFullScanSetsFullTime(bool incremental)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        Assert.False(fixture.State.GetState().HasDataLoaded);
        Set(fixture.Service, "_lastChangeNumberSeen", 200u);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
        await reporter.StartAsync();
        await (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", incremental, reporter.Token)!;
        Assert.Equal(200u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.True(fixture.State.GetState().HasDataLoaded);
        Assert.True(fixture.State.GetLastPicsCrawl() > fixture.PreviousCrawl);
        Assert.Equal(!incremental, fixture.State.GetLastFullPicsCrawl() > fixture.PreviousCrawl);
        Assert.Null(fixture.Service.PendingFullScan);
    }

    [Fact]
    public async Task APicsRunWithAFailedBatchEndsAmberAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        Set(fixture.Service, "_lastChangeNumberSeen", 200u);
        // The crawl counts a batch that threw a non-fatal error and carried on; two of five here.
        Set(fixture.Service, "_failedPicsBatches", 2);
        Set(fixture.Service, "_totalBatches", 5);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
        await reporter.StartAsync();

        await (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", false, reporter.Token)!;

        var run = Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationId == reporter.OperationId);
        Assert.Equal("completed", run.Status);
        var warning = Assert.Single(run.Warnings);
        Assert.Equal("common.notifications.warnings.depotBatchesFailed", warning.StageKey);
        Assert.Equal(2, warning.Context["failed"]);
        Assert.Equal(5, warning.Context["total"]);
    }

    [Fact]
    public async Task APicsRunWhoseOrphanLookupFailedWarnsThatOrphanDepotsWereNotResolvedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        Set(fixture.Service, "_lastChangeNumberSeen", 200u);
        // The crawl counts an orphan batch whose app lookup threw and carried on.
        Set(fixture.Service, "_failedOrphanBatches", 1);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
        await reporter.StartAsync();

        await (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", false, reporter.Token)!;

        var run = Assert.Single(fixture.Tracker.GetRuns().Runs, run => run.OperationId == reporter.OperationId);
        Assert.Equal("completed", run.Status);
        var warning = Assert.Single(run.Warnings);
        Assert.Equal("common.notifications.warnings.orphanDepotsNotResolved", warning.StageKey);
        Assert.Equal(1, warning.Context["count"]);
    }

    [Fact]
    public async Task LocalImportAndApplyDoNotClaimFreshness()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        await fixture.Pics.ImportToDatabaseAsync();
        Set(fixture.Service, "_baselineCommitted", true);
        await fixture.Service.ManuallyApplyDepotMappingsAsync();
        Assert.False(Get<bool>(fixture.Service, "_baselineCommitted"));
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.False(fixture.State.GetState().HasDataLoaded);
        Assert.NotNull(fixture.Service.PendingFullScan);
    }

    [Fact]
    public async Task ApplyingMappingsSavesRowsAfterTwoConsecutiveDeletions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        long[] ids;
        await using (var seed = fixture.Database.Factory.CreateDbContext())
        {
            var downloads = Enumerable.Range(1, 4)
                .Select(index => new Download
                {
                    Service = "steam",
                    ClientIp = $"127.0.0.{index}",
                    StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
                    EndTimeUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUrl = $"https://steam.example.test/depot/{index}",
                    DepotId = 10,
                    IsActive = false
                })
                .ToList();
            seed.Downloads.AddRange(downloads);
            await seed.SaveChangesAsync();
            ids = downloads.Select(download => download.Id).ToArray();
        }

        var step = 0;
        fixture.Recorder.Clear();
        fixture.Recorder.OnExecuting = command =>
        {
            if (step == 0 && command.CommandText.Contains("UPDATE \"Downloads\"", StringComparison.Ordinal))
            {
                using var concurrent = fixture.Database.Factory.CreateDbContext();
                concurrent.Downloads.Where(download => download.Id == ids[1]).ExecuteDelete();
                step = 1;
                return;
            }

            if (step == 1
                && command.CommandText.Contains("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Downloads\"", StringComparison.Ordinal))
            {
                step = 2;
                return;
            }

            if (step == 2 && command.CommandText.Contains("UPDATE \"Downloads\"", StringComparison.Ordinal))
            {
                using var concurrent = fixture.Database.Factory.CreateDbContext();
                concurrent.Downloads.Where(download => download.Id == ids[2]).ExecuteDelete();
                step = 3;
            }
        };

        await fixture.Service.ManuallyApplyDepotMappingsAsync();

        Assert.Equal(3, step);
        await using var db = fixture.Database.Factory.CreateDbContext();
        var saved = await db.Downloads.OrderBy(download => download.Id).ToListAsync();
        Assert.Equal(new[] { ids[0], ids[3] }, saved.Select(download => download.Id).ToArray());
        Assert.All(saved, download => Assert.Equal("Old owner", download.GameName));
        Assert.Equal(2, Get<int>(fixture.Service, "_emitDownloadsUpdated"));
    }

    [Fact]
    public async Task AnAppWhoseNameLookupFailedIsStoredUnnamedAndNamedByALaterPass()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        long id;
        await using (var seed = fixture.Database.Factory.CreateDbContext())
        {
            var download = new Download { Service = "steam", DepotId = 10, IsActive = false };
            seed.Downloads.Add(download);
            await seed.SaveChangesAsync();
            id = download.Id;
            // Depot 10 belongs to app 1, which the store does not answer for. "App 1" is the PICS
            // placeholder, so neither PICS nor the depot carries a name for it.
            await seed.SteamDepotMappings.Where(row => row.AppId == 1)
                .ExecuteUpdateAsync(row => row.SetProperty(mapping => mapping.AppName, "App 1"));
        }
        Get<ConcurrentDictionary<uint, string>>(fixture.Service, "_appNames").TryRemove(1u, out _);

        await fixture.Service.ManuallyApplyDepotMappingsAsync();

        await using (var db = fixture.Database.Factory.CreateDbContext())
        {
            var unnamed = await db.Downloads.SingleAsync(download => download.Id == id);
            Assert.Equal(1, unnamed.GameAppId);
            Assert.Null(unnamed.GameName);
        }

        fixture.StoreAnswer = """{"1":{"success":true,"data":{"name":"Named later","header_image":"https://example.com/1.jpg"}}}""";
        // The store skips an app for an hour after a failed lookup; the second pass stands for a later one.
        Get<ConcurrentDictionary<long, DateTime>>(Get<SteamService>(fixture.Service, "_steamService"), "_lookupRetryAtUtc").Clear();

        await fixture.Service.ManuallyApplyDepotMappingsAsync();

        await using var after = fixture.Database.Factory.CreateDbContext();
        Assert.Equal("Named later", (await after.Downloads.SingleAsync(download => download.Id == id)).GameName);
    }

    [Fact]
    public async Task HttpFailurePreservesThePreviousCatalog()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.StatusCode = HttpStatusCode.BadGateway;
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        await fixture.AssertPreviousAsync();
    }

    [Fact]
    public async Task FailedLegacyPinPreventsCatalogMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.LastPicsChangeNumber = null);
        Directory.CreateDirectory(fixture.StateFile + ".tmp");
        Assert.False(await fixture.Service.ImportFromGitHubAsync());
        Assert.Null(fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        await using var db = fixture.Database.Factory.CreateDbContext();
        Assert.Equal(new long[] { 1, 4, 9 }, await db.SteamDepotMappings.OrderBy(row => row.AppId).Select(row => row.AppId).ToArrayAsync());
    }

    [Theory]
    [InlineData(true, 100u)]
    [InlineData(false, 100u)]
    [InlineData(true, 99u)]
    [InlineData(false, 99u)]
    public async Task CachedAnswersMustMatchTheCommittedCursor(bool requiresFullScan, uint cachedCursor)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state =>
        {
            state.RequiresFullScan = requiresFullScan;
            state.LastViabilityCheckChangeNumber = cachedCursor;
        });
        var queries = 0;
        fixture.CheckChanges = (cursor, _) =>
        {
            queries++;
            return Task.FromResult((cursor + 10, false));
        };
        var result = await fixture.Service.CheckViabilityAsync(CancellationToken.None);
        Assert.Equal(cachedCursor == 100 ? 0 : 1, queries);
        Assert.Equal(cachedCursor != 100 || !requiresFullScan, result.IsViable);
        if (result.IsViable) Assert.Null(fixture.Service.PendingFullScan);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task FailedOrCanceledFinalizationDoesNotCommitTheCandidate(bool cancel, bool incremental, bool loaded)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.State.UpdateState(state => state.HasDataLoaded = loaded);
        Set(fixture.Service, "_lastChangeNumberSeen", 200u);
        using var source = new CancellationTokenSource();
        await using var reporter = (MappingOperationReporter)Invoke(fixture.Service, "CreateTrackedRebuildReporter", source,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual))!;
        await reporter.StartAsync();
        fixture.Events.BeforeSend = (_, content) =>
        {
            if (!content.ToString().Contains("applyingToDownloads", StringComparison.Ordinal)) return;
            if (cancel) source.Cancel();
            else Directory.CreateDirectory(fixture.StateFile + ".tmp");
        };
        var finalizing = (Task)Invoke(fixture.Service, "FinalizeAndNotifyAsync", incremental, reporter.Token)!;
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finalizing);
        else await Assert.ThrowsAsync<ServiceUnavailableException>(() => finalizing);
        fixture.Events.BeforeSend = null;
        await reporter.CompleteAsync(false, canceled: cancel);
        Assert.Equal(100u, fixture.State.GetState().LastPicsChangeNumber);
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastPicsCrawl());
        Assert.Equal(fixture.PreviousCrawl, fixture.State.GetLastFullPicsCrawl());
        Assert.Equal(loaded, fixture.State.GetState().HasDataLoaded);
        Assert.Equal(loaded, JsonSerializer.Deserialize<AppState>(await File.ReadAllTextAsync(fixture.StateFile))!.HasDataLoaded);
        Assert.NotNull(fixture.Service.PendingFullScan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingJsonDoesNotChangeLoadedState(bool loaded)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync();
        fixture.Pics.WritePicsJsonFile(Snapshot(100).Replace("\"version\"", "\"totalMappings\":2,\"version\"", StringComparison.Ordinal));
        fixture.State.UpdateState(state => state.HasDataLoaded = loaded);
        var before = await File.ReadAllTextAsync(fixture.StateFile);
        Assert.NotNull(await fixture.Pics.LoadFromJsonAsync());
        Assert.Equal(loaded, fixture.State.GetState().HasDataLoaded);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.StateFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CachedAnswerRequiresUsableBaseline(bool requiresFullScan)
    {
        Assert.False(SteamKit2Service.ShouldReuseCachedViability(requiresFullScan, false));
        Assert.True(SteamKit2Service.ShouldReuseCachedViability(requiresFullScan, true));
    }

    private static string Snapshot(uint cursor) => $$$$"""
        {"metadata":{"lastChangeNumber":{{{{cursor}}}},"version":"2.8","lastUpdated":"2026-01-01T00:00:00Z"},
         "futureField":"retained","depotMappings":{"10":{"ownerId":2,"appIds":[2,3],"appNames":["New owner","Shared app"],"discoveredAt":"2026-01-01T00:00:00Z"}}}
        """;

    private static object? Invoke(object instance, string method, params object?[] args)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var member = type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            if (member != null) return member.Invoke(instance, args);
        }
        throw new MissingMethodException(instance.GetType().FullName, method);
    }

    private static async Task<T> InvokeAsync<T>(object instance, string method) =>
        await (Task<T>)Invoke(instance, method)!;

    private static void Set(object instance, string field, object value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var member = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (member == null) continue;
            member.SetValue(instance, value);
            return;
        }
        throw new MissingFieldException(instance.GetType().FullName, field);
    }

    private static T Get<T>(object instance, string field)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var member = type.GetField(field, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (member != null) return (T)member.GetValue(instance)!;
            var property = type.GetProperty(field, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (property != null) return (T)property.GetValue(instance)!;
        }
        throw new MissingFieldException(instance.GetType().FullName, field);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly SteamService _steam;
        private readonly ProcessManager _processes;
        private readonly GameImageFetchService _images = null!;
        private readonly ServiceScheduleRegistry _schedules;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "depot-import-" + Guid.NewGuid().ToString("N"));
        public DateTime PreviousCrawl { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public TestDatabase Database { get; }
        public StateService State { get; }
        public PicsDataService Pics { get; }
        public SteamKit2Service Service { get; }
        public UnifiedOperationTracker Tracker { get; }

        /// <summary>The queue the depot and banner starts resolve; a real one on <see cref="Tracker"/> unless a test swaps it.</summary>
        public IOperationWaitlist Queue { get; set; }
        public RecordingNotifications Events { get; }
        public RecordingCommandInterceptor Recorder { get; } = new();
        public string Source { get; set; } = Snapshot(200);
        public string StoreAnswer { get; set; } = """{"2":{"success":true,"data":{"name":"New owner","header_image":"https://example.com/new.jpg"}}}""";
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public Func<CancellationToken, Task<string>>? Download { get; set; }
        public Func<uint, CancellationToken, Task<(uint CurrentChangeNumber, bool RequiresFullScan)>> CheckChanges { get; set; }
            = (cursor, _) => Task.FromResult((cursor + 10, false));
        public bool FailPublication { get; set; }
        public bool FailReload { get; set; }
        public CancellationTokenSource? CancelOnPrefillRead { get; set; }
        public Action? AfterCommit { get; set; }
        public string MappingFile => Pics.GetPicsJsonFilePath();
        public string StateFile { get; }

        private Fixture(TestDatabase database)
        {
            Database = database;
            Directory.CreateDirectory(Root);
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = Root;
            Directory.CreateDirectory(paths.GetPicsDirectory());
            State = StateTestMethods.CreateStateService(Root);
            StateFile = Path.Combine(paths.GetStateDirectory(), "state.json");
            _processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var tracker = new UnifiedOperationTracker(_processes, NullLogger<UnifiedOperationTracker>.Instance);
            Tracker = tracker;
            Queue = new OperationWaitlistService(
                tracker,
                OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
                NullLogger<OperationWaitlistService>.Instance);
            var registrations = new ServiceCollection();
            var options = new DbContextOptionsBuilder<AppDbContext>(Database.Options)
                .AddInterceptors(new ReadFailure(this), Recorder, new PrefillRead(this), new CommitProbe(this))
                .Options;
            registrations.AddScoped(_ => new AppDbContext(options));
            registrations.AddSingleton<GameImageFetchService>(_ => _images);
            registrations.AddTransient<IOperationWaitlist>(_ => Queue);
            _services = registrations.BuildServiceProvider();
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            Pics = new PicsDataService(
                NullLogger<PicsDataService>.Instance,
                scopes,
                paths,
                State,
                TimeProvider.System,
                (source, destination, overwrite) =>
                {
                    if (FailPublication) throw new IOException("Publication unavailable");
                    File.Move(source, destination, overwrite);
                });
            var clients = new Clients(this);
            _steam = new SteamService(new HttpClient(new StoreHandler(this)), NullLogger<SteamService>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
            Events = (RecordingNotifications)(object)notifications;
            _images = new GameImageFetchService(_services, NullLogger<GameImageFetchService>.Instance,
                new ConfigurationBuilder().Build(), State, notifications,
                DispatchProxy.Create<IImageCacheService, NullReturningProxy>(), tracker);
            Get<SemaphoreSlim>(_images, "_executionLock").Wait();
            var storage = Get<SteamAuthStorageService>(State, "_steamAuthStorage");
            Service = new Probe(this, scopes, _steam, Pics, State, clients,
                notifications, storage, tracker);
            _schedules = new ServiceScheduleRegistry(
                [Service], State, notifications, ScheduleExecutionTestService.Create(), tracker);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var database = await TestDatabase.CreateAsync();
            try
            {
                return new Fixture(database);
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async Task SeedAsync()
        {
            await using var db = Database.Factory.CreateDbContext();
            await db.SteamDepotMappings.ExecuteDeleteAsync();
            db.SteamDepotMappings.AddRange(
                new SteamDepotMapping { DepotId = 10, AppId = 1, AppName = "Old owner", IsOwner = true, DiscoveredAt = PreviousCrawl },
                new SteamDepotMapping { DepotId = 20, AppId = 4, AppName = "Removed app", IsOwner = true, DiscoveredAt = PreviousCrawl },
                new SteamDepotMapping { DepotId = 90, AppId = 9, AppName = "Local orphan", IsOwner = true, Source = "orphan-resolved", DiscoveredAt = PreviousCrawl });
            await db.SaveChangesAsync();
            Pics.WritePicsJsonFile(Snapshot(100));
            State.UpdateState(state =>
            {
                state.SetupCompleted = true;
                state.LastPicsChangeNumber = 100;
                state.LastPicsCrawl = PreviousCrawl;
                state.LastFullPicsCrawl = PreviousCrawl;
                state.RequiresFullScan = true;
                state.LastViabilityCheck = DateTime.UtcNow;
                state.LastViabilityCheckChangeNumber = 100;
                state.ViabilityChangeGap = 99;
            });
            Set(Service, "_automaticScanSkipped", true);
            await (Task)Invoke(Service, "LoadDepotMappingsAsync", false, CancellationToken.None)!;
        }

        public async Task AssertPreviousAsync()
        {
            Assert.False(State.GetState().HasDataLoaded);
            Assert.Equal(100u, State.GetState().LastPicsChangeNumber);
            Assert.Equal(PreviousCrawl, State.GetLastPicsCrawl());
            Assert.NotNull(Service.PendingFullScan);
            await using var db = Database.Factory.CreateDbContext();
            Assert.Equal(new long[] { 1, 4, 9 }, await db.SteamDepotMappings.OrderBy(row => row.AppId).Select(row => row.AppId).ToArrayAsync());
        }

        public SteamKit2Service Restart()
        {
            var state = StateTestMethods.CreateStateService(Root);
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = Root;
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state);
            return new Probe(this, scopes, _steam, pics, state, new Clients(this),
                (ISignalRNotificationService)(object)Events, Get<SteamAuthStorageService>(state, "_steamAuthStorage"),
                new UnifiedOperationTracker(_processes, NullLogger<UnifiedOperationTracker>.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            var handler = typeof(ServiceScheduleRegistry)
                .GetMethod("OnServiceExecutionStateChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<string>>(_schedules);
            ConfigurableScheduledService.ServiceExecutionStateChanged -= handler;
            ScheduledBackgroundService.ServiceExecutionStateChanged -= handler;
            Service.Dispose();
            _steam.Dispose();
            _processes.Dispose();
            Get<SemaphoreSlim>(_images, "_executionLock").Release();
            await _services.DisposeAsync();
            await Database.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }

        private sealed class Clients(Fixture fixture) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(new DownloadHandler(fixture));
        }

        private sealed class ReadFailure(Fixture fixture) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData commandEvent, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                if (fixture.FailReload && command.CommandText.Contains("SteamDepotMappings", StringComparison.Ordinal))
                    throw new IOException("Depot mappings cannot be loaded");
                return base.ReaderExecutingAsync(command, commandEvent, result, cancellationToken);
            }
        }

        private sealed class PrefillRead(Fixture fixture) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData commandEvent, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                if (command.CommandText.Contains("PrefillCachedDepots", StringComparison.Ordinal))
                    fixture.CancelOnPrefillRead?.Cancel();
                return base.ReaderExecutingAsync(command, commandEvent, result, cancellationToken);
            }
        }

        private sealed class CommitProbe(Fixture fixture) : DbTransactionInterceptor
        {
            public override Task TransactionCommittedAsync(
                DbTransaction transaction, TransactionEndEventData transactionEnd, CancellationToken cancellationToken = default)
            {
                fixture.AfterCommit?.Invoke();
                return base.TransactionCommittedAsync(transaction, transactionEnd, cancellationToken);
            }
        }

        private sealed class Probe(
            Fixture fixture, IServiceScopeFactory scopes, SteamService steam, PicsDataService pics, StateService state,
            IHttpClientFactory clients, ISignalRNotificationService notifications, SteamAuthStorageService storage, IUnifiedOperationTracker tracker)
            : SteamKit2Service(NullLogger<SteamKit2Service>.Instance, scopes, steam, pics, state, clients, notifications,
                new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage), storage, tracker,
                new PrefillCacheService(
                    new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(fixture.Database.Options)
                        .AddInterceptors(new PrefillRead(fixture)).Options),
                    NullLogger<PrefillCacheService>.Instance))
        {
            internal override Task<(uint CurrentChangeNumber, bool RequiresFullScan)> CheckChangesAsync(uint changeNumberToCheck, CancellationToken ct)
                => fixture.CheckChanges(changeNumberToCheck, ct);
        }

        private sealed class StoreHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(fixture.StoreAnswer)
                });
        }

        private sealed class DownloadHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => new(fixture.StatusCode)
                {
                    Content = new StringContent(fixture.Download == null ? fixture.Source : await fixture.Download(cancellationToken))
                };
        }
    }

    public class RecordingNotifications : DispatchProxy
    {
        public ConcurrentQueue<(string Event, JsonElement Content)> Snapshots { get; } = new();
        public Action<string, JsonElement>? BeforeSend { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: >= 2 } && args[0] is string name && args[1] is { } content)
            {
                var snapshot = JsonSerializer.SerializeToElement(content);
                BeforeSend?.Invoke(name, snapshot);
                Snapshots.Enqueue((name, snapshot));
            }
            return Task.CompletedTask;
        }
    }
}
