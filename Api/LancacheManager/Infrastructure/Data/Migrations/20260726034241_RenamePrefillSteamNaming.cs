using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenamePrefillSteamNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SteamUsername",
                table: "PrefillSessions",
                newName: "AccountUsername");

            migrationBuilder.RenameIndex(
                name: "IX_PrefillSessions_SteamUsername",
                table: "PrefillSessions",
                newName: "IX_PrefillSessions_AccountUsername");

            migrationBuilder.RenameTable(
                name: "BannedSteamUsers",
                newName: "BannedPrefillUsers");

            migrationBuilder.RenameIndex(
                name: "IX_BannedSteamUsers_Username",
                table: "BannedPrefillUsers",
                newName: "IX_BannedPrefillUsers_Username");

            migrationBuilder.RenameIndex(
                name: "IX_BannedSteamUsers_BannedUserId",
                table: "BannedPrefillUsers",
                newName: "IX_BannedPrefillUsers_BannedUserId");

            migrationBuilder.RenameIndex(
                name: "IX_BannedSteamUsers_BannedAtUtc",
                table: "BannedPrefillUsers",
                newName: "IX_BannedPrefillUsers_BannedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_BannedSteamUsers_IsLifted",
                table: "BannedPrefillUsers",
                newName: "IX_BannedPrefillUsers_IsLifted");

            // PostgreSQL keeps the old PK constraint name after RenameTable.
            migrationBuilder.Sql(
                """ALTER TABLE "BannedPrefillUsers" RENAME CONSTRAINT "PK_BannedSteamUsers" TO "PK_BannedPrefillUsers";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """ALTER TABLE "BannedPrefillUsers" RENAME CONSTRAINT "PK_BannedPrefillUsers" TO "PK_BannedSteamUsers";""");

            migrationBuilder.RenameIndex(
                name: "IX_BannedPrefillUsers_IsLifted",
                table: "BannedPrefillUsers",
                newName: "IX_BannedSteamUsers_IsLifted");

            migrationBuilder.RenameIndex(
                name: "IX_BannedPrefillUsers_BannedAtUtc",
                table: "BannedPrefillUsers",
                newName: "IX_BannedSteamUsers_BannedAtUtc");

            migrationBuilder.RenameIndex(
                name: "IX_BannedPrefillUsers_BannedUserId",
                table: "BannedPrefillUsers",
                newName: "IX_BannedSteamUsers_BannedUserId");

            migrationBuilder.RenameIndex(
                name: "IX_BannedPrefillUsers_Username",
                table: "BannedPrefillUsers",
                newName: "IX_BannedSteamUsers_Username");

            migrationBuilder.RenameTable(
                name: "BannedPrefillUsers",
                newName: "BannedSteamUsers");

            migrationBuilder.RenameIndex(
                name: "IX_PrefillSessions_AccountUsername",
                table: "PrefillSessions",
                newName: "IX_PrefillSessions_SteamUsername");

            migrationBuilder.RenameColumn(
                name: "AccountUsername",
                table: "PrefillSessions",
                newName: "SteamUsername");
        }
    }
}
