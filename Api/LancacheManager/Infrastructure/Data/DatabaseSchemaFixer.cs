using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// Fixes database schema issues that can't be handled by EF Core migrations alone.
/// PostgreSQL supports "ADD COLUMN IF NOT EXISTS" natively, so this is a thin wrapper.
/// </summary>
public static class DatabaseSchemaFixer
{
    /// <summary>
    /// Applies schema fixes AFTER migrations run.
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
            await AddColumnIfNotExistsAsync(connection, "UserPreferences", "ShowYearInDates", "INTEGER NOT NULL DEFAULT 0", logger);

            // Per-session refresh rate lock override (nullable bool: null = use global, 0 = unlocked, 1 = locked)
            await AddColumnIfNotExistsAsync(connection, "UserPreferences", "RefreshRateLocked", "INTEGER", logger);

            // Per-session max thread count limit per service (nullable int: null = use system default)
            await AddColumnIfNotExistsAsync(connection, "UserPreferences", "SteamMaxThreadCount", "INTEGER", logger);
            await AddColumnIfNotExistsAsync(connection, "UserPreferences", "EpicMaxThreadCount", "INTEGER", logger);

            // Per-session prefill expiry per service (nullable datetime: null = no access)
            await AddColumnIfNotExistsAsync(connection, "UserSessions", "SteamPrefillExpiresAtUtc", "TEXT", logger);
            await AddColumnIfNotExistsAsync(connection, "UserSessions", "EpicPrefillExpiresAtUtc", "TEXT", logger);

            // Token rotation columns for mobile SignalR authentication
            await AddColumnIfNotExistsAsync(connection, "UserSessions", "PreviousSessionTokenHash", "TEXT", logger);
            await AddColumnIfNotExistsAsync(connection, "UserSessions", "PreviousTokenValidUntilUtc", "TEXT", logger);
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
        checkCmd.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name=@table AND column_name=@column";
        var tableParam = checkCmd.CreateParameter();
        tableParam.ParameterName = "@table";
        tableParam.Value = table;
        checkCmd.Parameters.Add(tableParam);
        var colParam = checkCmd.CreateParameter();
        colParam.ParameterName = "@column";
        colParam.Value = column;
        checkCmd.Parameters.Add(colParam);

        var columnExists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

        if (!columnExists)
        {
            logger.LogInformation("Adding missing {Column} column to {Table} table...", column, table);
            using var addCmd = connection.CreateCommand();
            addCmd.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN IF NOT EXISTS \"{column}\" {definition}";
            await addCmd.ExecuteNonQueryAsync();
            logger.LogInformation("{Column} column added successfully", column);
        }
    }
}
