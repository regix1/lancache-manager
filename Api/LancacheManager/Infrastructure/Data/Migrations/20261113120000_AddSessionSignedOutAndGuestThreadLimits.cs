using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionSignedOutAndGuestThreadLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.AddColumn<bool>(
                name: "SignedOut",
                table: "UserSessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migration.AddColumn<int>(
                name: "XboxMaxThreadCount",
                table: "UserPreferences",
                type: "integer",
                nullable: true);

            migration.AddColumn<int>(
                name: "BattleNetMaxThreadCount",
                table: "UserPreferences",
                type: "integer",
                nullable: true);

            migration.AddColumn<int>(
                name: "RiotMaxThreadCount",
                table: "UserPreferences",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropColumn(
                name: "SignedOut",
                table: "UserSessions");

            migration.DropColumn(
                name: "XboxMaxThreadCount",
                table: "UserPreferences");

            migration.DropColumn(
                name: "BattleNetMaxThreadCount",
                table: "UserPreferences");

            migration.DropColumn(
                name: "RiotMaxThreadCount",
                table: "UserPreferences");
        }
    }
}
