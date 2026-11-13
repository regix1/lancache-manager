using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a run while its code waits in a step that does not see the stop, so the run's card has ended
/// while its code still runs. The same job asked for again in that time, the way its controller or caller asks, must
/// wait on a card or get the conflict answer, never start beside the stopped run: a corruption scan, an evicted data
/// removal, a data import and the download history upgrade.
/// </summary>
public sealed class ForceStoppedRunOfOtherOwnersRequestWaitsTests
{
    [Fact]
    public async Task ACorruptionScanAskedForWhileAForceStoppedScanStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        // The scan checks for its program before its first progress send; it never gets to run it here.
        await File.WriteAllTextAsync(Path.Combine(host.Root, nameof(IPathResolver.GetRustCorruptionManagerPath)), string.Empty);
        var databases = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"force_stopped_corruption_{Guid.NewGuid():N}").Options);
        var scans = host.Create<CorruptionDetectionService>(databases);

        // The scan's worker is held in its first progress send.
        host.Notifications.Hold(SignalREvents.CorruptionDetectionProgress);
        var stopped = Assert.IsType<Guid>(await scans.StartDetectionAsync());
        await host.Notifications.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
        await host.ForceStopAsync(stopped);

        var (outcome, started) = await host.AskThroughTheQueueAsync(
            OperationType.CorruptionDetection, ConflictScope.Bulk(),
            CorruptionDetectionService.DetectionOperationName(CorruptionDetectionMethod.RepeatedMiss, null),
            () => scans.StartDetectionAsync(),
            new CorruptionDetectionRequestKey(null, 3, CorruptionDetectionService.DefaultLookbackDays));
        ForceStoppedOwnerHost.AssertWaits("The corruption scan", "corruption scan", outcome);

        host.Notifications.Release();
        await ForceStoppedOwnerHost.AssertRunsAfterAsync("corruption scan", "corruption scan", stopped, started);
    }

    [Fact]
    public async Task AnEvictedDataRemovalAskedForWhileAForceStoppedRemovalStillRuns_WaitsOnACard()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        await using var database = await TestDatabase.CreateAsync();
        await using var services = new ServiceCollection()
            .AddScoped(_ => database.Factory.CreateDbContext())
            .AddSingleton(host.Services.GetRequiredService<OperationStateService>())
            .AddSingleton(host.Services.GetRequiredService<IOperationConflictChecker>())
            .BuildServiceProvider();
        var removals = host.Create<CacheReconciliationService>(services);

        // The removal is held in its first progress send, inside its delete transaction, after its repair was made.
        host.Notifications.Hold(SignalREvents.EvictionRemovalProgress);
        var stopped = await removals.StartBulkEvictionRemovalAsync(CancellationToken.None);
        await host.Notifications.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
        await host.ForceStopAsync(stopped);

        var (outcome, _) = await host.AskThroughTheQueueAsync(
            OperationType.EvictionRemoval, ConflictScope.Bulk(), "Evicted Data Removal (all)",
            async () => await removals.StartBulkEvictionRemovalAsync(CancellationToken.None));
        ForceStoppedOwnerHost.AssertWaits("The evicted data removal", "evicted data removal", outcome);
        // Not followed to the waiting card's start: the stopped removal leaves a repair, which this host does not run.
        host.Notifications.Release();
    }

    [Fact]
    public async Task ADataImportAskedForWhileAForceStoppedImportStillRuns_GetsTheConflictAnswerAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Nothing listens on either port: the import fails at its first connection once it gets there.
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=2;Database=target;Username=u;Password=p;Timeout=2"
            })
            .Build();
        var imports = host.Create<DataMigrationController>(configuration);
        var request = new DataMigrationImportRequest
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=source;Username=u;Password=p;Timeout=2"
        };

        // The import, which runs in its request, is held in its started send.
        host.Notifications.Hold(SignalREvents.DataImportStarted);
        var first = imports.ImportLancacheManagerAsync(request);
        await host.Notifications.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
        var stopped = Assert.Single(host.Tracker.GetActiveOperations(OperationType.DataImport)).Id;
        await host.ForceStopAsync(stopped);

        var again = await AnswerOf(imports.ImportLancacheManagerAsync(request));
        Assert.True(again == Conflict,
            $"The data import asked for while the force-stopped import's code still ran was answered: {again}; " +
            "until that code has ended it must get the conflict answer");

        host.Notifications.Release();
        await AnswerOf(first);
        var after = await AnswerOf(imports.ImportLancacheManagerAsync(request));
        Assert.False(after == Conflict,
            "Once the force-stopped import's code ended, a data import was still answered with a conflict");
    }

    private const string Conflict = "the conflict answer";

    /// <summary>What the import request answered. Nothing listens at the source, so an import that started goes on
    /// to open it and fails there, which the app's error handling answers as a server error.</summary>
    private static async Task<string> AnswerOf(Task<IActionResult> import)
    {
        try
        {
            var result = await import.WaitAsync(ForceStoppedOwnerHost.Timeout);
            return result is ConflictObjectResult ? Conflict : $"{Describe(result)}, a second import started beside the stopped one";
        }
        catch (Npgsql.NpgsqlException exception)
        {
            return $"a second import started beside the stopped one: it went on to open the source database ({exception.Message})";
        }
    }

    [Fact]
    public async Task ADownloadHistoryUpgradeAskedForWhileAForceStoppedUpgradeStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var databases = new HeldDatabaseOpen();
        using var upgrades = host.CreateWithParameter<DownloadHistoryUpgradeService>(
            typeof(Task), databases, Task.CompletedTask);
        Task<Guid?> Start() => (Task<Guid?>)typeof(DownloadHistoryUpgradeService)
            .GetMethod("StartUpgradeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(upgrades, null)!;

        // The upgrade's run is held in its database open, the first step of its run.
        var stopped = Assert.IsType<Guid>(await Start());
        await databases.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
        await host.ForceStopAsync(stopped);

        // Asked as its caller asks (RequestRunAsync), with the upgrade's own start.
        var (outcome, started) = await host.AskThroughTheQueueAsync(
            OperationType.DownloadHistoryUpgrade, ConflictScope.Bulk(), DownloadHistoryUpgradeService.OperationName, Start);
        ForceStoppedOwnerHost.AssertWaits("The download history upgrade", "download history upgrade", outcome);

        databases.Release();
        await ForceStoppedOwnerHost.AssertRunsAfterAsync("download history upgrade", "download history upgrade", stopped, started);
    }

    private static string Describe(IActionResult result) =>
        result is ObjectResult { StatusCode: { } status } ? $"{result.GetType().Name} ({status})" : result.GetType().Name;

    /// <summary>
    /// No database: the first open waits for <see cref="Release"/> without seeing the request's cancel, as a
    /// connection already being made does, and then meets the stop at its next step. Every open fails.
    /// </summary>
    private sealed class HeldDatabaseOpen : IDbContextFactory<AppDbContext>
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _opens;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public AppDbContext CreateDbContext() => throw new InvalidOperationException("No database in this test");

        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _opens) == 1)
            {
                Entered.TrySetResult();
                await _release.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new InvalidOperationException("No database in this test");
        }
    }
}
