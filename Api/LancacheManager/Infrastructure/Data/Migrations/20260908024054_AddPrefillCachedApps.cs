using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrefillCachedApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.CreateTable(
                name: "PrefillCachedApps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Platform = table.Column<string>(type: "text", nullable: false),
                    AppId = table.Column<string>(type: "text", nullable: false),
                    AppName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CachedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CachedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrefillCachedApps", x => x.Id);
                });

            migration.CreateIndex(
                name: "IX_PrefillCachedApps_Platform_AppId",
                table: "PrefillCachedApps",
                columns: new[] { "Platform", "AppId" },
                unique: true);
            migration.Sql("""
                INSERT INTO "PrefillCachedApps" ("Platform", "AppId", "AppName", "CachedAtUtc", "CachedBy", "TotalBytes")
                SELECT 'Steam', CAST("AppId" AS TEXT), MAX("AppName"), MAX("CachedAtUtc"), MAX("CachedBy"), SUM("TotalBytes")
                FROM "PrefillCachedDepots"
                GROUP BY "AppId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropTable(
                name: "PrefillCachedApps");
        }
    }
}
