using System.Data.Common;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using LancacheManager.Infrastructure.Data.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using LancacheManager.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Text.Json;

namespace LancacheManager.Tests;

public class PrefillCachedAppTests
{
    [Fact]
    public async Task SteamSnapshot_DistinguishesEmptySnapshotAndAbsentAuthority()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = new AppDbContext(database.Options))
        {
            context.PrefillCachedApps.AddRange(
                new PrefillCachedApp
                {
                    Platform = PrefillPlatform.Steam,
                    AppId = "20",
                    CacheRevision = PrefillCacheService.SteamCacheReceipt,
                    CachedAtUtc = DateTime.UtcNow
                },
                new PrefillCachedApp
                {
                    Platform = PrefillPlatform.Steam,
                    AppId = "30",
                    CacheRevision = "legacy-receipt",
                    CachedAtUtc = DateTime.UtcNow
                });
            context.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 99,
                DepotId = 100,
                ManifestId = 1000,
                CachedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var service = new PrefillCacheService(new TestDbContextFactory(database.Options),
            NullLogger<PrefillCacheService>.Instance);
        await using (var verify = new AppDbContext(database.Options))
        {
            Assert.Equal(PrefillCacheService.SteamCacheReceipt,
                (await verify.PrefillCachedApps.SingleAsync(app => app.AppId == "20")).CacheRevision);
        }
        var snapshot = await service.GetCacheSnapshotAsync([10, 20, 30]);

        Assert.Equal(CacheAuthority.Empty, Assert.Single(snapshot.Scope, app => app.AppId == 10U).Authority);
        Assert.Equal(CacheAuthority.Snapshot, Assert.Single(snapshot.Scope, app => app.AppId == 20U).Authority);
        Assert.Equal(CacheAuthority.Absent, Assert.Single(snapshot.Scope, app => app.AppId == 30U).Authority);
        Assert.Equal(100, Assert.Single(snapshot.Depots).DepotId);
    }

    [Fact]
    public async Task SteamCache_CompleteEvidenceCommitsReceiptAndDepots()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options),
            NullLogger<PrefillCacheService>.Instance);

        var changed = await service.RecordSteamCacheAsync(20, "Game", 30, "user",
        [
            new DepotManifestProgressInfo { DepotId = 200, ManifestId = 2000, TotalBytes = 10 },
            new DepotManifestProgressInfo { DepotId = 100, ManifestId = 1000, TotalBytes = 20 }
        ]);

        Assert.True(changed);
        await using var context = new AppDbContext(database.Options);
        var app = Assert.Single(await context.PrefillCachedApps.ToListAsync());
        Assert.Equal(PrefillCacheService.SteamCacheReceipt, app.CacheRevision);
        Assert.Equal([100L, 200L], await context.PrefillCachedDepots.OrderBy(depot => depot.DepotId)
            .Select(depot => depot.DepotId).ToListAsync());
    }

    [Fact]
    public async Task SteamCache_MissingOrConflictingEvidencePreservesPriorRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillCachedApps.Add(new PrefillCachedApp
            {
                Platform = PrefillPlatform.Steam,
                AppId = "20",
                CacheRevision = "prior",
                CachedAtUtc = DateTime.UtcNow
            });
            seed.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 20,
                DepotId = 100,
                ManifestId = 1000,
                CachedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options),
            NullLogger<PrefillCacheService>.Instance);

        Assert.False(await service.RecordSteamCacheAsync(20, "Game", 1, null, null));
        Assert.False(await service.RecordSteamCacheAsync(20, "Game", 1, null,
        [
            new DepotManifestProgressInfo { DepotId = 100, ManifestId = 1000, TotalBytes = 1 },
            new DepotManifestProgressInfo { DepotId = 100, ManifestId = 2000, TotalBytes = 1 }
        ]));

        await using var context = new AppDbContext(database.Options);
        Assert.Equal("prior", (await context.PrefillCachedApps.SingleAsync()).CacheRevision);
        Assert.Equal(1000UL, (await context.PrefillCachedDepots.SingleAsync()).ManifestId);
    }

    [Fact]
    public async Task SteamCache_DepotWriteFailureRollsBackReceipt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(new FailingDepotCommandInterceptor())
            .Options;
        var service = new PrefillCacheService(new TestDbContextFactory(options),
            NullLogger<PrefillCacheService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordSteamCacheAsync(
            20,
            "Game",
            1,
            null,
            [new DepotManifestProgressInfo { DepotId = 100, ManifestId = 1000, TotalBytes = 1 }]));

        await using var context = new AppDbContext(database.Options);
        Assert.Empty(await context.PrefillCachedApps.ToListAsync());
        Assert.Empty(await context.PrefillCachedDepots.ToListAsync());
    }

    [Fact]
    public async Task SteamCache_ConcurrentWritersKeepOneReceiptAndPhysicalPair()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options),
            NullLogger<PrefillCacheService>.Instance);
        var evidence = new[]
        {
            new DepotManifestProgressInfo { DepotId = 100, ManifestId = 1000, TotalBytes = 1 }
        };

        var changes = await Task.WhenAll(
            service.RecordSteamCacheAsync(20, "Game", 1, "one", evidence),
            service.RecordSteamCacheAsync(20, "Game", 1, "two", evidence));

        Assert.Single(changes, changed => changed);
        await using var context = new AppDbContext(database.Options);
        Assert.Single(await context.PrefillCachedApps.ToListAsync());
        Assert.Single(await context.PrefillCachedDepots.ToListAsync());
    }

    [Fact]
    public void CachedApp_RunOptionsRetainStoredNames()
    {
        const string stored = """
            {"CachedApps":[{"AppId":"Case/opaque-id","Revision":"revision-1"},{"AppId":"Legacy/id","Revision":null}]}
            """;
        var restored = JsonSerializer.Deserialize<DaemonRunOptions>(stored)!;
        Assert.Equal("revision-1", Assert.Single(restored.CachedApps, app => app.AppId == "Case/opaque-id").Revision);
        Assert.Null(Assert.Single(restored.CachedApps, app => app.AppId == "Legacy/id").Revision);

        var options = new DaemonRunOptions
        {
            CachedApps = [new CachedAppInput { AppId = "Case/opaque-id", Revision = "revision-1" },
                new CachedAppInput { AppId = "Legacy/id" }]
        };
        var json = JsonSerializer.Serialize(options);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("cachedApps", out _));
        var apps = document.RootElement.GetProperty("CachedApps");
        Assert.Equal(2, apps.GetArrayLength());
        foreach (var app in apps.EnumerateArray())
            Assert.Equal(["AppId", "Revision"], app.EnumerateObject().Select(property => property.Name).ToArray());
        var roundTrip = JsonSerializer.Deserialize<DaemonRunOptions>(json)!;
        Assert.Equal(options.CachedApps, roundTrip.CachedApps);
    }

    [Fact]
    public async Task ScopedController_RoundTripsOpaqueIdsAndRejectsMissingService()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        await service.RecordCachedAppAsync(PrefillPlatform.Epic, "Case/opaque-id", "Game", 1, null);
        await service.RecordCachedAppAsync(PrefillPlatform.Xbox, "Case/opaque-id", "Game", 1, null);
        var (controller, _) = PrefillCacheChangeTests.NewController(database.Options);
        Assert.IsType<BadRequestObjectResult>((await controller.GetCachedAppsAsync(null)).Result);
        var result = Assert.IsType<OkObjectResult>((await controller.GetCachedAppsAsync(PrefillPlatform.Epic)).Result);
        var apps = Assert.IsAssignableFrom<IEnumerable<CachedAppDto>>(result.Value);
        Assert.Equal("Case/opaque-id", Assert.Single(apps).AppId);
        await controller.ClearAppCacheAsync("Case/opaque-id", PrefillPlatform.Epic);
        Assert.Empty(await service.GetCachedAppsAsync(PrefillPlatform.Epic));
        Assert.Single(await service.GetCachedAppsAsync(PrefillPlatform.Xbox));
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    [InlineData(PrefillPlatform.Xbox)]
    public async Task PersistentPicker_UsesSharedCacheAcrossPlatforms(PrefillPlatform platform)
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        await service.RecordCachedAppAsync(platform, "CACHED-ID", "Game", 1, null);
        var controller = new PersistentPrefillController(null!, null!, service, NullLogger<PersistentPrefillController>.Instance);
        var method = typeof(PersistentPrefillController).GetMethod("ResolveCachedAppIdsForGamePickerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<List<string>>)method.Invoke(controller,
            [platform, new List<string> { "cached-id", "not-cached" }, CancellationToken.None])!;
        var result = await task;
        Assert.Equal(["cached-id"], result);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelledTask = (Task<List<string>>)method.Invoke(controller,
            [platform, new List<string> { "cached-id" }, cancelled.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);
    }

    [Fact]
    public async Task ConcurrentAppWrites_KeepOneMembership()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        var writes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.RecordCachedAppAsync(PrefillPlatform.Xbox, "opaque", "Game", 1, null)));
        Assert.Equal(1, writes.Count(added => added));
        Assert.Single(await service.GetCachedAppsAsync(PrefillPlatform.Xbox));
    }

    [Fact]
    public void CacheStatus_RestrictsResponsesAndLetsNegativeWin()
    {
        var status = new CacheStatusResult
        {
            Apps = [new AppCacheStatus { AppId = "A", IsUpToDate = true },
                new AppCacheStatus { AppId = "B", IsUpToDate = true },
                new AppCacheStatus { AppId = "B", IsUpToDate = false },
                new AppCacheStatus { AppId = "unrequested", IsUpToDate = true }]
        };
        var (verified, outdated, unknown) = status.ResolveAppIds(["A", "B", "C", "A"]);
        Assert.Equal(["A"], verified);
        Assert.Equal(["B"], outdated);
        Assert.Equal(["C"], unknown);
    }

    [Fact]
    public void CacheStatus_NormalizesTypedRowsIntoExclusiveBuckets()
    {
        var status = new CacheStatusResult
        {
            Version = 2,
            Apps =
            [
                AppCacheStatus.Current("A"),
                AppCacheStatus.Outdated("B"),
                AppCacheStatus.Unknown("C", CacheReason.ManifestUnavailable)
            ]
        };

        var normalized = status.Normalize(["A", "B", "C"]);
        var (current, outdated, unknown) = normalized.ResolveAppIds(["A", "B", "C"]);
        Assert.Equal(["A"], current);
        Assert.Equal(["B"], outdated);
        Assert.Equal(["C"], unknown);
        Assert.Equal(3, current.Concat(outdated).Concat(unknown).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void CacheStatus_InvalidTypedSetDoesNotAcceptPartialClaims()
    {
        var status = new CacheStatusResult
        {
            Version = 2,
            Apps =
            [
                AppCacheStatus.Current("A"),
                AppCacheStatus.Outdated("A"),
                AppCacheStatus.Current("extra")
            ]
        };

        var normalized = status.Normalize(["A", "B"]);
        Assert.All(normalized.Apps, app =>
        {
            Assert.Equal(CacheOutcome.Unknown, app.Outcome);
            Assert.Equal(CacheReason.InvalidResult, app.Reason);
        });
    }

    [Fact]
    public async Task Migration_GroupsSteamDepotsAndPreservesLegacyRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        var migration = new AddPrefillCachedApps();
        var command = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        await context.Database.ExecuteSqlRawAsync(command);
        Assert.Empty(await context.PrefillCachedApps.ToListAsync());
        context.PrefillCachedDepots.AddRange(
            new PrefillCachedDepot { AppId = 123, DepotId = 1, ManifestId = 1, TotalBytes = 10, CachedAtUtc = DateTime.UtcNow },
            new PrefillCachedDepot { AppId = 123, DepotId = 2, ManifestId = 1, TotalBytes = 20, AppName = "Game", CachedAtUtc = DateTime.UtcNow });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync(command);
        var app = Assert.Single(await context.PrefillCachedApps.ToListAsync());
        Assert.Equal(PrefillPlatform.Steam, app.Platform);
        Assert.Equal("123", app.AppId);
        Assert.Equal("Game", app.AppName);
        Assert.Null(app.CachedBy);
        Assert.Equal(30, app.TotalBytes);
        Assert.Equal(2, await context.PrefillCachedDepots.CountAsync());
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam, "steam")]
    [InlineData(PrefillPlatform.Epic, "epicgames")]
    [InlineData(PrefillPlatform.BattleNet, "blizzard")]
    [InlineData(PrefillPlatform.Riot, "riot")]
    [InlineData(PrefillPlatform.Xbox, "xbox")]
    public async Task EvictedDownload_HidesOnlyMatchingPlatform(PrefillPlatform platform, string serviceName)
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        foreach (var value in Enum.GetValues<PrefillPlatform>())
            await service.RecordCachedAppAsync(value, "123", "Game", 1, null);
        await using var context = new AppDbContext(database.Options);
        context.Downloads.Add(new Download
        {
            Service = serviceName, ClientIp = "127.0.0.1", Datasource = "Default", GameName = "Game",
            GameAppId = platform == PrefillPlatform.Steam ? 123 : null,
            EpicAppId = platform == PrefillPlatform.Epic ? "123" : null,
            XboxProductId = platform == PrefillPlatform.Xbox ? "123" : null,
            StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow, IsEvicted = true
        });
        await context.SaveChangesAsync();
        Assert.Empty(await service.GetCachedAppsAsync(platform));
        foreach (var other in Enum.GetValues<PrefillPlatform>().Where(value => value != platform))
            Assert.Single(await service.GetCachedAppsAsync(other));
        Assert.Equal(1, await PrefillCacheService.MatchingCachedApps(context, context.Downloads.Where(d => d.IsEvicted)).ExecuteDeleteAsync());
        Assert.Equal(4, await context.PrefillCachedApps.CountAsync());
    }

    [Fact]
    public async Task EpicEviction_KnownDifferentIdDoesNotMatchEqualName()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        await service.RecordCachedAppAsync(PrefillPlatform.Epic, "one", "Game", 1, null);
        await using var context = new AppDbContext(database.Options);
        context.Downloads.Add(new Download
        {
            Service = "epicgames", ClientIp = "127.0.0.1", Datasource = "Default", GameName = "Game",
            EpicAppId = "two", StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow, IsEvicted = true
        });
        await context.SaveChangesAsync();
        Assert.Single(await service.GetCachedAppsAsync(PrefillPlatform.Epic));
        Assert.Equal(0, await PrefillCacheService.MatchingCachedApps(context, context.Downloads).ExecuteDeleteAsync());
    }

    [Fact]
    public async Task ClearApp_AppOnlyRowsAreScopedAndIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        await service.RecordCachedAppAsync(PrefillPlatform.Epic, "opaque-id", "Game", 1, null);
        await service.RecordCachedAppAsync(PrefillPlatform.Riot, "opaque-id", "Game", 1, null);
        Assert.Equal((1, 0), await service.ClearAppCacheAsync(PrefillPlatform.Epic, "opaque-id"));
        Assert.Equal((0, 0), await service.ClearAppCacheAsync(PrefillPlatform.Epic, "opaque-id"));
        Assert.Single(await service.GetCachedAppsAsync(PrefillPlatform.Riot));
        Assert.Equal((1, 0), await service.ClearAllCacheAsync(PrefillPlatform.Riot));
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    [InlineData(PrefillPlatform.Xbox)]
    public async Task CachedApp_RepeatedWriteKeepsOneRowAndKnownName(PrefillPlatform platform)
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        Assert.True(await service.RecordCachedAppAsync(platform, "AbC-001", "Game", 10, "user", "revision-1"));
        Assert.False(await service.RecordCachedAppAsync(platform, "AbC-001", null, 20, null, "revision-1"));
        Assert.True(await service.RecordCachedAppAsync(platform, "AbC-001", null, 20, null, "revision-2"));
        var app = Assert.Single(await service.GetCachedAppsAsync(platform));
        Assert.Equal("AbC-001", app.AppId);
        Assert.Equal("Game", app.AppName);
        Assert.Equal(20, app.TotalBytes);
        Assert.Equal("revision-2", app.CacheRevision);
    }

    [Fact]
    public async Task CachedApp_SameIdRemainsIndependentAcrossPlatforms()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        foreach (var platform in Enum.GetValues<PrefillPlatform>())
        {
            Assert.True(await service.RecordCachedAppAsync(platform, "123", "Game", 1, null));
        }
        await using var context = new AppDbContext(database.Options);
        Assert.Equal(5, await context.PrefillCachedApps.CountAsync());
        foreach (var platform in Enum.GetValues<PrefillPlatform>())
        {
            Assert.Single(await service.GetCachedAppsAsync(platform));
        }
    }

    [Theory]
    [InlineData("steam", PrefillPlatform.Steam)]
    [InlineData("epic", PrefillPlatform.Epic)]
    [InlineData("epicgames", PrefillPlatform.Epic)]
    [InlineData("BattleNet", PrefillPlatform.BattleNet)]
    [InlineData("blizzard", PrefillPlatform.BattleNet)]
    [InlineData("riot", PrefillPlatform.Riot)]
    [InlineData("xbox", PrefillPlatform.Xbox)]
    public void ServiceAlias_ResolvesPlatform(string service, PrefillPlatform platform)
    {
        Assert.Equal(platform, service.ToPrefillPlatform());
        Assert.Equal(platform, platform.ToService().ToPrefillPlatform());
        Assert.Null("unrelated".ToPrefillPlatform());
    }

    private sealed class FailingDepotCommandInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData commandEvent,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"PrefillCachedDepots\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected depot write failure.");
            return ValueTask.FromResult(result);
        }
    }
}
