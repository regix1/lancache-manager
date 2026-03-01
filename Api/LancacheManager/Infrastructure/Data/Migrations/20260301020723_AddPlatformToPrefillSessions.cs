using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformToPrefillSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "PrefillSessions",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Steam");

            migrationBuilder.CreateIndex(
                name: "IX_PrefillSessions_Platform",
                table: "PrefillSessions",
                column: "Platform");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrefillSessions_Platform",
                table: "PrefillSessions");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "PrefillSessions");
        }
    }
}
