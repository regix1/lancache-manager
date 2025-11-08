using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddBlizzardSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BlizzardArchiveIndex",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "BlizzardByteOffset",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlizzardFileName",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlizzardProduct",
                table: "Downloads",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BlizzardChunkMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Product = table.Column<string>(type: "TEXT", nullable: false),
                    ArchiveIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    ByteOffset = table.Column<uint>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<uint>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    GameName = table.Column<string>(type: "TEXT", nullable: true),
                    GameImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    DiscoveredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlizzardChunkMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlizzardChunkMappings_ArchiveIndex",
                table: "BlizzardChunkMappings",
                column: "ArchiveIndex");

            migrationBuilder.CreateIndex(
                name: "IX_BlizzardChunkMappings_Product",
                table: "BlizzardChunkMappings",
                column: "Product");

            migrationBuilder.CreateIndex(
                name: "IX_BlizzardChunkMappings_ProductArchiveOffset",
                table: "BlizzardChunkMappings",
                columns: new[] { "Product", "ArchiveIndex", "ByteOffset" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlizzardChunkMappings");

            migrationBuilder.DropColumn(
                name: "BlizzardArchiveIndex",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "BlizzardByteOffset",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "BlizzardFileName",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "BlizzardProduct",
                table: "Downloads");
        }
    }
}
