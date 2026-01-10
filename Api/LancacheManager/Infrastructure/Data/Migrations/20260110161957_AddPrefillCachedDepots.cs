using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillCachedDepots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrefillCachedDepots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppId = table.Column<uint>(type: "INTEGER", nullable: false),
                    DepotId = table.Column<uint>(type: "INTEGER", nullable: false),
                    ManifestId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    AppName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CachedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CachedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillCachedDepots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrefillCachedDepots_AppId",
                table: "PrefillCachedDepots",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillCachedDepots_DepotId_ManifestId",
                table: "PrefillCachedDepots",
                columns: new[] { "DepotId", "ManifestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrefillCachedDepots");
        }
    }
}
