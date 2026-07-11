using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCorruptionScanEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy rows contain only service counts and cannot be upgraded into
            // trustworthy candidate evidence. They are derived scan cache, so clear
            // them and require a fresh scan instead of assigning a fabricated scope.
            migrationBuilder.Sql("DELETE FROM \"CachedCorruptionDetections\";");

            migrationBuilder.AddColumn<string>(
                name: "HttpRange",
                table: "LogEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidatesJson",
                table: "CachedCorruptionDetections",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "DatasourceName",
                table: "CachedCorruptionDetections",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<bool>(
                name: "RemovalAllowed",
                table: "CachedCorruptionDetections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ScanId",
                table: "CachedCorruptionDetections",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "CachedCorruptionScans",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    DetectionMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Threshold = table.Column<int>(type: "integer", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedCorruptionScans", x => x.ScanId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedCorruptionDetections_Scan_Service_Datasource",
                table: "CachedCorruptionDetections",
                columns: new[] { "ScanId", "ServiceName", "DatasourceName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedCorruptionScans_CompletedAtUtc",
                table: "CachedCorruptionScans",
                column: "CompletedAtUtc",
                descending: new bool[0]);

            migrationBuilder.AddForeignKey(
                name: "FK_CachedCorruptionDetections_CachedCorruptionScans_ScanId",
                table: "CachedCorruptionDetections",
                column: "ScanId",
                principalTable: "CachedCorruptionScans",
                principalColumn: "ScanId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CachedCorruptionDetections_CachedCorruptionScans_ScanId",
                table: "CachedCorruptionDetections");

            migrationBuilder.DropTable(
                name: "CachedCorruptionScans");

            migrationBuilder.DropIndex(
                name: "IX_CachedCorruptionDetections_Scan_Service_Datasource",
                table: "CachedCorruptionDetections");

            migrationBuilder.DropColumn(
                name: "HttpRange",
                table: "LogEntries");

            migrationBuilder.DropColumn(
                name: "CandidatesJson",
                table: "CachedCorruptionDetections");

            migrationBuilder.DropColumn(
                name: "DatasourceName",
                table: "CachedCorruptionDetections");

            migrationBuilder.DropColumn(
                name: "RemovalAllowed",
                table: "CachedCorruptionDetections");

            migrationBuilder.DropColumn(
                name: "ScanId",
                table: "CachedCorruptionDetections");
        }
    }
}
