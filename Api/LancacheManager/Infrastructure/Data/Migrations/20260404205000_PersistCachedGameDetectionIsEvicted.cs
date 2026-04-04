using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersistCachedGameDetectionIsEvicted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEvicted",
                table: "CachedGameDetections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: games with no cache files found are considered evicted.
            migrationBuilder.Sql(
                "UPDATE \"CachedGameDetections\" SET \"IsEvicted\" = TRUE WHERE \"CacheFilesFound\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEvicted",
                table: "CachedGameDetections");
        }
    }
}
