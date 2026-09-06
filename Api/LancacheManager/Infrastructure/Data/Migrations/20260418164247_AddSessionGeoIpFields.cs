using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionGeoIpFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrowserLanguage",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountryName",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IspName",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicIpAddress",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegionName",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScreenResolution",
                table: "UserSessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "UserSessions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrowserLanguage",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "City",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "CountryName",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "IspName",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "PublicIpAddress",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "RegionName",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "ScreenResolution",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "UserSessions");
        }
    }
}
