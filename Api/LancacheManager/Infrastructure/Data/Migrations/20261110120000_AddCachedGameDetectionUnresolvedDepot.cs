using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedGameDetectionUnresolvedDepot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migration)
        {
            migration.AddColumn<bool>(
                name: "IsUnresolvedDepot",
                table: "CachedGameDetections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Rows written before the flag existed are told apart only by their placeholder name.
            migration.Sql("UPDATE \"CachedGameDetections\" SET \"IsUnresolvedDepot\" = true WHERE \"GameName\" LIKE 'Unknown Game (Depot%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migration)
        {
            migration.DropColumn(
                name: "IsUnresolvedDepot",
                table: "CachedGameDetections");
        }
    }
}
