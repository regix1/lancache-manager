using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillHistoryEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_PrefillSessions_SessionId",
                table: "PrefillSessions",
                column: "SessionId");

            migrationBuilder.CreateTable(
                name: "PrefillHistoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    AppId = table.Column<uint>(type: "INTEGER", nullable: false),
                    AppName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BytesDownloaded = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillHistoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrefillHistoryEntries_PrefillSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "PrefillSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrefillHistoryEntries_AppId",
                table: "PrefillHistoryEntries",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillHistoryEntries_SessionId",
                table: "PrefillHistoryEntries",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillHistoryEntries_StartedAtUtc",
                table: "PrefillHistoryEntries",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrefillHistoryEntries");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PrefillSessions_SessionId",
                table: "PrefillSessions");
        }
    }
}
