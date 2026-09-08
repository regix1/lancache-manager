using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using LancacheManager.Infrastructure.Data.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using LancacheManager.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace LancacheManager.Tests;

public class PrefillCachedAppTests
{
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentPicker_NoCandidatesSkipsDaemonAndFailurePreservesEligibility(bool seed)
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new PrefillCacheService(new TestDbContextFactory(database.Options), NullLogger<PrefillCacheService>.Instance);
        if (seed) await service.RecordCachedAppAsync(PrefillPlatform.Steam, "123", "Game", 1, null);
        var (daemon, _, _) = PrefillCacheChangeTests.NewDaemon(database.Options);
        var controller = new PersistentPrefillController(null!, null!, service, NullLogger<PersistentPrefillController>.Instance);
        var method = typeof(PersistentPrefillController).GetMethod("ResolveCachedAppIdsForGamePickerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<(List<string> CachedAppIds, List<string> UnknownAppIds)>)method.Invoke(controller,
            [PrefillPlatform.Steam, seed ? daemon : null, "missing", new List<string> { "123", "456" }, CancellationToken.None])!;
        var result = await task;
        Assert.Empty(result.CachedAppIds);
        Assert.Equal(seed ? new[] { "123" } : [], result.UnknownAppIds);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelledTask = (Task<(List<string>, List<string>)>)method.Invoke(controller,
            [PrefillPlatform.Steam, daemon, "missing", new List<string> { "123" }, cancelled.Token])!;
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
        Assert.True(await service.RecordCachedAppAsync(platform, "AbC-001", "Game", 10, "user"));
        Assert.False(await service.RecordCachedAppAsync(platform, "AbC-001", null, 20, null));
        var app = Assert.Single(await service.GetCachedAppsAsync(platform));
        Assert.Equal("AbC-001", app.AppId);
        Assert.Equal("Game", app.AppName);
        Assert.Equal(20, app.TotalBytes);
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
}
