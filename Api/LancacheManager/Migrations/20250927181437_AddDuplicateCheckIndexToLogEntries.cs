using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddDuplicateCheckIndexToLogEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service", "Timestamp", "Url", "BytesServed" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries");
        }
    }
}
