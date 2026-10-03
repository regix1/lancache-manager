using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleExecutionWarning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Warning",
                table: "ScheduleExecutions",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Warning",
                table: "ScheduleExecutions");
        }
    }
}
