using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDatasourceSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Datasource",
                table: "LogEntries",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Datasource",
                table: "Downloads",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Datasource",
                table: "LogEntries",
                column: "Datasource");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_Datasource",
                table: "Downloads",
                column: "Datasource");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LogEntries_Datasource",
                table: "LogEntries");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_Datasource",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "Datasource",
                table: "LogEntries");

            migrationBuilder.DropColumn(
                name: "Datasource",
                table: "Downloads");
        }
    }
}
