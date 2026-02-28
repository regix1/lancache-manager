using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SplitPrefillPermissionsPerService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- UserSessions: add per-service prefill expiry columns ---
            migrationBuilder.AddColumn<DateTime>(
                name: "SteamPrefillExpiresAtUtc",
                table: "UserSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EpicPrefillExpiresAtUtc",
                table: "UserSessions",
                type: "TEXT",
                nullable: true);

            // Copy existing PrefillExpiresAtUtc into both new columns
            migrationBuilder.Sql(
                "UPDATE UserSessions SET SteamPrefillExpiresAtUtc = PrefillExpiresAtUtc, EpicPrefillExpiresAtUtc = PrefillExpiresAtUtc WHERE PrefillExpiresAtUtc IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "PrefillExpiresAtUtc",
                table: "UserSessions");

            // --- UserPreferences: add per-service thread count columns ---
            migrationBuilder.AddColumn<int>(
                name: "SteamMaxThreadCount",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpicMaxThreadCount",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: true);

            // Copy existing MaxThreadCount into both new columns
            migrationBuilder.Sql(
                "UPDATE UserPreferences SET SteamMaxThreadCount = MaxThreadCount, EpicMaxThreadCount = MaxThreadCount WHERE MaxThreadCount IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "MaxThreadCount",
                table: "UserPreferences");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // --- UserPreferences: restore MaxThreadCount ---
            migrationBuilder.AddColumn<int>(
                name: "MaxThreadCount",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: true);

            // Restore from Steam value (arbitrary choice for rollback)
            migrationBuilder.Sql(
                "UPDATE UserPreferences SET MaxThreadCount = SteamMaxThreadCount WHERE SteamMaxThreadCount IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "SteamMaxThreadCount",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "EpicMaxThreadCount",
                table: "UserPreferences");

            // --- UserSessions: restore PrefillExpiresAtUtc ---
            migrationBuilder.AddColumn<DateTime>(
                name: "PrefillExpiresAtUtc",
                table: "UserSessions",
                type: "TEXT",
                nullable: true);

            // Restore from Steam value (arbitrary choice for rollback)
            migrationBuilder.Sql(
                "UPDATE UserSessions SET PrefillExpiresAtUtc = SteamPrefillExpiresAtUtc WHERE SteamPrefillExpiresAtUtc IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "SteamPrefillExpiresAtUtc",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "EpicPrefillExpiresAtUtc",
                table: "UserSessions");
        }
    }
}
