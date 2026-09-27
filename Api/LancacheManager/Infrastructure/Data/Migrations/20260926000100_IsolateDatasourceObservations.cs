using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IsolateDatasourceObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.DropIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries");

            migration.CreateIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service", "Timestamp", "Url", "BytesServed", "Datasource" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries");

            migration.CreateIndex(
                name: "IX_LogEntries_DuplicateCheck",
                table: "LogEntries",
                columns: new[] { "ClientIp", "Service", "Timestamp", "Url", "BytesServed" });
        }
    }
}
