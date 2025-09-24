using System;
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
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTime = table.Column<DateTime>(type: "TEXT", nullable: false),
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
                    LastActivity = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SteamDepotMappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientStats_LastSeen",
                table: "ClientStats",
                column: "LastSeen");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_Client_Service_Active",
                table: "Downloads",
                columns: new[] { "ClientIp", "Service", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_EndTime",
                table: "Downloads",
                column: "EndTime");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_IsActive",
                table: "Downloads",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_StartTime",
                table: "Downloads",
                column: "StartTime",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceStats_LastActivity",
                table: "ServiceStats",
                column: "LastActivity");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_AppId",
                table: "SteamDepotMappings",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_SteamDepotMappings_DepotId",
                table: "SteamDepotMappings",
                column: "DepotId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientStats");

            migrationBuilder.DropTable(
                name: "Downloads");

            migrationBuilder.DropTable(
                name: "ServiceStats");

            migrationBuilder.DropTable(
                name: "SteamDepotMappings");
        }
    }
}
