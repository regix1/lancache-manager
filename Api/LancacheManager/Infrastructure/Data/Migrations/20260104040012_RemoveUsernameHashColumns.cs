using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUsernameHashColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrefillSessions_SteamUsernameHash",
                table: "PrefillSessions");

            migrationBuilder.DropIndex(
                name: "IX_BannedSteamUsers_UsernameHash",
                table: "BannedSteamUsers");

            migrationBuilder.DropColumn(
                name: "SteamUsernameHash",
                table: "PrefillSessions");

            migrationBuilder.DropColumn(
                name: "UsernameHash",
                table: "BannedSteamUsers");

            migrationBuilder.AddColumn<string>(
                name: "SteamUsername",
                table: "PrefillSessions",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "BannedSteamUsers",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_SteamUsername",
                table: "PrefillSessions",
                column: "SteamUsername");

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_Username",
                table: "BannedSteamUsers",
                column: "Username");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrefillSessions_SteamUsername",
                table: "PrefillSessions");

            migrationBuilder.DropIndex(
                name: "IX_BannedSteamUsers_Username",
                table: "BannedSteamUsers");

            migrationBuilder.DropColumn(
                name: "SteamUsername",
                table: "PrefillSessions");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "BannedSteamUsers");

            migrationBuilder.AddColumn<string>(
                name: "SteamUsernameHash",
                table: "PrefillSessions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UsernameHash",
                table: "BannedSteamUsers",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_SteamUsernameHash",
                table: "PrefillSessions",
                column: "SteamUsernameHash");

            migrationBuilder.CreateIndex(
                name: "IX_BannedSteamUsers_UsernameHash",
                table: "BannedSteamUsers",
                column: "UsernameHash");
        }
    }
}
