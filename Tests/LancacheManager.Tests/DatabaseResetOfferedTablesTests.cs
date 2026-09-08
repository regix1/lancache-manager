using System.Text.RegularExpressions;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The Database Management screen and the reset allowlist are two hand-maintained lists of the same
/// table names, and when they drift the screen offers a checkbox the backend then discards without
/// saying so: XboxGameMappings and XboxCdnPatterns were both offered and both cleared nothing.
/// Reading the names out of the screen's own source is what makes the next drift fail here rather
/// than on a user's box.
/// </summary>
public sealed partial class DatabaseResetOfferedTablesTests
{
    [Theory]
    [InlineData("PrefillCachedApps", 0)]
    [InlineData("PrefillCachedDepots", 1)]
    public async Task CachedTableReset_CountsAndClearsMatchingMembership(string table, int remaining)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        context.PrefillCachedApps.AddRange(
            new PrefillCachedApp { Platform = PrefillPlatform.Steam, AppId = "123", CachedAtUtc = DateTime.UtcNow },
            new PrefillCachedApp { Platform = PrefillPlatform.Epic, AppId = "opaque", CachedAtUtc = DateTime.UtcNow });
        context.PrefillCachedDepots.Add(new PrefillCachedDepot
        {
            AppId = 123, DepotId = 456, ManifestId = 789, CachedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        Assert.Equal(2, await DatabaseService.CountResetTableRowsAsync(context, "PrefillCachedApps", CancellationToken.None));
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var service = new DatabaseService(context, notifications, NullLogger<DatabaseService>.Instance,
            null!, new TestDbContextFactory(database.Options), null!, null!, null!, null!, null!, null!, null!, tracker);
        var id = tracker.RegisterOperation(OperationType.DatabaseReset, "reset", new CancellationTokenSource());
        var method = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(service, [id, new List<string> { table }, false, CancellationToken.None])!);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(id)!.Status);
        Assert.Equal(remaining, await DatabaseService.CountResetTableRowsAsync(context, "PrefillCachedApps", CancellationToken.None));
        if (remaining > 0) Assert.Equal(PrefillPlatform.Epic, (await context.PrefillCachedApps.AsNoTracking().SingleAsync()).Platform);
    }

    [Fact]
    public void EveryTableTheScreenOffersSurvivesResetResolution()
    {
        var offered = OfferedTableNames();

        // Both were the reported defect, and naming them keeps an extraction that silently returned
        // nothing from passing the loop below.
        Assert.Contains("XboxGameMappings", offered);
        Assert.Contains("XboxCdnPatterns", offered);
        Assert.Contains("PrefillCachedApps", offered);

        foreach (var table in offered)
        {
            Assert.Contains(table, DatabaseService.ResolveResetTables([table]));
        }
    }

    /// <summary>
    /// Whitespace-tolerant wherever the formatter can rewrap, because a pre-commit hook runs
    /// prettier over DataSection.tsx and reformatting it must not fail a backend test.
    /// </summary>
    private static List<string> OfferedTableNames()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Web",
            "src",
            "components",
            "features",
            "management",
            "sections",
            "DataSection.tsx"));

        var list = TableListRegex().Match(source);
        Assert.True(
            list.Success,
            "DataSection.tsx no longer declares the reset table list. If it moved or was renamed, "
                + "this guard must be pointed at its new home rather than deleted.");

        return TableNameRegex().Matches(list.Groups[1].Value)
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    [GeneratedRegex(@"const\s+tables\s*=\s*\[(.*?)\n\s*\];", RegexOptions.Singleline)]
    private static partial Regex TableListRegex();

    [GeneratedRegex(@"name:\s*['""]([A-Za-z]+)['""]")]
    private static partial Regex TableNameRegex();
}
