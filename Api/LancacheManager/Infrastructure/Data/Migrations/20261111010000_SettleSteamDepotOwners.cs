using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettleSteamDepotOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            // A depot has one owner. Earlier imports could leave a second owner row when another source named a
            // different owner; the stored rows do not say which import was newer, so the row kept is the one the
            // naming queries already show (a non-Prefill row first, then the lowest AppId). No row is removed.
            migration.Sql("UPDATE \"SteamDepotMappings\" AS m SET \"IsOwner\" = false WHERE m.\"IsOwner\" AND m.\"Id\" <> (SELECT keep.\"Id\" FROM \"SteamDepotMappings\" AS keep WHERE keep.\"DepotId\" = m.\"DepotId\" AND keep.\"IsOwner\" ORDER BY keep.\"Source\" = 'Prefill', keep.\"AppId\" LIMIT 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            // Which rows were owners before is not kept, so the depots keep one owner.
        }
    }
}
