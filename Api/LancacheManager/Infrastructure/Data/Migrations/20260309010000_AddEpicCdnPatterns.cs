using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpicCdnPatterns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EpicCdnPatterns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CdnHost = table.Column<string>(type: "TEXT", nullable: false),
                    ChunkBaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicCdnPatterns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpicCdnPatterns_AppId",
                table: "EpicCdnPatterns",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_EpicCdnPatterns_ChunkBaseUrl",
                table: "EpicCdnPatterns",
                column: "ChunkBaseUrl",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpicCdnPatterns");
        }
    }
}
