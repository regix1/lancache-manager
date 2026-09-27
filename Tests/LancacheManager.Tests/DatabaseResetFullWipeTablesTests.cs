using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// The other direction of the drift <see cref="DatabaseResetOfferedTablesTests"/> guards. The full
/// wipe reads its table list off the EF model, and the reset then puts that list back through the
/// hand-maintained allowlist, discarding anything missing from it with no log and no default arm.
/// Map a new entity, forget the allowlist entry, and the endpoint that claims to wipe everything
/// quietly leaves that table full.
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

        // The reset journal prevents a later start from deleting accounts created after the reset.
        Assert.Equal(["AccountResets", "UserAccounts"], mapped.Except(wiped, StringComparer.Ordinal).Order().ToList());

        foreach (var table in wiped)
        {
            Assert.Contains(table, DatabaseService.ResolveResetTables([table]));
        }
    }

    [Fact]
    public void ResetLocksDownloadsBeforeLogEntries()
    {
        var source = ReadSource("Infrastructure", "Services", "System", "DatabaseService.cs");
        var transaction = source.IndexOf("using var deleteTransaction", StringComparison.Ordinal);
        var downloadsLock = source.IndexOf(
            "LOCK TABLE \\\"Downloads\\\" IN SHARE ROW EXCLUSIVE MODE",
            transaction,
            StringComparison.Ordinal);
        var logEntriesLock = source.IndexOf(
            "LOCK TABLE \\\"LogEntries\\\" IN SHARE ROW EXCLUSIVE MODE",
            transaction,
            StringComparison.Ordinal);
        var firstMutationBranch = source.IndexOf(
            "if (tablesToClear.Contains(\"Downloads\") && !tablesToClear.Contains(\"LogEntries\"))",
            transaction,
            StringComparison.Ordinal);

        Assert.True(transaction >= 0);
        Assert.True(downloadsLock > transaction);
        Assert.True(logEntriesLock > downloadsLock);
        Assert.True(firstMutationBranch > logEntriesLock);
    }

    [Fact]
    public void ResetLogEntriesClearsEveryCheckpointMap()
    {
        var stateSource = ReadSource("Infrastructure", "Services", "State", "StateService.cs");
        var method = stateSource.IndexOf("public void ClearLogProcessingPositions()", StringComparison.Ordinal);
        var methodEnd = stateSource.IndexOf("public LogIngestDiagnostics?", method, StringComparison.Ordinal);
        var body = stateSource[method..methodEnd];

        Assert.Contains("state.LogProcessing.Position = 0", body, StringComparison.Ordinal);
        Assert.Contains("DatasourcePositions.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("DatasourceTotalLines.Clear()", body, StringComparison.Ordinal);
        Assert.Contains("DatasourceSourcePositions.Clear()", body, StringComparison.Ordinal);

        var resetSource = ReadSource("Infrastructure", "Services", "System", "DatabaseService.cs");
        Assert.Contains(
            "if (tablesToClear.Contains(\"LogEntries\"))",
            resetSource,
            StringComparison.Ordinal);
        Assert.Contains("_stateRepository.ClearLogProcessingPositions();", resetSource, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, "Api", "LancacheManager", .. segments]));
    }
}
