using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillTrafficAndSliceSizes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.AddColumn<bool>(
                name: "IsPrefill",
                table: "Downloads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migration.AddColumn<string>(
                name: "SliceSizes",
                table: "CachedGameDetections",
                type: "text",
                nullable: true);

            migration.AddColumn<string>(
                name: "ClientAddressesJson",
                table: "PrefillRuns",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropColumn(
                name: "IsPrefill",
                table: "Downloads");

            migration.DropColumn(
                name: "SliceSizes",
                table: "CachedGameDetections");

            migration.DropColumn(
                name: "ClientAddressesJson",
                table: "PrefillRuns");
        }
    }
}
