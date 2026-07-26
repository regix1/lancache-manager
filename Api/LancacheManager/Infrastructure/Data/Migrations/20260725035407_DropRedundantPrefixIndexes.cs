using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRedundantPrefixIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings");

            migrationBuilder.DropIndex(
                name: "IX_LogEntries_Client_Service",
                table: "LogEntries");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_IsEvicted_StartTimeUtc",
                table: "Downloads");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings",
                column: "DepotId");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Client_Service",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_IsEvicted_StartTimeUtc",
                table: "Downloads",
                columns: new[] { "IsEvicted", "StartTimeUtc" });
        }
    }
}
