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
            // Column is added by DatabaseSchemaFixer before migrations run.
            // This migration is now a no-op to prevent duplicate column errors.
            // SQLite doesn't support "ADD COLUMN IF NOT EXISTS" so we handle it
            // in the pre-migration fixer which can properly check existence.
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
