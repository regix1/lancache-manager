using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddStreamSessionsAndSpeedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DownloadSpeedBps",
                table: "Downloads",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SessionDurationSeconds",
                table: "Downloads",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StreamSessionCount",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "UploadSpeedBps",
                table: "Downloads",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StreamSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    SessionStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SessionEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SessionStartLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SessionEndLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    BytesSent = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesReceived = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    UpstreamHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DownloadId = table.Column<int>(type: "INTEGER", nullable: true),
                    Datasource = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "default")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StreamSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StreamSessions_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_ClientIp",
                table: "StreamSessions",
                column: "ClientIp");

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_Correlation",
                table: "StreamSessions",
                columns: new[] { "ClientIp", "SessionStartUtc", "SessionEndUtc", "UpstreamHost" });

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_Datasource",
                table: "StreamSessions",
                column: "Datasource");

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_DownloadId",
                table: "StreamSessions",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_DuplicateCheck",
                table: "StreamSessions",
                columns: new[] { "ClientIp", "SessionEndUtc", "BytesSent", "BytesReceived", "DurationSeconds", "UpstreamHost", "Datasource" });

            migrationBuilder.CreateIndex(
                name: "IX_StreamSessions_SessionEndUtc",
                table: "StreamSessions",
                column: "SessionEndUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StreamSessions");

            migrationBuilder.DropColumn(
                name: "DownloadSpeedBps",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "SessionDurationSeconds",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "StreamSessionCount",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "UploadSpeedBps",
                table: "Downloads");
        }
    }
}
