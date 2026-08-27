using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedUnmappedScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedUnmappedScans",
                columns: table => new
                {
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContractVersion = table.Column<int>(type: "integer", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedUnmappedScans", x => x.ScanId);
                });

            migrationBuilder.CreateTable(
                name: "CachedUnmappedDetections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DatasourceName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileCount = table.Column<long>(type: "bigint", nullable: false),
                    TotalSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    FilesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedUnmappedDetections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CachedUnmappedDetections_CachedUnmappedScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "CachedUnmappedScans",
                        principalColumn: "ScanId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedUnmappedDetections_ScanId",
                table: "CachedUnmappedDetections",
                column: "ScanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedUnmappedDetections");

            migrationBuilder.DropTable(
                name: "CachedUnmappedScans");
        }
    }
}
