using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class FixDepotMappingConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings",
                column: "DepotId");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId_AppId",
                table: "SteamDepotMappings",
                columns: new[] { "DepotId", "AppId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings");

            migrationBuilder.DropIndex(
                name: "IX_SteamDepotMappings_DepotId_AppId",
                table: "SteamDepotMappings");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings",
                column: "DepotId",
                unique: true);
        }
    }
}
