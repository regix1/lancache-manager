using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixEpicGameDetectionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the old single-column unique index on GameAppId.
            // All Epic games share GameAppId=0, so this index prevented more than one
            // Epic game from ever being persisted to the database.
            migrationBuilder.DropIndex(
                name: "IX_CachedGameDetection_GameAppId",
                table: "CachedGameDetections");

            // Create a composite unique index on (GameAppId, EpicAppId).
            // Steam games: GameAppId is unique per game, EpicAppId is NULL → each row is distinct.
            // Epic games: GameAppId is always 0, EpicAppId is the unique identifier → each row is distinct.
            migrationBuilder.CreateIndex(
                name: "IX_CachedGameDetection_GameAppId_EpicAppId",
                table: "CachedGameDetections",
                columns: new[] { "GameAppId", "EpicAppId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CachedGameDetection_GameAppId_EpicAppId",
                table: "CachedGameDetections");

            migrationBuilder.CreateIndex(
                name: "IX_CachedGameDetection_GameAppId",
                table: "CachedGameDetections",
                column: "GameAppId",
                unique: true);
        }
    }
}
