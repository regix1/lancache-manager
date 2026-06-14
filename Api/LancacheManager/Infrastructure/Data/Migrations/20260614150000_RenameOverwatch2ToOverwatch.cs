using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameOverwatch2ToOverwatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DATA-ONLY migration (no schema/column/index change).
            // Blizzard's "Overwatch 2" was renamed to "Overwatch" in the single-source TACT
            // catalog (rust-processor/tact_products.json). Apply-Now only re-maps rows whose
            // GameName is NULL, so already-ingested Blizzard rows keep the old label until this
            // backfill runs. Every UPDATE is scoped to the Blizzard service so Steam's separate
            // "Overwatch(R)" card (Service='steam', different literal name) is never touched.
            // Idempotent: re-running matches zero rows once renamed.

            migrationBuilder.Sql(@"
UPDATE ""Downloads""
SET ""GameName"" = 'Overwatch'
WHERE ""GameName"" = 'Overwatch 2' AND ""Service"" ILIKE '%blizzard%';");

            migrationBuilder.Sql(@"
UPDATE ""GameImages""
SET ""AppId"" = 'overwatch'
WHERE ""AppId"" = 'overwatch-2' AND ""Service"" = 'blizzard';");

            // Defensive: Blizzard is detected at the service tier (GameAppId NULL), so this
            // table usually has no per-game Overwatch row, but rename it if present for safety.
            migrationBuilder.Sql(@"
UPDATE ""CachedGameDetections""
SET ""GameName"" = 'Overwatch'
WHERE ""GameName"" = 'Overwatch 2' AND ""Service"" ILIKE '%blizzard%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the Blizzard-scoped rename. Safe to run even if no rows match.
            migrationBuilder.Sql(@"
UPDATE ""CachedGameDetections""
SET ""GameName"" = 'Overwatch 2'
WHERE ""GameName"" = 'Overwatch' AND ""Service"" ILIKE '%blizzard%';");

            migrationBuilder.Sql(@"
UPDATE ""GameImages""
SET ""AppId"" = 'overwatch-2'
WHERE ""AppId"" = 'overwatch' AND ""Service"" = 'blizzard';");

            migrationBuilder.Sql(@"
UPDATE ""Downloads""
SET ""GameName"" = 'Overwatch 2'
WHERE ""GameName"" = 'Overwatch' AND ""Service"" ILIKE '%blizzard%';");
        }
    }
}
