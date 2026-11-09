using System.Text.RegularExpressions;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

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
    [Fact]
    public void AccountResetJournalIsNeitherOfferedNorSelectivelyResettable()
    {
        Assert.DoesNotContain("AccountResets", OfferedTableNames());
        Assert.Empty(DatabaseService.ResolveResetTables(["AccountResets"]));
    }
    [Theory]
    [InlineData("PrefillCachedApps", 0)]
    [InlineData("PrefillCachedDepots", 1)]
    public async Task CachedTableReset_CountsAndClearsMatchingMembership(string table, int remaining)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        using var files = new IntegrationFixture();
        await AccountResetTests.RunAsync(database, files);
        var later = Guid.NewGuid();
        context.UserAccounts.Add(AccountResetTests.Account(later));
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
            null!, new TestDbContextFactory(database.Options), null!, null!, null!,
            new ServiceCollection().BuildServiceProvider(), null!, null!, null!, tracker);
        var id = tracker.RegisterOperation(OperationType.DatabaseReset, "reset", new CancellationTokenSource());
        var method = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(service, [id, new List<string> { table }, false, CancellationToken.None])!);
        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(id)!.Status);
        Assert.NotNull((await context.AccountResets.SingleAsync()).CompletedAtUtc);
        await AccountResetTests.RunAsync(database, files);
        Assert.Equal(later, (await context.UserAccounts.SingleAsync()).Id);
        Assert.Equal(remaining, await DatabaseService.CountResetTableRowsAsync(context, "PrefillCachedApps", CancellationToken.None));
        if (remaining > 0) Assert.Equal(PrefillPlatform.Epic, (await context.PrefillCachedApps.AsNoTracking().SingleAsync()).Platform);
    }

    [Fact]
    public async Task DepotMappingResetWaitsForTheDepotOwnerKeyAndClearsEveryTable()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        using var files = new IntegrationFixture();
        await AccountResetTests.RunAsync(database, files);
        context.SteamDepotMappings.Add(new SteamDepotMapping
        {
            DepotId = 1000, AppId = 1, AppName = "Game", IsOwner = true, Source = "SteamKit2-PICS"
        });
        context.PrefillCachedApps.Add(
            new PrefillCachedApp { Platform = PrefillPlatform.Steam, AppId = "1", CachedAtUtc = DateTime.UtcNow });
        await context.SaveChangesAsync();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        // The depot-mapping reset looks for the PICS file under the PICS directory, which does not exist here.
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new DatabaseService(context, notifications, NullLogger<DatabaseService>.Instance,
            paths, new TestDbContextFactory(database.Options), null!, null!, null!,
            new ServiceCollection().BuildServiceProvider(), null!, null!, null!, tracker);
        var id = tracker.RegisterOperation(OperationType.DatabaseReset, "reset", new CancellationTokenSource());
        var method = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await using var holder = new NpgsqlConnection(database.ConnectionString + ";Application Name=depot-key-holder");
        await holder.OpenAsync();
        await using var held = await holder.BeginTransactionAsync();
        await using (var take = holder.CreateCommand())
        {
            take.CommandText = $"SELECT pg_advisory_xact_lock({PrefillCacheService.DepotOwnerLockKey})";
            await take.ExecuteNonQueryAsync();
        }

        var reset = Task.Run(() => (Task)method.Invoke(service,
            [id, new List<string> { "SteamDepotMappings", "PrefillCachedApps" }, false, CancellationToken.None])!);
        await using (var reader = new NpgsqlConnection(database.ConnectionString + ";Application Name=depot-key-reader"))
        {
            await reader.OpenAsync();
            await using var count = reader.CreateCommand();
            // classid and objid of the bigint key in pg_locks: its high and low 32 bits.
            count.CommandText = $"""
                SELECT count(*) FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND classid = {PrefillCacheService.DepotOwnerLockKey >> 32}::oid
                  AND objid = {PrefillCacheService.DepotOwnerLockKey & 0xFFFFFFFF}::oid
                  AND objsubid = 1
                """;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!reset.IsCompleted && (long)(await count.ExecuteScalarAsync())! < 1)
                await Task.Delay(10, timeout.Token);
        }
        Assert.False(reset.IsCompleted);
        await held.CommitAsync();
        await reset.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(id)!.Status);
        await using var check = new AppDbContext(database.Options);
        Assert.Empty(await check.SteamDepotMappings.ToListAsync());
        Assert.Empty(await check.PrefillCachedApps.ToListAsync());
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
    internal static List<string> OfferedTableNames()
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
