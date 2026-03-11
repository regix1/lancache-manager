using Microsoft.EntityFrameworkCore.Migrations;

#nullable enable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpicAppIdToDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpicAppId",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_EpicAppId",
                table: "Downloads",
                column: "EpicAppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Downloads_EpicAppId",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "EpicAppId",
                table: "Downloads");
        }
    }
}
