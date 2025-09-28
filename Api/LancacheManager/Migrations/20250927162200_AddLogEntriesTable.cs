using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddLogEntriesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                name: "IX_LogEntries_Client_Service",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service" });

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_DownloadId",
                table: "LogEntries",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Timestamp",
                table: "LogEntries",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LogEntries");
        }
    }
}
