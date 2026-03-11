using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpicGameMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EpicGameMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DiscoveredByHash = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicGameMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EpicGameMappings_AppId",
                table: "EpicGameMappings",
                column: "AppId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpicGameMappings_Name",
                table: "EpicGameMappings",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_EpicGameMappings_DiscoveredAtUtc",
                table: "EpicGameMappings",
                column: "DiscoveredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EpicGameMappings");
        }
    }
}
