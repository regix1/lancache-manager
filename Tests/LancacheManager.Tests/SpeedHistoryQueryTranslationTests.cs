using System.Data.Common;
using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

public class SpeedHistoryQueryTranslationTests
{
    /// <summary>
    /// Runs the speed history endpoint against the Npgsql provider pointed at a port nothing is
    /// listening on. The provider translates the query before it opens a connection, so a sum or a
    /// filter it cannot translate fails here while a translatable one gets as far as the refused
    /// connection. TotalBytes is computed from CacheHitBytes and CacheMissBytes and no column holds
    /// it, so naming it in the aggregate throws at runtime on every call to the endpoint. The
    /// seeded tests cannot see that, because the in-memory provider evaluates the computed property
    /// in .NET and returns an answer.
    /// </summary>
    [Fact]
    public async Task SpeedHistoryTotalsQuery_TranslatesToSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=translation_smoke_test;Timeout=1")
            .Options;

        var controller = new SpeedsController(
            null!,
            new SpeedHistoryDbContextFactory(options),
            DispatchProxy.Create<IStateService, RetroStateProxy>(),
            null!);

        var failure = await Assert.ThrowsAnyAsync<Exception>(
            () => controller.GetSpeedHistoryAsync(60));

        Assert.DoesNotContain("could not be translated", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        var reachedTheDatabase = false;
        for (var link = failure; link is not null; link = link.InnerException)
        {
            reachedTheDatabase |= link is DbException;
        }

        Assert.True(reachedTheDatabase, $"Expected a connection failure, got: {failure}");
    }

    private sealed class SpeedHistoryDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
