using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillSessionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BannedSteamUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UsernameHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BanReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    BannedDeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    BannedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BannedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsLifted = table.Column<bool>(type: "INTEGER", nullable: false),
                    LiftedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LiftedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BannedSteamUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrefillSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SteamUsernameHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsAuthenticated = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPrefilling = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TerminationReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TerminatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_BannedAtUtc",
                table: "BannedSteamUsers",
                column: "BannedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_IsLifted",
                table: "BannedSteamUsers",
                column: "IsLifted");

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_UsernameHash",
                table: "BannedSteamUsers",
                column: "UsernameHash");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_ContainerId",
                table: "PrefillSessions",
                column: "ContainerId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_CreatedAtUtc",
                table: "PrefillSessions",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_DeviceId",
                table: "PrefillSessions",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_SessionId",
                table: "PrefillSessions",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_Status",
                table: "PrefillSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_SteamUsernameHash",
                table: "PrefillSessions",
                column: "SteamUsernameHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BannedSteamUsers");

            migrationBuilder.DropTable(
                name: "PrefillSessions");
        }
    }
}
