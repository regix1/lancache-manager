using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// The other direction of the drift <see cref="DatabaseResetOfferedTablesTests"/> guards. The full
/// wipe reads its table list off the EF model, and the reset then puts that list back through the
/// hand-maintained allowlist, discarding anything missing from it with no log and no default arm.
/// Map a new entity, forget the allowlist entry, and the endpoint that claims to wipe everything
/// quietly leaves that table full. [24][25]
/// </summary>
public sealed class DatabaseResetFullWipeTablesTests
{
    [Fact]
    public void EveryTableTheFullWipeResolvesSurvivesResetResolution()
    {
        // Table names are relational metadata: the in-memory provider builds the model without the
        // relational conventions and every GetTableName() comes back null, which silently empties
        // both lists below. No connection is ever opened here - the model is read, not queried.
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=127.0.0.1;Database=lancache")
                .Options);

        var wiped = DatabaseService.ResolveFullResetTables(context);
        Assert.NotEmpty(wiped);

        var mapped = context.Model.GetEntityTypes()
            .Select(entityType => entityType.GetTableName())
            .Where(tableName => !string.IsNullOrEmpty(tableName))
            .Select(tableName => tableName!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The wipe leaves UserAccounts alone so the user stays signed in, and __EFMigrationsHistory
        // is not a mapped entity so it never reaches this comparison. A third name appearing here is
        // a table the wipe stopped covering.
        Assert.Equal(["UserAccounts"], mapped.Except(wiped, StringComparer.Ordinal).ToList());

        foreach (var table in wiped)
        {
            Assert.Contains(table, DatabaseService.ResolveResetTables([table]));
        }
    }
}
