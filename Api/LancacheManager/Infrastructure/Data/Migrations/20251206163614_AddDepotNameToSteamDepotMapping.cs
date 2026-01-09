using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDepotNameToSteamDepotMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HideAboutSections",
                table: "UserPreferences");

            migrationBuilder.AddColumn<string>(
                name: "DepotName",
                table: "SteamDepotMappings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DepotName",
                table: "SteamDepotMappings");

            migrationBuilder.AddColumn<bool>(
                name: "HideAboutSections",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
