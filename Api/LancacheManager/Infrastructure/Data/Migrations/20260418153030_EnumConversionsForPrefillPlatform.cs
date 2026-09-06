using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnumConversionsForPrefillPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "PrefillSessions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "Platform",
                table: "PrefillSessions",
                type: "text",
                nullable: false,
                defaultValue: "Steam",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "Steam");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "PrefillHistoryEntries",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "PrefillSessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Platform",
                table: "PrefillSessions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Steam",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "Steam");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "PrefillHistoryEntries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
