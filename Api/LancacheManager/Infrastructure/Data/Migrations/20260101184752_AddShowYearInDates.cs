using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddShowYearInDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the ShowYearInDates column to UserPreferences
            // Note: DatabaseSchemaFixer.ApplyPostMigrationFixesAsync() will handle existing databases
            // that already ran this migration when it was a no-op
            migrationBuilder.AddColumn<bool>(
                name: "ShowYearInDates",
                table: "UserPreferences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowYearInDates",
                table: "UserPreferences");
        }
    }
}
