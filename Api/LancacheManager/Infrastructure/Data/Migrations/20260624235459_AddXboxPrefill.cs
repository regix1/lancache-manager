using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddXboxPrefill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "XboxPrefillExpiresAtUtc",
                table: "UserSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XboxProductId",
                table: "Downloads",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "XboxCdnPatterns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    UrlFragment = table.Column<string>(type: "text", nullable: false),
                    CdnHost = table.Column<string>(type: "text", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XboxCdnPatterns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "XboxGameMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    DiscoveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XboxGameMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_XboxProductId",
                table: "Downloads",
                column: "XboxProductId");

            migrationBuilder.CreateIndex(
                name: "IX_XboxCdnPatterns_ProductId",
                table: "XboxCdnPatterns",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_XboxCdnPatterns_UrlFragment",
                table: "XboxCdnPatterns",
                column: "UrlFragment",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XboxGameMappings_DiscoveredAtUtc",
                table: "XboxGameMappings",
                column: "DiscoveredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_XboxGameMappings_ProductId",
                table: "XboxGameMappings",
                column: "ProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XboxGameMappings_Title",
                table: "XboxGameMappings",
                column: "Title");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XboxCdnPatterns");

            migrationBuilder.DropTable(
                name: "XboxGameMappings");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_XboxProductId",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "XboxPrefillExpiresAtUtc",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "XboxProductId",
                table: "Downloads");
        }
    }
}
