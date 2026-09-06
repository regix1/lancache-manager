using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCorruptionScanHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCurrent",
                table: "CachedCorruptionScans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ScanMode",
                table: "CachedCorruptionScans",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // Current identity must be truthful and deterministic. Only a completed
            // supported v4 row can become current; legacy structural rows retain a
            // null ScanMode because the requested mode was not previously persisted.
            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT
                        "ScanId",
                        ROW_NUMBER() OVER (
                            PARTITION BY "DetectionMode"
                            ORDER BY "CompletedAtUtc" DESC, "ScanId" DESC
                        ) AS row_number
                    FROM "CachedCorruptionScans"
                    WHERE "ContractVersion" = 4
                      AND "Status" = 'completed'
                      AND "DetectionMode" IN ('repeated_miss', 'structural')
                )
                UPDATE "CachedCorruptionScans" AS scan
                SET "IsCurrent" = TRUE
                FROM ranked
                WHERE scan."ScanId" = ranked."ScanId"
                  AND ranked.row_number = 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CachedCorruptionScans_Current_DetectionMode",
                table: "CachedCorruptionScans",
                column: "DetectionMode",
                unique: true,
                filter: "\"IsCurrent\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CachedCorruptionScans_Current_DetectionMode",
                table: "CachedCorruptionScans");

            migrationBuilder.DropColumn(
                name: "IsCurrent",
                table: "CachedCorruptionScans");

            migrationBuilder.DropColumn(
                name: "ScanMode",
                table: "CachedCorruptionScans");
        }
    }
}
