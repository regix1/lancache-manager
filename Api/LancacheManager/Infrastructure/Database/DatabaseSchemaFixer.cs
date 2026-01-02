using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Infrastructure.Database;

/// <summary>
/// Fixes database schema issues that can't be handled by EF Core migrations alone.
/// This is necessary because SQLite doesn't support "ADD COLUMN IF NOT EXISTS".
/// </summary>
public static class DatabaseSchemaFixer
{
    /// <summary>
    /// Applies schema fixes before migrations run.
    /// Call this before DbContext.Database.MigrateAsync().
    /// </summary>
    public static async Task ApplyPreMigrationFixesAsync(DbContext dbContext, ILogger logger)
    {
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();

        try
        {
            // Fix: Empty AddShowYearInDates migration (20260101184752)
            await AddColumnIfNotExistsAsync(connection, "UserPreferences", "ShowYearInDates", "INTEGER NOT NULL DEFAULT 0", logger);
        }
        catch (Exception ex)
        {
            // Table might not exist yet (fresh install) - migrations will create it
            logger.LogDebug(ex, "Pre-migration schema check skipped (table may not exist yet)");
        }
    }

    private static async Task AddColumnIfNotExistsAsync(
        System.Data.Common.DbConnection connection,
        string table,
        string column,
        string definition,
        ILogger logger)
    {
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var columnExists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

        if (!columnExists)
        {
            logger.LogInformation("Adding missing {Column} column to {Table} table...", column, table);
            using var addCmd = connection.CreateCommand();
            addCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            await addCmd.ExecuteNonQueryAsync();
            logger.LogInformation("{Column} column added successfully", column);
        }
    }
}
