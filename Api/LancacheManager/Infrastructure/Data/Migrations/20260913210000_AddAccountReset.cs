using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LancacheManager.Infrastructure.Data.Migrations;

public partial class AddAccountReset : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.CreateTable(
            name: "AccountResets",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                PrimaryAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                AccountIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AccountResets", entry => entry.Id);
                table.CheckConstraint("CK_AccountResets_Id", "\"Id\" = 1");
            });
    }

    protected override void Down(MigrationBuilder migration)
        => throw new NotSupportedException("The account reset migration is not reversible. Its journal must be retained to prevent deleting later accounts.");
}
