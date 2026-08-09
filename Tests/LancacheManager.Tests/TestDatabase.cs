using LancacheManager.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private TestDatabase(SqliteConnection connection, DbContextOptions<AppDbContext> options)
    {
        _connection = connection;
        Factory = new TestDbContextFactory(options);
    }

    public TestDbContextFactory Factory { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new TestDatabase(connection, options);
    }

    public async Task DropPreferencesTableAsync()
    {
        await using var context = Factory.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync("DROP TABLE \"UserPreferences\"");
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
