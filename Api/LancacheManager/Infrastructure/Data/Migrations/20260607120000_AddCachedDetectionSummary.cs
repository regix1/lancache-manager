using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations;

/// <inheritdoc />
public partial class AddCachedDetectionSummary : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CachedDetectionSummaries",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                GamesOnDiskBytes = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                GamesOnDiskCount = table.Column<int>(type: "integer", nullable: false),
                IdentifiedCacheBytes = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                IdentifiedServiceBytes = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                IdentifiedServiceCount = table.Column<int>(type: "integer", nullable: false),
                ComputedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CachedDetectionSummaries", x => x.Id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CachedDetectionSummaries");
    }
}
