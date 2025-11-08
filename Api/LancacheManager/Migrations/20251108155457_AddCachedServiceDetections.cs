using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedServiceDetections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedServiceDetections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CacheFilesFound = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSizeBytes = table.Column<ulong>(type: "INTEGER", nullable: false),
                    SampleUrlsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CacheFilePathsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastDetectedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedServiceDetections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedServiceDetection_LastDetectedUtc",
                table: "CachedServiceDetections",
                column: "LastDetectedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CachedServiceDetection_ServiceName",
                table: "CachedServiceDetections",
                column: "ServiceName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedServiceDetections");
        }
    }
}
