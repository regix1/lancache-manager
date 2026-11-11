using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleExecutionFailureReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.AddColumn<string>(
                name: "FailureReason",
                table: "ScheduleExecutions",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropColumn(
                name: "FailureReason",
                table: "ScheduleExecutions");
        }
    }
}
