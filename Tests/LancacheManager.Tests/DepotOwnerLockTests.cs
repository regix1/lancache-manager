using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

// The owner-row advisory key is database-wide, so another class's mapping write would show up as a
// second waiter on it.
[CollectionDefinition(nameof(DepotOwnerLockCollection), DisableParallelization = true)]
public sealed class DepotOwnerLockCollection { }

[Collection(nameof(DepotOwnerLockCollection))]
public sealed class DepotOwnerLockTests
{
    // classid and objid of the bigint key in pg_locks: its high and low 32 bits.
    private static readonly string KeyWaiters = $"""
        SELECT count(*) FROM pg_locks
        WHERE locktype = 'advisory' AND NOT granted
          AND classid = {PrefillCacheService.DepotOwnerLockKey >> 32}::oid
          AND objid = {PrefillCacheService.DepotOwnerLockKey & 0xFFFFFFFF}::oid
          AND objsubid = 1
        """;

    [Fact]
    public async Task TwoConcurrentMappingWritesForOneDepotLeaveOneOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        var pause = new ReadPause();
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(pause).Options;
        var service = NewService(options);

        await using var first = new AppDbContext(options);
        await using var second = new AppDbContext(options);
        var inserted = await Task.WhenAll(
            Task.Run(() => service.EnsureDepotMappingExistsAsync(first, 100, 1000, "First game")),
            Task.Run(() => service.EnsureDepotMappingExistsAsync(second, 200, 1000, "Second game")))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { true, true }, inserted);
        await using var check = database.Factory.CreateDbContext();
        Assert.Single(await check.SteamDepotMappings.Where(row => row.DepotId == 1000 && row.IsOwner).ToListAsync());
    }

    [Fact]
    public async Task AMappingWriteStartedDuringAnOpenReplaceWaitsAndLeavesOneOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.SteamDepotMappings.Add(new SteamDepotMapping
            {
                DepotId = 1000, AppId = 1, AppName = "Old owner", IsOwner = true, Source = "SteamKit2-PICS"
            });
            await seed.SaveChangesAsync();
        }
        using var files = new PicsFiles(database.Options);
        var service = NewService(database.Options);
        Task<bool>? write = null;

        await files.Pics.ImportToDatabaseAsync(Snapshot(2000, 2, "Replacement game"), true, CancellationToken.None,
            async (message, _) =>
            {
                if (write != null || !message.StartsWith("Inserting mappings...", StringComparison.Ordinal)) return;
                // The replace has deleted the old owner but not committed.
                write = Task.Run(async () =>
                {
                    await using var context = database.Factory.CreateDbContext();
                    return await service.EnsureDepotMappingExistsAsync(context, 5, 1000, "Prefill game");
                });
                await WaitForWaiterOrCompletionAsync(database, write);
            });

        Assert.True(await write!.WaitAsync(TimeSpan.FromSeconds(10)));
        await using var check = database.Factory.CreateDbContext();
        var rows = await check.SteamDepotMappings.Where(row => row.DepotId == 1000).ToListAsync();
        Assert.Equal(5, Assert.Single(rows, row => row.IsOwner).AppId);
    }

    [Fact]
    public async Task AFirstImportWaitingForTheKeySeesAPrefillRowCommittedMeanwhile()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new PicsFiles(database.Options);
        await using var holder = await HoldKeyAsync(database);

        var import = Task.Run(() => files.Pics.ImportToDatabaseAsync(
            Snapshot(1000, 7, "Snapshot name"), false, CancellationToken.None));
        await WaitForWaiterOrCompletionAsync(database, import);
        Assert.False(import.IsCompleted);
        await using (var other = await OpenSecondPoolAsync(database))
        await using (var insert = other.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO "SteamDepotMappings" ("DepotId", "AppId", "AppName", "IsOwner", "DiscoveredAt", "Source")
                VALUES (1000, 7, 'Prefill name', true, now(), 'Prefill')
                """;
            await insert.ExecuteNonQueryAsync();
        }
        await holder.Transaction.CommitAsync();
        await import.WaitAsync(TimeSpan.FromSeconds(10));

        await using var check = database.Factory.CreateDbContext();
        var row = await check.SteamDepotMappings.SingleAsync(row => row.DepotId == 1000 && row.AppId == 7);
        Assert.Equal("SteamKit2-PICS", row.Source);
        Assert.Equal("Snapshot name", row.AppName);
    }

    [Fact]
    public async Task ARetriedImportReleasesTheKey()
    {
        await using var database = await TestDatabase.CreateAsync();
        var failure = new FirstInsertFails();
        using var files = new PicsFiles(RetryOptions(database, failure));
        var service = NewService(database.Options);

        await files.Pics.ImportToDatabaseAsync(Snapshot(1000, 7, "Snapshot name"), false, CancellationToken.None);

        Assert.True(failure.Thrown);
        await using var context = database.Factory.CreateDbContext();
        Assert.True(await service.EnsureDepotMappingExistsAsync(context, 8, 1001, "Prefill game")
            .WaitAsync(TimeSpan.FromSeconds(5)));
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(7, (await check.SteamDepotMappings.SingleAsync(row => row.DepotId == 1000)).AppId);
    }

    [Fact]
    public async Task AMappingWriteRetriedAfterATransientFailureLeavesOneRow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var failure = new FirstSaveFails();
        var options = RetryOptions(database, failure);
        var service = NewService(options);

        await using (var context = new AppDbContext(options))
        {
            Assert.True(await service.EnsureDepotMappingExistsAsync(context, 100, 1000, "Prefill game"));
        }

        Assert.True(failure.Thrown);
        await using var check = database.Factory.CreateDbContext();
        var row = Assert.Single(await check.SteamDepotMappings.ToListAsync());
        Assert.True(row.IsOwner);
    }

    [Fact]
    public async Task APrefillRecordReturnsWhileAnImportHoldsTheKeyAndMapsAfterIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Options);
        await using var holder = await HoldKeyAsync(database);

        Assert.True(await service.RecordSteamCacheAsync(100, "Prefill game", 1, null, [Depot(1000)])
            .WaitAsync(TimeSpan.FromSeconds(1)));

        await using (var context = database.Factory.CreateDbContext())
        {
            Assert.Single(await context.PrefillCachedDepots.ToListAsync());
            Assert.Empty(await context.SteamDepotMappings.ToListAsync());
        }
        await holder.Transaction.CommitAsync();
        var rows = await WaitForMappingsAsync(database, 1);
        Assert.True(Assert.Single(rows).IsOwner);
    }

    [Fact]
    public async Task PrefillRecordsWaitingForTheKeyHoldOneConnectionBetweenThem()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewService(database.Options);
        await using var holder = await HoldKeyAsync(database);

        foreach (var appId in new long[] { 100, 200, 300 })
        {
            Assert.True(await service.RecordSteamCacheAsync(appId, $"Game {appId}", 1, null, [Depot(appId * 10)])
                .WaitAsync(TimeSpan.FromSeconds(1)));
        }
        await using (var reader = await OpenSecondPoolAsync(database))
        {
            await WaitForWaitersAsync(reader, 1);
            // Give any further loop time to reach the key before counting again.
            await Task.Delay(500);
            Assert.Equal(1L, await CountWaitersAsync(reader));
        }

        await holder.Transaction.CommitAsync();
        var rows = await WaitForMappingsAsync(database, 3);
        Assert.Equal(new long[] { 100, 200, 300 }, rows.Where(row => row.IsOwner).Select(row => row.AppId).Order());
    }

    [Fact]
    public async Task APrefillRecordWhoseFirstDepotFailsStillMapsTheSecond()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = database.Factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"SteamDepotMappings\" ADD CONSTRAINT reject_depot CHECK (\"DepotId\" <> 1000)");
        }
        var service = NewService(database.Options);

        Assert.True(await service.RecordSteamCacheAsync(100, "Prefill game", 1, null, [Depot(1000), Depot(1001)]));

        var row = Assert.Single(await WaitForMappingsAsync(database, 1));
        Assert.Equal(1001, row.DepotId);
    }

    private static PrefillCacheService NewService(DbContextOptions<AppDbContext> options) =>
        new(new TestDbContextFactory(options), NullLogger<PrefillCacheService>.Instance);

    private static DepotManifestProgressInfo Depot(long depotId) =>
        new() { DepotId = depotId, ManifestId = 1, TotalBytes = 1 };

    private static PicsJsonData Snapshot(long depotId, long appId, string appName) =>
        JsonSerializer.Deserialize<PicsJsonData>($$$"""
            {"metadata":{"lastChangeNumber":200,"version":"2.8","lastUpdated":"2026-01-01T00:00:00Z"},
             "depotMappings":{"{{{depotId}}}":{"ownerId":{{{appId}}},"appIds":[{{{appId}}}],"appNames":["{{{appName}}}"],
               "source":"SteamKit2-PICS","discoveredAt":"2026-01-01T00:00:00Z"} } }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static DbContextOptions<AppDbContext> RetryOptions(TestDatabase database, IInterceptor failure) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(database.ConnectionString, options => options.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(failure)
            .Options;

    // A pool of its own, so the key holder and the pg_locks reader leave the service's three connections free.
    private static async Task<NpgsqlConnection> OpenSecondPoolAsync(TestDatabase database)
    {
        var connection = new NpgsqlConnection(database.ConnectionString +";Application Name=depot-lock-test");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<KeyHolder> HoldKeyAsync(TestDatabase database)
    {
        var connection = await OpenSecondPoolAsync(database);
        var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_advisory_xact_lock({PrefillCacheService.DepotOwnerLockKey})";
        await command.ExecuteNonQueryAsync();
        return new KeyHolder(connection, transaction);
    }

    private static async Task<long> CountWaitersAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = KeyWaiters;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task WaitForWaitersAsync(NpgsqlConnection connection, long count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await CountWaitersAsync(connection) < count)
            await Task.Delay(10, timeout.Token);
    }

    // Returns once the task waits on the key, or once it finished without waiting.
    private static async Task WaitForWaiterOrCompletionAsync(TestDatabase database, Task task)
    {
        await using var reader = await OpenSecondPoolAsync(database);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!task.IsCompleted && await CountWaitersAsync(reader) < 1)
            await Task.Delay(10, timeout.Token);
    }

    private static async Task<List<SteamDepotMapping>> WaitForMappingsAsync(TestDatabase database, int count)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var context = database.Factory.CreateDbContext();
            var rows = await context.SteamDepotMappings.ToListAsync();
            if (rows.Count >= count) return rows;
            await Task.Delay(50, deadline.Token);
        }
    }

    private sealed class KeyHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class PicsFiles : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "depot-owner-lock-" + Guid.NewGuid().ToString("N"));

        public PicsFiles(DbContextOptions<AppDbContext> options)
        {
            Directory.CreateDirectory(_root);
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = _root;
            var registrations = new ServiceCollection();
            registrations.AddScoped(_ => new AppDbContext(options));
            _services = registrations.BuildServiceProvider();
            Pics = new PicsDataService(NullLogger<PicsDataService>.Instance,
                _services.GetRequiredService<IServiceScopeFactory>(), paths, StateTestMethods.CreateStateService(_root));
        }

        public PicsDataService Pics { get; }

        public void Dispose()
        {
            _services.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    // Holds each of the first two depot-mapping reads until the other arrives, or for one second,
    // so two unserialized writers both read the table before either inserts.
    private sealed class ReadPause : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData commandEvent, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"SteamDepotMappings\"", StringComparison.Ordinal))
            {
                var arrived = Interlocked.Increment(ref _arrived);
                if (arrived == 2) _bothArrived.TrySetResult();
                if (arrived <= 2)
                {
                    try { await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken); }
                    catch (TimeoutException) { }
                }
            }
            return result;
        }
    }

    private sealed class FirstInsertFails : DbCommandInterceptor
    {
        public bool Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData commandEvent, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Thrown && command.CommandText.Contains("UNNEST", StringComparison.Ordinal))
            {
                Thrown = true;
                throw new PostgresException("deadlock detected", "ERROR", "ERROR", "40P01");
            }
            return base.NonQueryExecutingAsync(command, commandEvent, result, cancellationToken);
        }
    }

    private sealed class FirstSaveFails : SaveChangesInterceptor
    {
        public bool Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saveEvent, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Thrown)
            {
                Thrown = true;
                throw new PostgresException("deadlock detected", "ERROR", "ERROR", "40P01");
            }
            return base.SavingChangesAsync(saveEvent, result, cancellationToken);
        }
    }
}
