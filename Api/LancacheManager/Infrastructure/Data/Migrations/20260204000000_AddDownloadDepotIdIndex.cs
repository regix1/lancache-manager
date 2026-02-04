using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadDepotIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Downloads_DepotId",
                table: "Downloads",
                column: "DepotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Downloads_DepotId",
                table: "Downloads");
        }
    }
}
