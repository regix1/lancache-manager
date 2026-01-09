using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSessionIdColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite doesn't support DROP COLUMN, so we need to recreate the table
            migrationBuilder.Sql(@"
                -- Disable foreign key constraints
                PRAGMA foreign_keys = OFF;

                -- Create new UserSessions table without SessionId
                CREATE TABLE UserSessions_new (
                    DeviceId TEXT NOT NULL PRIMARY KEY,
                    DeviceName TEXT NOT NULL,
                    IpAddress TEXT NOT NULL,
                    OperatingSystem TEXT NOT NULL,
                    Browser TEXT NOT NULL,
                    IsGuest INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    ExpiresAtUtc TEXT NULL,
                    LastSeenAtUtc TEXT NOT NULL,
                    IsRevoked INTEGER NOT NULL,
                    RevokedAtUtc TEXT NULL,
                    RevokedBy TEXT NULL,
                    ApiKey TEXT NULL
                );

                -- Copy data from old table (excluding SessionId)
                INSERT INTO UserSessions_new (DeviceId, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey)
                SELECT DeviceId, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey
                FROM UserSessions;

                -- Drop old table
                DROP TABLE UserSessions;

                -- Rename new table
                ALTER TABLE UserSessions_new RENAME TO UserSessions;

                -- Recreate indexes
                CREATE INDEX IX_UserSessions_ExpiresAtUtc ON UserSessions (ExpiresAtUtc);
                CREATE INDEX IX_UserSessions_IsRevoked ON UserSessions (IsRevoked);

                -- Re-enable foreign key constraints
                PRAGMA foreign_keys = ON;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Add SessionId column back
            migrationBuilder.Sql(@"
                -- Disable foreign key constraints
                PRAGMA foreign_keys = OFF;

                -- Create old UserSessions table with SessionId
                CREATE TABLE UserSessions_old (
                    DeviceId TEXT NOT NULL PRIMARY KEY,
                    SessionId TEXT NULL,
                    DeviceName TEXT NOT NULL,
                    IpAddress TEXT NOT NULL,
                    OperatingSystem TEXT NOT NULL,
                    Browser TEXT NOT NULL,
                    IsGuest INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    ExpiresAtUtc TEXT NULL,
                    LastSeenAtUtc TEXT NOT NULL,
                    IsRevoked INTEGER NOT NULL,
                    RevokedAtUtc TEXT NULL,
                    RevokedBy TEXT NULL,
                    ApiKey TEXT NULL
                );

                -- Copy data back
                INSERT INTO UserSessions_old (DeviceId, SessionId, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey)
                SELECT DeviceId, NULL, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey
                FROM UserSessions;

                -- Drop new table
                DROP TABLE UserSessions;

                -- Rename old table
                ALTER TABLE UserSessions_old RENAME TO UserSessions;

                -- Recreate indexes
                CREATE INDEX IX_UserSessions_ExpiresAtUtc ON UserSessions (ExpiresAtUtc);
                CREATE INDEX IX_UserSessions_IsRevoked ON UserSessions (IsRevoked);

                -- Re-enable foreign key constraints
                PRAGMA foreign_keys = ON;
            ");
        }
    }
}
