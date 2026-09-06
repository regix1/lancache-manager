using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceAndEpicAppIdToGameDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EpicAppId",
                table: "CachedGameDetections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "CachedGameDetections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EpicAppId",
                table: "CachedGameDetections");

            migrationBuilder.DropColumn(
                name: "Service",
                table: "CachedGameDetections");
        }
    }
}
