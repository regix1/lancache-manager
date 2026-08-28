using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropClientAndServiceStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientStats");

            migrationBuilder.DropTable(
                name: "ServiceStats");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientStats",
                columns: table => new
                {
                    ClientIp = table.Column<string>(type: "text", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalCacheHitBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalCacheMissBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalDownloads = table.Column<int>(type: "integer", nullable: false),
                    TotalDurationSeconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientStats", x => x.ClientIp);
                });

            migrationBuilder.CreateTable(
                name: "ServiceStats",
                columns: table => new
                {
                    Service = table.Column<string>(type: "text", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalCacheHitBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalCacheMissBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalDownloads = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceStats", x => x.Service);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientStats_LastActivityUtc",
                table: "ClientStats",
                column: "LastActivityUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceStats_LastActivityUtc",
                table: "ServiceStats",
                column: "LastActivityUtc");
        }
    }
}
