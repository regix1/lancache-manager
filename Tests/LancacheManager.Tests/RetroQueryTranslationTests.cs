using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class RetroQueryTranslationTests
{
    /// <summary>
    /// Compiles both GroupBy + aggregate projections the retro endpoint runs through the Npgsql
    /// provider, without opening a connection. Every retro request builds one of them - the paged
    /// path wraps the depot-and-client aggregate, the in-memory path materializes it directly, and
    /// a request that merges reads the coarser one - so a clause the provider cannot translate (a
    /// TimeSpan.TotalSeconds or a conditional sum inside a grouped aggregate) fails here instead of
    /// returning a 500 at runtime, which no in-memory-provider test can see.
    /// <para>
    /// It invokes the controller's own method rather than restating the query. The copy this
    /// replaced had already drifted: it was missing XboxProductId and the EvictedCount conditional
    /// sum, which is exactly the shape it existed to protect.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("BuildRetroGroupedQuery")]
    [InlineData("BuildRetroMergedQuery")]
    public void RetroGroupedAggregateQuery_TranslatesToSql(string builderName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        using var context = new AppDbContext(options);
        var controller = new DownloadsController(
            context,
            DispatchProxy.Create<IStateService, RetroStateProxy>(),
            DispatchProxy.Create<IEventsService, NullReturningProxy>(),
            NullLogger<DownloadsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var buildGroupedQuery = typeof(DownloadsController)
            .GetMethod(builderName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var query = (IQueryable)buildGroupedQuery.Invoke(controller, [new RetroDownloadQuery()])!;

        var sql = query.ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The two state reads every retro query makes before it is built, answered so the query reaches the
/// provider: no hidden addresses (a null list would throw on Count) and the mode that keeps evicted
/// rows, so no filter drops out of the query being compiled. Everything else falls through to
/// <see cref="NullReturningProxy"/>.
/// </summary>
internal class RetroStateProxy : NullReturningProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => base.Invoke(targetMethod, args)
        };
}
