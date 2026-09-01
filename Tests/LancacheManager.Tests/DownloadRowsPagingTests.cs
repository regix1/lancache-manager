using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the forward walk over the download rows. The route used to read every visible row into
/// memory at once, so the size of one response was the size of the table; these hold it to a page
/// and hold the walk to covering each row exactly once.
/// </summary>
public class DownloadRowsPagingTests
{
    /// <summary>
    /// A caller that asks for nothing gets the newest page, not the table. 501 rows are seeded so
    /// the oldest one has to be left behind.
    /// </summary>
    [Fact]
    public async Task TheFirstPageStopsAtThePageSize()
    {
        var controller = await NewControllerAsync(SeedMinutes(501));

        var rows = await GetRowsAsync(controller);

        Assert.Equal(500, rows.Count);
        Assert.Equal(501, rows[0].Id);
        Assert.Equal(2, rows[^1].Id);
        Assert.DoesNotContain(rows, r => r.Id == 1);
    }

    /// <summary>
    /// The last row of a page names the position the next one starts from, and a page shorter than
    /// the page size is the end of the walk.
    /// </summary>
    [Fact]
    public async Task TheNextPageStartsAfterTheLastRowOfThePreviousOne()
    {
        var controller = await NewControllerAsync(SeedMinutes(501));

        var rows = await GetRowsAsync(controller, afterId: 2);

        Assert.Equal(1, Assert.Single(rows).Id);
    }

    /// <summary>
    /// Two rows sharing a start time are ordered by id, so the walk crosses that pair without
    /// repeating either or dropping either.
    /// </summary>
    [Fact]
    public async Task RowsSharingAStartTimeAreWalkedOnceEach()
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        var controller = await NewControllerAsync(
        [
            NewDownload(1, day.AddHours(10)),
            NewDownload(2, day.AddHours(11)),
            NewDownload(3, day.AddHours(11)),
            NewDownload(4, day.AddHours(12))
        ]);

        var wholeList = await GetRowsAsync(controller);
        Assert.Equal([4L, 3L, 2L, 1L], wholeList.Select(r => r.Id));

        var afterTheTie = await GetRowsAsync(controller, afterId: 3);
        Assert.Equal([2L, 1L], afterTheTie.Select(r => r.Id));
    }

    /// <summary>
    /// The row a walk is continuing from can be deleted under it, because the cleanup service runs
    /// on its own schedule. There is no position left to continue from, and an empty page would
    /// read as the end of the walk, so the caller would save a file missing every older row
    /// without being told. The walk fails instead.
    /// </summary>
    [Fact]
    public async Task ContinuingFromARowThatIsGoneFailsTheWalk()
    {
        var controller = await NewControllerAsync(SeedMinutes(3));

        var result = await controller.GetAllAsync(afterId: 9999);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static async Task<List<Download>> GetRowsAsync(
        DownloadRowsController controller,
        long? afterId = null)
    {
        var result = await controller.GetAllAsync(afterId: afterId);
        return Assert.IsType<List<Download>>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<DownloadRowsController> NewControllerAsync(List<Download> rows)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"download-rows-paging-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(rows);
            await seed.SaveChangesAsync();
        }

        var stateService = DispatchProxy.Create<IStateService, NothingHiddenState>();

        return new DownloadRowsController(new AppDbContext(options), stateService)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>
    /// One row per minute, oldest as id 1, so the newest-first order is the ids counting down.
    /// </summary>
    private static List<Download> SeedMinutes(int count)
    {
        var day = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(1, count)
            .Select(i => NewDownload(i, day.AddMinutes(i)))
            .ToList();
    }

    private static Download NewDownload(long id, DateTime startTimeUtc) => new()
    {
        Id = id,
        Service = "steam",
        ClientIp = "10.0.0.1",
        StartTimeUtc = startTimeUtc,
        EndTimeUtc = startTimeUtc.AddMinutes(1),
        CacheHitBytes = 100,
        CacheMissBytes = 0,
        IsActive = false
    };

    /// <summary>
    /// The two stored settings this route reads: no client is hidden, and evicted rows are shown,
    /// so every seeded row is visible and the page boundary is the only thing under test.
    /// </summary>
    public class NothingHiddenState : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => new List<string>(),
                nameof(IStateService.GetEvictedDataMode) => "show",
                _ => null
            };
    }
}
