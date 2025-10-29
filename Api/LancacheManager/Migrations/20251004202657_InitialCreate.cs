using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientStats",
                columns: table => new
                {
                    ClientIp = table.Column<string>(type: "TEXT", nullable: false),
                    TotalCacheHitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalCacheMissBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalDownloads = table.Column<int>(type: "INTEGER", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivityLocal = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientStats", x => x.ClientIp);
                });

            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    ClientIp = table.Column<string>(type: "TEXT", nullable: false),
                    StartTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartTimeLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTimeLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CacheHitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheMissBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    GameAppId = table.Column<uint>(type: "INTEGER", nullable: true),
                    GameName = table.Column<string>(type: "TEXT", nullable: true),
                    GameImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastUrl = table.Column<string>(type: "TEXT", nullable: true),
                    DepotId = table.Column<uint>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceStats",
                columns: table => new
                {
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    TotalCacheHitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalCacheMissBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalDownloads = table.Column<int>(type: "INTEGER", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastActivityLocal = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceStats", x => x.Service);
                });

            migrationBuilder.CreateTable(
                name: "SteamDepotMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DepotId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AppId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AppName = table.Column<string>(type: "TEXT", nullable: true),
                    DiscoveredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamDepotMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientIp = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Service = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    BytesServed = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheStatus = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    DepotId = table.Column<uint>(type: "INTEGER", nullable: true),
                    DownloadId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogEntries_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientStats_LastActivityUtc",
                table: "ClientStats",
                column: "LastActivityUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_Client_Service_Active",
                table: "Downloads",
                columns: new[] { "ClientIp", "Service", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_EndTime",
                table: "Downloads",
                column: "EndTimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_IsActive",
                table: "Downloads",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_StartTime",
                table: "Downloads",
                column: "StartTimeUtc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Client_Service",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service" });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_DownloadId",
                table: "LogEntries",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service", "Timestamp", "Url", "BytesServed" });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Timestamp",
                table: "LogEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceStats_LastActivityUtc",
                table: "ServiceStats",
                column: "LastActivityUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_AppId",
                table: "SteamDepotMappings",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings",
                column: "DepotId");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId_AppId",
                table: "SteamDepotMappings",
                columns: new[] { "DepotId", "AppId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientStats");

            migrationBuilder.DropTable(
                name: "LogEntries");

            migrationBuilder.DropTable(
                name: "ServiceStats");

            migrationBuilder.DropTable(
                name: "SteamDepotMappings");

            migrationBuilder.DropTable(
                name: "Downloads");
        }
    }
}
