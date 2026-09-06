using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadsGameLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Downloads_GameAppId",
                table: "Downloads",
                column: "GameAppId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_GameName",
                table: "Downloads",
                column: "GameName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Downloads_GameAppId",
                table: "Downloads");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_GameName",
                table: "Downloads");
        }
    }
}
