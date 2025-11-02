using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedGameDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedGameDetections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameAppId = table.Column<uint>(type: "INTEGER", nullable: false),
                    GameName = table.Column<string>(type: "TEXT", nullable: false),
                    CacheFilesFound = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSizeBytes = table.Column<ulong>(type: "INTEGER", nullable: false),
                    DepotIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SampleUrlsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CacheFilePathsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastDetectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedGameDetections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedGameDetection_GameAppId",
                table: "CachedGameDetections",
                column: "GameAppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedGameDetection_LastDetectedUtc",
                table: "CachedGameDetections",
                column: "LastDetectedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedGameDetections");
        }
    }
}
