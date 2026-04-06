using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardCompositeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Downloads_IsEvicted_StartTimeUtc",
                table: "Downloads",
                columns: new[] { "IsEvicted", "StartTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_IsEvicted_StartTimeUtc_ClientIp",
                table: "Downloads",
                columns: new[] { "IsEvicted", "StartTimeUtc", "ClientIp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Downloads_IsEvicted_StartTimeUtc",
                table: "Downloads");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_IsEvicted_StartTimeUtc_ClientIp",
                table: "Downloads");
        }
    }
}
