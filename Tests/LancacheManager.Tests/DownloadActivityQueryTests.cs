using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

public sealed class DownloadActivityQueryTests
{
    [Fact]
    public async Task BatchStatsUseCurrentSnapshotAndKeepRecordedRangeClients()
    {
        var options = Options("batch");
        var (start, end) = await SeedDownloadsAsync(options);
        var current = CurrentSnapshot();
        var reads = 0;
        var service = (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        SetField(service, "_dbContextFactory", new TestContexts(options));
        SetField(service, "_readActivity", new Func<DownloadSpeedSnapshot>(() =>
        {
            reads++;
            return current;
        }));

        var method = typeof(DashboardBatchService).GetMethod(
            "GetDashboardStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<object>)method.Invoke(
            service,
            [start, end, new List<long>(), null, new List<string>(), "show", new List<string>(), CancellationToken.None])!;
        var response = Assert.IsType<DashboardStatsResponse>(await task);

        Assert.Equal(1, reads);
        AssertCurrent(response);
        Assert.Equal(2, response.UniqueClients);
        Assert.Equal(2, response.Period.Downloads);
    }

    [Fact]
    public async Task DirectStatsUseCurrentSnapshotAndKeepRecordedRangeClients()
    {
        var options = Options("direct");
        var (start, end) = await SeedDownloadsAsync(options);
        var current = CurrentSnapshot();
        var reads = 0;
        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        SetField(controller, "_context", new AppDbContext(options));
        SetField(controller, "_stateRepository", State());
        SetField(controller, "_readActivity", new Func<DownloadSpeedSnapshot>(() =>
        {
            reads++;
            return current;
        }));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.DashboardStatsAsync(start, end, eventId: null, CancellationToken.None);
        var response = Assert.IsType<DashboardStatsResponse>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(1, reads);
        AssertCurrent(response);
        Assert.Equal(2, response.UniqueClients);
        Assert.Equal(2, response.Period.Downloads);
    }

    private static void AssertCurrent(DashboardStatsResponse response)
    {
        Assert.Equal(2, response.ActiveDownloads);
        Assert.Equal(1, response.ActiveClients);
        Assert.Equal("activity-stream", response.ActivityStreamId);
        Assert.Equal(17, response.ActivityRevision);
    }

    private static DownloadSpeedSnapshot CurrentSnapshot()
    {
        var firstSeen = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var lastSeen = firstSeen.AddSeconds(1);
        var activeUntil = firstSeen.AddSeconds(15);
        return new DownloadSpeedSnapshot
        {
            StreamId = "activity-stream",
            Revision = 17,
            TimestampUtc = lastSeen,
            IsAvailable = true,
            WindowSeconds = 2,
            GameSpeeds =
            [
                Game("steam:1", "steam", "10.0.0.9", firstSeen, lastSeen, activeUntil),
                Game("epic:2", "epic", "10.0.0.9", firstSeen, lastSeen, activeUntil)
            ],
            ClientSpeeds =
            [
                new ClientSpeedInfo
                {
                    ClientIp = "10.0.0.9",
                    ActiveGames = 2,
                    ActiveUntilUtc = activeUntil
                }
            ]
        };
    }

    private static GameSpeedInfo Game(
        string key,
        string service,
        string clientIp,
        DateTime firstSeen,
        DateTime lastSeen,
        DateTime activeUntil) => new()
        {
            Key = key,
            Service = service,
            ClientIp = clientIp,
            FirstSeenUtc = firstSeen,
            LastSeenUtc = lastSeen,
            ActiveUntilUtc = activeUntil,
            Sources =
            [
                new DownloadSource
                {
                    Datasources = ["default"],
                    FirstSeenUtc = firstSeen,
                    LastSeenUtc = lastSeen,
                    ActiveUntilUtc = activeUntil,
                    MeasuredUntilUtc = lastSeen.AddSeconds(2)
                }
            ]
        };

    private static async Task<(long Start, long End)> SeedDownloadsAsync(
        DbContextOptions<AppDbContext> options)
    {
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        await using var context = new AppDbContext(options);
        context.Downloads.AddRange(
            Download("steam", "10.0.0.1", start.UtcDateTime.AddMinutes(5), isActive: false),
            Download("epic", "10.0.0.2", start.UtcDateTime.AddMinutes(10), isActive: true),
            Download("wsus", "10.0.0.3", start.UtcDateTime.AddHours(-2), isActive: true));
        await context.SaveChangesAsync();
        return (start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
    }

    private static Download Download(
        string service,
        string clientIp,
        DateTime start,
        bool isActive) => new()
        {
            Service = service,
            ClientIp = clientIp,
            StartTimeUtc = start,
            EndTimeUtc = start.AddMinutes(1),
            CacheHitBytes = 100,
            CacheMissBytes = 50,
            IsActive = isActive
        };

    private static DbContextOptions<AppDbContext> Options(string suffix) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"activity-query-{suffix}-{Guid.NewGuid():N}")
            .Options;

    private static IStateService State()
    {
        var state = DispatchProxy.Create<IStateService, StateProxy>();
        return state;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private sealed class TestContexts(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppDbContext(options));
    }

    private class StateProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => new List<string>(),
                nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
                _ when targetMethod?.ReturnType == typeof(string) => "show",
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }
}
