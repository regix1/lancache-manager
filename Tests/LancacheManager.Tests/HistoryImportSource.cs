using Npgsql;

namespace LancacheManager.Tests;

/// <summary>
/// A throwaway source database for a history import: one downloads table in the shape an older LANCache Manager
/// wrote, holding the passes a test names, on the same server as the import's target.
/// </summary>
internal static class HistoryImportSource
{
    public static async Task<string> CreateSourceAsync(
        string targetConnection, IReadOnlyList<(string Client, DateTime Start, DateTime End)> passes)
    {
        var name = $"import_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(targetConnection)
        {
            Database = "postgres",
            SearchPath = string.Empty,
            Pooling = false,
            CommandTimeout = 120
        };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var source = new NpgsqlConnectionStringBuilder(targetConnection)
        {
            Database = name,
            SearchPath = "public",
            Pooling = false
        };
        await using var sourceConnection = new NpgsqlConnection(source.ConnectionString);
        await sourceConnection.OpenAsync();
        await using (var create = new NpgsqlCommand("""
            CREATE TABLE public."Downloads" (
                "Service" text NOT NULL,
                "ClientIp" text NOT NULL,
                "StartTimeUtc" timestamptz NOT NULL,
                "EndTimeUtc" timestamptz NOT NULL,
                "CacheHitBytes" bigint NOT NULL,
                "CacheMissBytes" bigint NOT NULL,
                "IsActive" boolean,
                "DepotId" bigint,
                "GameAppId" bigint,
                "Datasource" text
            )
            """, sourceConnection))
        {
            await create.ExecuteNonQueryAsync();
        }

        foreach (var (client, start, end) in passes)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public."Downloads" (
                    "Service", "ClientIp", "StartTimeUtc", "EndTimeUtc",
                    "CacheHitBytes", "CacheMissBytes", "IsActive", "DepotId", "GameAppId", "Datasource")
                VALUES ('steam', @client, @start, @end, 1, 2, false, 7, NULL, 'default')
                """, sourceConnection);
            insert.Parameters.AddWithValue("client", client);
            insert.Parameters.AddWithValue("start", start);
            insert.Parameters.AddWithValue("end", end);
            await insert.ExecuteNonQueryAsync();
        }

        return source.ConnectionString;
    }

    public static async Task DropSourceAsync(string sourceConnection)
    {
        var source = new NpgsqlConnectionStringBuilder(sourceConnection);
        var name = source.Database;
        source.Database = "postgres";
        source.SearchPath = string.Empty;
        source.Pooling = false;
        source.CommandTimeout = 120;
        await using var connection = new NpgsqlConnection(source.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Holds every write to the test schema's downloads table while reads go on: a SHARE lock lets the import read
/// its batch and check each row, and stops it on the row's insert until the lock is let go.
/// </summary>
internal sealed class DownloadsWriteGate(string connectionString) : IAsyncDisposable
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;

    public async Task CloseAsync()
    {
        _connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await _connection.OpenAsync();
        _transaction = await _connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("LOCK TABLE \"Downloads\" IN SHARE MODE", _connection, _transaction);
        await command.ExecuteNonQueryAsync();
        _closed.TrySetResult();
    }

    public async Task WaitForBlockedWriteAsync(TimeSpan timeout)
    {
        // The import closes the gate itself in one order, so its lock must be held before this connection is used.
        await _closed.Task.WaitAsync(timeout);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("""
                SELECT COUNT(*) FROM pg_locks
                WHERE NOT granted AND locktype = 'relation'
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                  AND relation = '"Downloads"'::regclass
                """, _connection, _transaction);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("The import never reached a write to the downloads table.");
    }

    public async Task OpenAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync() => await OpenAsync();
}
