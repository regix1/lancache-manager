using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LancacheManager.Tests;

/// <summary>
/// A throwaway copy of the application's tables on the local PostgreSQL server, dropped again on
/// disposal. Every instance gets a schema of its own inside one shared database: xUnit runs test
/// classes in parallel, so two instances alive at once must not see each other's rows, and a schema
/// is the cheapest thing PostgreSQL will create and drop concurrently. A database each serialises on
/// the template PostgreSQL copies from, and one transaction rolled back at the end cannot hold tests
/// that deliberately write from a second connection.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    /// <summary>
    /// The server the schemas live on. CI starts a postgres service with these credentials and a
    /// local checkout is expected to have one running with them too. The address is written out
    /// rather than "localhost": that name resolves to ::1 first on Windows, where the server is
    /// reachable over IPv4 only, and every connection then pays a failed attempt before retrying.
    /// </summary>
    private const string ServerConnectionString = "Host=127.0.0.1;Port=5432;Username=lancache;Password=lancache";

    /// <summary>
    /// Holds the test schemas. Named apart from the "lancache" database so a developer's own
    /// installation never has tables created or dropped in it.
    /// </summary>
    private const string DatabaseName = "lancache_tests";

    private static readonly SemaphoreSlim PreparationGate = new(1, 1);
    private static string? _createScript;

    private readonly string _schema;
    private readonly string _connectionString;

    private TestDatabase(string schema, string connectionString, DbContextOptions<AppDbContext> options)
    {
        _schema = schema;
        _connectionString = connectionString;
        Options = options;
        Factory = new TestDbContextFactory(options);
    }

    public DbContextOptions<AppDbContext> Options { get; }

    public TestDbContextFactory Factory { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var createScript = await PrepareAsync();
        var schema = $"test_{Guid.NewGuid():N}";
        await ExecuteAsync(DatabaseConnectionString, $"CREATE SCHEMA \"{schema}\"");

        // The schema in front is where the unqualified names in the create script land; "public"
        // stays on the path behind it so the citext type the model uses still resolves. The pool is
        // kept small and cleared on disposal because a schema of its own means a connection string
        // of its own, and PostgreSQL only allows a hundred connections by default: around twenty
        // classes run at once, so a pool of five would reach that ceiling exactly and start failing
        // whichever test happened to ask for the next connection. No test opens more than two at a
        // time; they take a second context only after the first is disposed.
        var connectionString = $"{DatabaseConnectionString};Search Path={schema},public;Maximum Pool Size=3";
        await ExecuteAsync(connectionString, createScript);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        return new TestDatabase(schema, connectionString, options);
    }

    public async Task DropPreferencesTableAsync()
    {
        await using var context = Factory.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE \"UserPreferences\"");
    }

    public async ValueTask DisposeAsync()
    {
        using (var connection = new NpgsqlConnection(_connectionString))
        {
            NpgsqlConnection.ClearPool(connection);
        }

        await ExecuteAsync(DatabaseConnectionString, $"DROP SCHEMA \"{_schema}\" CASCADE");
    }

    private static string DatabaseConnectionString => $"{ServerConnectionString};Database={DatabaseName}";

    /// <summary>
    /// Creates the shared database and works out the create script, once for the whole test run. The
    /// script is generated rather than the model creator called because EF Core skips creation when
    /// the database already holds tables, and after the first schema it always does.
    /// </summary>
    private static async Task<string> PrepareAsync()
    {
        await PreparationGate.WaitAsync();
        try
        {
            if (_createScript is null)
            {
                await CreateDatabaseAsync();

                // Installed once in "public" and left there. The create script asks for it too, but
                // an extension belongs to the database rather than the schema, so a test dropping
                // its own schema would take everyone else's citext with it.
                await ExecuteAsync(DatabaseConnectionString, "CREATE EXTENSION IF NOT EXISTS citext SCHEMA public");

                await using var context = new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(DatabaseConnectionString).Options);
                _createScript = context.Database.GenerateCreateScript();
            }

            return _createScript;
        }
        finally
        {
            PreparationGate.Release();
        }
    }

    private static async Task CreateDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection($"{ServerConnectionString};Database=postgres");
        await connection.OpenAsync();
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", DatabaseName);
        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
