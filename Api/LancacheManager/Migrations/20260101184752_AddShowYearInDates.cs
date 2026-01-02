using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Migrations
{
    /// <inheritdoc />
    public partial class AddShowYearInDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
