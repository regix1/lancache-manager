using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "PrefillHistoryEntries",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "PrefillHistoryEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "PrefillHistoryEntries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "PrefillRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DaemonInstanceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScheduleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NotificationMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ParentOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    OptionsJson = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CancelRequested = table.Column<bool>(type: "boolean", nullable: false),
                    HistoryIncomplete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrefillRuns_PrefillSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "PrefillSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrefillHistoryEntries_RunId_AppId",
                table: "PrefillHistoryEntries",
                columns: new[] { "RunId", "AppId" },
                unique: true,
                filter: "\"RunId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillRuns_SessionId_CompletedAtUtc",
                table: "PrefillRuns",
                columns: new[] { "SessionId", "CompletedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_PrefillHistoryEntries_PrefillRuns_RunId",
                table: "PrefillHistoryEntries",
                column: "RunId",
                principalTable: "PrefillRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PrefillHistoryEntries_PrefillRuns_RunId",
                table: "PrefillHistoryEntries");

            migrationBuilder.DropTable(
                name: "PrefillRuns");

            migrationBuilder.DropIndex(
                name: "IX_PrefillHistoryEntries_RunId_AppId",
                table: "PrefillHistoryEntries");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "PrefillHistoryEntries");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "PrefillHistoryEntries");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "PrefillHistoryEntries");
        }
    }
}
