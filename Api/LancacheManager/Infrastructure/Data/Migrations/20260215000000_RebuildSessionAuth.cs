using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RebuildSessionAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop old UserPreferences (has FK to old UserSessions.DeviceId)
            migrationBuilder.DropTable(name: "UserPreferences");

            // Drop old UserSessions table
            migrationBuilder.DropTable(name: "UserSessions");

            // Create new UserSessions table with Guid PK and token hash
            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    SessionType = table.Column<string>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    UserAgent = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                });

            // Create new UserPreferences table with Guid FK
            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelectedTheme = table.Column<string>(type: "TEXT", nullable: true),
                    SharpCorners = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableFocusOutlines = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableTooltips = table.Column<bool>(type: "INTEGER", nullable: false),
                    PicsAlwaysVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableStickyNotifications = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseLocalTimezone = table.Column<bool>(type: "INTEGER", nullable: false),
                    Use24HourFormat = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowDatasourceLabels = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ShowYearInDates = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowedTimeFormats = table.Column<string>(type: "TEXT", nullable: true),
                    RefreshRate = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPreferences_UserSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UserSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Indexes on UserSessions
            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_SessionTokenHash",
                table: "UserSessions",
                column: "SessionTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_SessionType",
                table: "UserSessions",
                column: "SessionType");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ExpiresAtUtc",
                table: "UserSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_IsRevoked",
                table: "UserSessions",
                column: "IsRevoked");

            // Indexes on UserPreferences
            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_SessionId",
                table: "UserPreferences",
                column: "SessionId",
                unique: true);

            // Rename DeviceId → CreatedBySessionId in PrefillSessions
            migrationBuilder.RenameColumn(
                name: "DeviceId",
                table: "PrefillSessions",
                newName: "CreatedBySessionId");

            migrationBuilder.RenameIndex(
                name: "IX_PrefillSessions_DeviceId",
                table: "PrefillSessions",
                newName: "IX_PrefillSessions_CreatedBySessionId");

            // Rename BannedDeviceId → BannedBySessionId in BannedSteamUsers
            migrationBuilder.RenameColumn(
                name: "BannedDeviceId",
                table: "BannedSteamUsers",
                newName: "BannedBySessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert column renames
            migrationBuilder.RenameColumn(
                name: "BannedBySessionId",
                table: "BannedSteamUsers",
                newName: "BannedDeviceId");

            migrationBuilder.RenameColumn(
                name: "CreatedBySessionId",
                table: "PrefillSessions",
                newName: "DeviceId");

            migrationBuilder.RenameIndex(
                name: "IX_PrefillSessions_CreatedBySessionId",
                table: "PrefillSessions",
                newName: "IX_PrefillSessions_DeviceId");

            migrationBuilder.DropTable(name: "UserPreferences");
            migrationBuilder.DropTable(name: "UserSessions");

            // Recreate old tables (DeviceId-based)
            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    OperatingSystem = table.Column<string>(type: "TEXT", nullable: false),
                    Browser = table.Column<string>(type: "TEXT", nullable: false),
                    IsGuest = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    PrefillEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PrefillExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    SelectedTheme = table.Column<string>(type: "TEXT", nullable: true),
                    SharpCorners = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableFocusOutlines = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableTooltips = table.Column<bool>(type: "INTEGER", nullable: false),
                    PicsAlwaysVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisableStickyNotifications = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseLocalTimezone = table.Column<bool>(type: "INTEGER", nullable: false),
                    Use24HourFormat = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowDatasourceLabels = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowYearInDates = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowedTimeFormats = table.Column<string>(type: "TEXT", nullable: true),
                    RefreshRate = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPreferences_UserSessions_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "UserSessions",
                        principalColumn: "DeviceId",
                        onDelete: ReferentialAction.Cascade);
                });
        }
    }
}
