using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class SeparateSessionIdFromDeviceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Use raw SQL to handle the complex migration with foreign keys
            migrationBuilder.Sql(@"
                -- Disable foreign key constraints
                PRAGMA foreign_keys = OFF;

                -- 1. Create new UserSessions table with DeviceId as primary key
                CREATE TABLE UserSessions_new (
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

                -- 2. Copy data from old UserSessions to new (SessionId becomes DeviceId)
                INSERT INTO UserSessions_new (DeviceId, SessionId, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey)
                SELECT SessionId, SessionId, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey
                FROM UserSessions;

                -- 3. Create new UserPreferences table with DeviceId instead of SessionId
                CREATE TABLE UserPreferences_new (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    DeviceId TEXT NOT NULL,
                    SelectedTheme TEXT NULL,
                    SharpCorners INTEGER NOT NULL,
                    DisableFocusOutlines INTEGER NOT NULL,
                    DisableTooltips INTEGER NOT NULL,
                    PicsAlwaysVisible INTEGER NOT NULL,
                    HideAboutSections INTEGER NOT NULL,
                    DisableStickyNotifications INTEGER NOT NULL,
                    UseLocalTimezone INTEGER NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_UserPreferences_UserSessions_DeviceId FOREIGN KEY (DeviceId) REFERENCES UserSessions_new (DeviceId) ON DELETE CASCADE
                );

                -- 4. Copy data from old UserPreferences to new
                INSERT INTO UserPreferences_new (Id, DeviceId, SelectedTheme, SharpCorners, DisableFocusOutlines, DisableTooltips, PicsAlwaysVisible, HideAboutSections, DisableStickyNotifications, UseLocalTimezone, UpdatedAtUtc)
                SELECT Id, SessionId, SelectedTheme, SharpCorners, DisableFocusOutlines, DisableTooltips, PicsAlwaysVisible, HideAboutSections, DisableStickyNotifications, UseLocalTimezone, UpdatedAtUtc
                FROM UserPreferences;

                -- 5. Drop old tables
                DROP TABLE UserPreferences;
                DROP TABLE UserSessions;

                -- 6. Rename new tables to original names
                ALTER TABLE UserSessions_new RENAME TO UserSessions;
                ALTER TABLE UserPreferences_new RENAME TO UserPreferences;

                -- 7. Create indexes
                CREATE UNIQUE INDEX IX_UserPreferences_DeviceId ON UserPreferences (DeviceId);
                CREATE INDEX IX_UserSessions_ExpiresAtUtc ON UserSessions (ExpiresAtUtc);
                CREATE INDEX IX_UserSessions_IsRevoked ON UserSessions (IsRevoked);

                -- Re-enable foreign key constraints
                PRAGMA foreign_keys = ON;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Use raw SQL to reverse the migration
            migrationBuilder.Sql(@"
                -- Disable foreign key constraints
                PRAGMA foreign_keys = OFF;

                -- 1. Create old UserSessions table with SessionId as primary key
                CREATE TABLE UserSessions_old (
                    SessionId TEXT NOT NULL PRIMARY KEY,
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

                -- 2. Copy data from new UserSessions to old (DeviceId becomes SessionId)
                INSERT INTO UserSessions_old (SessionId, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey)
                SELECT DeviceId, DeviceName, IpAddress, OperatingSystem, Browser, IsGuest, CreatedAtUtc, ExpiresAtUtc, LastSeenAtUtc, IsRevoked, RevokedAtUtc, RevokedBy, ApiKey
                FROM UserSessions;

                -- 3. Create old UserPreferences table with SessionId instead of DeviceId
                CREATE TABLE UserPreferences_old (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    SessionId TEXT NOT NULL,
                    SelectedTheme TEXT NULL,
                    SharpCorners INTEGER NOT NULL,
                    DisableFocusOutlines INTEGER NOT NULL,
                    DisableTooltips INTEGER NOT NULL,
                    PicsAlwaysVisible INTEGER NOT NULL,
                    HideAboutSections INTEGER NOT NULL,
                    DisableStickyNotifications INTEGER NOT NULL,
                    UseLocalTimezone INTEGER NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    CONSTRAINT FK_UserPreferences_UserSessions_SessionId FOREIGN KEY (SessionId) REFERENCES UserSessions_old (SessionId) ON DELETE CASCADE
                );

                -- 4. Copy data from new UserPreferences to old
                INSERT INTO UserPreferences_old (Id, SessionId, SelectedTheme, SharpCorners, DisableFocusOutlines, DisableTooltips, PicsAlwaysVisible, HideAboutSections, DisableStickyNotifications, UseLocalTimezone, UpdatedAtUtc)
                SELECT Id, DeviceId, SelectedTheme, SharpCorners, DisableFocusOutlines, DisableTooltips, PicsAlwaysVisible, HideAboutSections, DisableStickyNotifications, UseLocalTimezone, UpdatedAtUtc
                FROM UserPreferences;

                -- 5. Drop new tables
                DROP TABLE UserPreferences;
                DROP TABLE UserSessions;

                -- 6. Rename old tables to original names
                ALTER TABLE UserSessions_old RENAME TO UserSessions;
                ALTER TABLE UserPreferences_old RENAME TO UserPreferences;

                -- 7. Create indexes
                CREATE UNIQUE INDEX IX_UserPreferences_SessionId ON UserPreferences (SessionId);
                CREATE INDEX IX_UserSessions_ExpiresAtUtc ON UserSessions (ExpiresAtUtc);
                CREATE INDEX IX_UserSessions_IsRevoked ON UserSessions (IsRevoked);

                -- Re-enable foreign key constraints
                PRAGMA foreign_keys = ON;
            ");
        }
    }
}
