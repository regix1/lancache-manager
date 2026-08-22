using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillSessionCreatedByAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByAccountId",
                table: "PrefillSessions",
                type: "uuid",
                nullable: true);

            // Existing rows name their creating session but not its account, so the account is copied
            // across for every session row that is still there. CreatedBySessionId is a Guid held in a
            // character varying column, so the two sides are matched as text: casting the column to
            // uuid would raise on the legacy rows that hold an empty string or an unparsable value.
            migrationBuilder.Sql("""
                UPDATE "PrefillSessions" AS p
                SET "CreatedByAccountId" = s."AccountId"
                FROM "UserSessions" AS s
                WHERE lower(p."CreatedBySessionId") = s."Id"::text;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByAccountId",
                table: "PrefillSessions");
        }
    }
}
