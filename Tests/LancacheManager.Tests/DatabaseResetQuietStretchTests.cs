using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A reset clears two small tables one right after the other, then spends a long time on the next one. The card
/// must show the second cleared table during that stretch, instead of the first one until the next table is done.
/// </summary>
[Collection(nameof(DatabaseResetCollection))]
public sealed class DatabaseResetQuietStretchTests
{
    private const string ClearedTableStageKey = "signalr.dbReset.clearedTable";

    [Fact]
    public async Task AResetSendsTheSecondQuickTableBeforeAQuietStretchEndsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var slowTableEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowTableRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new RecordingCommandInterceptor
        {
            // The third table's delete is the long one: it waits until the test lets it go.
            OnExecuting = command =>
            {
                if (command.CommandText.Contains("DELETE", StringComparison.Ordinal)
                    && command.CommandText.Contains("\"EvictionScanCheckpoints\"", StringComparison.Ordinal))
                {
                    slowTableEntered.TrySetResult();
                    slowTableRelease.Task.Wait(TimeSpan.FromSeconds(30));
                }
            }
        };
        var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(commands)
            .Options);
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var sent = new ScheduledRunReporterTests.CapturingNotificationService();
        var service = new DatabaseService(
            context: null!,
            sent,
            new CapturingLogger<DatabaseService>(),
            pathResolver: null!,
            contexts,
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: new ServiceCollection()
                .AddSingleton<IOperationConflictChecker>(
                    OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance))
                .BuildServiceProvider(),
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            tracker);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.OperationTerminal += operation =>
        {
            if (operation.Type == OperationType.DatabaseReset)
            {
                ended.TrySetResult();
            }
        };

        // These three keep the order given: none of them must be cleared before another.
        service.StartResetAsync(["BannedPrefillUsers", "CacheSnapshots", "EvictionScanCheckpoints"]);
        try
        {
            await slowTableEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(SentClearedTable(sent, "BannedPrefillUsers"), "The reset's first cleared table was not sent.");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!SentClearedTable(sent, "CacheSnapshots") && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(
                SentClearedTable(sent, "CacheSnapshots"),
                "The reset never sent the second cleared table (CacheSnapshots) while it worked on the next one: "
                + "the card still showed BannedPrefillUsers as the last table cleared.");
        }
        finally
        {
            slowTableRelease.TrySetResult();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static bool SentClearedTable(ScheduledRunReporterTests.CapturingNotificationService sent, string tableName) =>
        sent.PayloadsFor<DatabaseResetProgress>(SignalREvents.DatabaseResetProgress).Any(progress =>
            progress.StageKey == ClearedTableStageKey
            && progress.Context.TryGetValue("tableName", out var name)
            && Equals(name, tableName));
}
