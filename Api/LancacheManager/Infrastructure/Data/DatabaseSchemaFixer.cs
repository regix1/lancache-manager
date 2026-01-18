using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// Fixes database schema issues that can't be handled by EF Core migrations alone.
/// This is necessary because SQLite doesn't support "ADD COLUMN IF NOT EXISTS".
/// </summary>
public static class DatabaseSchemaFixer
{
    /// <summary>
    /// Applies schema fixes AFTER migrations run.
    /// This handles existing databases that ran older versions of migrations (e.g., no-op migrations).
    /// Call this AFTER DbContext.Database.MigrateAsync().
    /// </summary>
    public static async Task ApplyPostMigrationFixesAsync(DbContext dbContext, ILogger logger)
    {
        var connection = dbContext.Database.GetDbConnection();
        
        // Connection should already be open from migrations, but ensure it is
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        try
        {
            // Fix: ShowYearInDates column may be missing if database ran the old no-op migration
            // For fresh installs, the migration now properly adds it
            await AddColumnIfNotExistsAsync(connection, "UserPreferences", "ShowYearInDates", "INTEGER NOT NULL DEFAULT 0", logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Post-migration schema fix failed - this may cause issues");
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
