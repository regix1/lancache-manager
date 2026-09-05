using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropCacheFilePathsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheFilePathsJson",
                table: "CachedServiceDetections");

            migrationBuilder.DropColumn(
                name: "CacheFilePathsJson",
                table: "CachedGameDetections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CacheFilePathsJson",
                table: "CachedServiceDetections",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CacheFilePathsJson",
                table: "CachedGameDetections",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
