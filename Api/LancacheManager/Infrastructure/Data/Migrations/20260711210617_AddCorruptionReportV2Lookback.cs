using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCorruptionReportV2Lookback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // V1 rows are derived scan state and do not carry a truthful evidence
            // lookback. Invalidate children before headers rather than fabricating
            // settings for a snapshot that the V2 API must not expose.
            migrationBuilder.Sql("DELETE FROM \"CachedCorruptionDetections\";");
            migrationBuilder.Sql("DELETE FROM \"CachedCorruptionScans\";");

            migrationBuilder.AddColumn<int>(
                name: "LookbackDays",
                table: "CachedCorruptionScans",
                type: "integer",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LookbackDays",
                table: "CachedCorruptionScans");
        }
    }
}
