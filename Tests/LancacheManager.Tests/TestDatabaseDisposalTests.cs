using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LancacheManager.Tests;

/// <summary>
/// Disposing a test database drops its schema whatever the test left running on it: a session of
/// its own still inside a transaction, or a session under another application name that writes once
/// the drop has started.
/// </summary>
public sealed class TestDatabaseDisposalTests
{
    [Fact]
    public async Task ASessionLeftOpenOnTheSchemaDoesNotHoldTheDropAsync()
    {
        var database = await TestDatabase.CreateAsync();
        var schema = database.Schema;
        await using var monitor = await OpenMonitorAsync(database);

        await using var context = database.Factory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.SteamDepotMappings.Where(mapping => mapping.DepotId == 11).ToListAsync();

        var teardown = database.DisposeAsync().AsTask();
        var finished = await Task.WhenAny(teardown, Task.Delay(TimeSpan.FromSeconds(10))) == teardown;

        Assert.True(finished, "the drop waited for a session the test left inside a transaction");
        await teardown;
        Assert.Equal(0L, await CountSchemasAsync(monitor, schema));
    }

    [Fact]
    public async Task ASessionUnderAnotherNameThatWritesAfterTheDropStartsDoesNotDeadlockItAsync()
    {
        var database = await TestDatabase.CreateAsync();
        var schema = database.Schema;
        await using var monitor = await OpenMonitorAsync(database);

        await using var writer = new NpgsqlConnection(database.ConnectionString + ";Application Name=disposal-other-writer");
        await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using (var read = writer.CreateCommand())
        {
            read.CommandText = "SELECT count(*) FROM \"SteamDepotMappings\" WHERE \"DepotId\" = 11";
            await read.ExecuteScalarAsync();
        }

        var teardown = database.DisposeAsync().AsTask();
        await WaitForDropToWaitAsync(monitor, schema, teardown);

        var write = await Record.ExceptionAsync(async () =>
        {
            await using var insert = writer.CreateCommand();
            insert.CommandText = """
                INSERT INTO "SteamDepotMappings" ("DepotId", "AppId", "AppName", "IsOwner", "Source", "DiscoveredAt")
                VALUES (11, 10, 'Game', true, 'Prefill', now())
                """;
            await insert.ExecuteNonQueryAsync();
        });
        await transaction.RollbackAsync();
        var teardownError = await Record.ExceptionAsync(() => teardown.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(
            teardownError is null,
            $"dropping the test schema failed: {teardownError?.Message}; the write ended with: {write?.Message}");
        Assert.Equal(0L, await CountSchemasAsync(monitor, schema));
    }

    [Fact]
    public async Task DroppingTheSchemaSucceedsWhileAMappingWriteHoldsItOpen()
    {
        foreach (var insertBeforeDrop in new[] { true, false })
        {
            var database = await TestDatabase.CreateAsync();
            var schema = database.Schema;
            await using var monitor = await OpenMonitorAsync(database);

            // The same read-then-insert transaction the prefill mapping write runs.
            var context = database.Factory.CreateDbContext();
            var transaction = await context.Database.BeginTransactionAsync();
            await context.SteamDepotMappings.Where(mapping => mapping.DepotId == 11).ToListAsync();
            if (insertBeforeDrop)
            {
                await InsertMappingAsync(context);
            }

            var teardown = Record.ExceptionAsync(async () => await database.DisposeAsync());
            await WaitForDropToWaitAsync(monitor, schema, teardown);

            var write = await Record.ExceptionAsync(async () =>
            {
                if (!insertBeforeDrop)
                {
                    await InsertMappingAsync(context);
                }
                await transaction.CommitAsync();
                await transaction.DisposeAsync();
                await context.DisposeAsync();
            });
            var teardownError = await teardown;

            // A disposal that fails after its rename leaves the schema under the renamed name.
            await using (var cleanup = monitor.CreateCommand())
            {
                cleanup.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\", \"{schema}_dropped\" CASCADE";
                await cleanup.ExecuteNonQueryAsync();
            }

            Assert.True(
                teardownError is null,
                $"insert before drop: {insertBeforeDrop}; dropping the test schema failed: {teardownError?.Message}; the write ended with: {write?.Message}");
        }
    }

    private static async Task InsertMappingAsync(AppDbContext context)
    {
        context.SteamDepotMappings.Add(new SteamDepotMapping
        {
            DepotId = 11,
            AppId = 10,
            AppName = "Game",
            IsOwner = true,
            Source = "Prefill",
            DiscoveredAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    // Outside the schema's search path and application name, so disposal never ends it.
    private static async Task<NpgsqlConnection> OpenMonitorAsync(TestDatabase database)
    {
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            SearchPath = null,
            ApplicationName = "disposal-monitor",
            Pooling = false
        };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    // Returns once the drop waits on a lock, or once the teardown finished without waiting.
    private static async Task WaitForDropToWaitAsync(NpgsqlConnection monitor, string schema, Task teardown)
    {
        await using var command = monitor.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM pg_stat_activity
            WHERE wait_event_type = 'Lock' AND query LIKE 'DROP SCHEMA%' AND position(@schema in query) > 0
            """;
        command.Parameters.AddWithValue("schema", schema);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!teardown.IsCompleted && (long)(await command.ExecuteScalarAsync())! < 1)
            await Task.Delay(10, timeout.Token);
    }

    // Counts the schema under its own name and under any name disposal renamed it to.
    private static async Task<long> CountSchemasAsync(NpgsqlConnection monitor, string schema)
    {
        await using var command = monitor.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname LIKE @schema || '%'";
        command.Parameters.AddWithValue("schema", schema);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
