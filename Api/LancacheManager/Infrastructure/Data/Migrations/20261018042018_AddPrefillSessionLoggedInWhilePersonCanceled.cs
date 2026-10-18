using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillSessionLoggedInWhilePersonCanceled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.AddColumn<bool>(
                name: "LoggedInWhilePersonCanceled",
                table: "PrefillSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropColumn(
                name: "LoggedInWhilePersonCanceled",
                table: "PrefillSessions");
        }
    }
}
