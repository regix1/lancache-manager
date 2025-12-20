using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddEventsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    StartTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartTimeLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTimeLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventDownloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaggedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AutoTagged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventDownloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventDownloads_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventDownloads_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventDownloads_DownloadId",
                table: "EventDownloads",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_EventDownloads_EventId_DownloadId",
                table: "EventDownloads",
                columns: new[] { "EventId", "DownloadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventDownloads_TaggedAtUtc",
                table: "EventDownloads",
                column: "TaggedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Events_EndTimeUtc",
                table: "Events",
                column: "EndTimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Events_StartTimeUtc",
                table: "Events",
                column: "StartTimeUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventDownloads");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
