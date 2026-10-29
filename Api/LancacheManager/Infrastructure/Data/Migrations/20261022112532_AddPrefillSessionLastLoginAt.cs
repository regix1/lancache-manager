using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillSessionLastLoginAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.AddColumn<DateTime>(
                name: "LastLoginAtUtc",
                table: "PrefillSessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // A row written before this column starts its window from its create time.
            migration.Sql("UPDATE \"PrefillSessions\" SET \"LastLoginAtUtc\" = \"CreatedAtUtc\"");

            migration.AddColumn<bool>(
                name: "SignedOut",
                table: "PrefillSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropColumn(
                name: "LastLoginAtUtc",
                table: "PrefillSessions");

            migration.DropColumn(
                name: "SignedOut",
                table: "PrefillSessions");
        }
    }
}
