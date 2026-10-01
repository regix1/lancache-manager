using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task EvictionScanCheckpointResetCountsDeletesAndReportsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.EvictionScanCheckpoints.AddRange(
                new EvictionScanCheckpoint
                {
                    OperationId = Guid.NewGuid(),
                    Processed = 12,
                    Evicted = 3,
                    UnEvicted = 1,
                    StartedAtUtc = now.AddMinutes(-3),
                    FinalizedAtUtc = now.AddMinutes(-2)
                },
                new EvictionScanCheckpoint
                {
                    OperationId = Guid.NewGuid(),
                    Processed = 5,
                    Evicted = 1,
                    UnEvicted = 0,
                    StartedAtUtc = now.AddMinutes(-1)
                });
            seed.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "192.0.2.10",
                StartTimeUtc = now.AddMinutes(-2),
                EndTimeUtc = now,
                CacheHitBytes = 4096
            });
            await seed.SaveChangesAsync();

            Assert.Equal(
                2,
                await DatabaseService.CountResetTableRowsAsync(
                    seed,
                    "EvictionScanCheckpoints",
                    CancellationToken.None));
        }

        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var recorder = (RecordingNotificationProxy)(object)notifications;
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await using var serviceContext = new AppDbContext(database.Options);
        var service = new DatabaseService(
            serviceContext,
            notifications,
            NullLogger<DatabaseService>.Instance,
            pathResolver: null!,
            dbContextFactory: new TestDbContextFactory(database.Options),
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: null!,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            operationTracker: tracker);
        var operationId = tracker.RegisterOperation(
            OperationType.DatabaseReset,
            "Database reset",
            new CancellationTokenSource());
        var reset = typeof(DatabaseService).GetMethod(
            "DoResetAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await ((Task)reset.Invoke(
            service,
            [operationId, new List<string> { "EvictionScanCheckpoints" }, false, CancellationToken.None])!);

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        await using (var verify = new AppDbContext(database.Options))
        {
            Assert.Empty(await verify.EvictionScanCheckpoints.AsNoTracking().ToListAsync());
            Assert.Single(await verify.Downloads.AsNoTracking().ToListAsync());
        }

        var cleared = Assert.Single(
            recorder.Invocations
                .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAllAsync))
                .Where(call => Assert.IsType<string>(call.Args[0]) == SignalREvents.DatabaseResetProgress)
                .Select(call => Assert.IsType<DatabaseResetProgress>(call.Args[1])),
            progress => progress.StageKey == "signalr.dbReset.clearedTable");
        Assert.Equal(
            "EvictionScanCheckpoints",
            Assert.IsType<string>(cleared.Context["tableName"]));
        Assert.Equal(2, Assert.IsType<int>(cleared.Context["count"]));
    }

    [Fact]
    public void ScheduleHistoryHasCountDeleteAndReportArms()
    {
        var source = ReadSource("Infrastructure", "Services", "System", "DatabaseService.cs");

        Assert.Contains(
            "\"ScheduleExecutions\" => await context.ScheduleExecutions.CountAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains("case \"ScheduleExecutions\":", source, StringComparison.Ordinal);
        Assert.Contains(
            "context.ScheduleExecutions.ExecuteDeleteAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ScheduleExecutions\", scheduleExecutionsCount",
            source,
            StringComparison.Ordinal);
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
