using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIsEvictedToCachedServiceDetections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEvicted",
                table: "CachedServiceDetections",
                type: "boolean",
                nullable: false,
                defaultValue: false);
            // No backfill SQL needed — all existing rows are not evicted
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEvicted",
                table: "CachedServiceDetections");
        }
    }
}
