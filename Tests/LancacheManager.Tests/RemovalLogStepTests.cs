using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// A game or service removal deletes cache files with no log lock, then gives each datasource its
/// own log step under the Rewrite lock: the access.log purge, the row delete and the kept
/// positions. A cancel before a log step starts records a cancel and keeps the history; a step
/// that started, or a failure after a cache step, is finished by the repair.
/// </summary>
public sealed class RemovalLogStepTests
{
    private const long GameAppId = 570;

    [Theory]
    [InlineData(OperationType.GameRemoval)]
    [InlineData(OperationType.ServiceRemoval)]
    public async Task EachDatasourceLogStepRunsUnderTheLockRightAfterItsCacheStepAsync(OperationType type)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), type);
        var rust = harness.Rust;
        rust.BetaSucceeds = true;
        rust.PurgeLinesRemoved = 5;
        rust.ReportUrls.Add("/report/url");
        if (type == OperationType.GameRemoval)
        {
            rust.ReportDepotIds.Add(1);
        }
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            foreach (var datasource in new[] { "alpha", "beta" })
            {
                // A row of the target whose depot the Steam set covers, and a row of another service.
                AddRow(seed, datasource, "steam", GameAppId, depotId: 1, "/depot/1/" + datasource);
                AddRow(seed, datasource, "epicgames", gameAppId: null, depotId: null, "/kept/" + datasource);
            }
            await seed.SaveChangesAsync();
        }
        // The live import adds a row of the target while each cache step runs.
        rust.OnRemoverRun = async run =>
        {
            await using var live = rust.Contexts.CreateDbContext();
            AddRow(live, run == 1 ? "alpha" : "beta", "steam", GameAppId, depotId: 2, "/live/" + run);
            await live.SaveChangesAsync();
        };
        ulong logEntriesRemoved = 0;
        var config = harness.CreateConfig(type, Metrics(type), async (operationId, cancellationToken, report) =>
        {
            if (type == OperationType.GameRemoval)
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                logEntriesRemoved = game.LogEntriesRemoved;
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            }

            var service = await harness.Manager.RemoveServiceFromCacheAsync(
                "steam",
                cancellationToken,
                Progress(report),
                operationId);
            logEntriesRemoved = service.LogEntriesRemoved;
            return (service.CacheFilesDeleted, checked((long)service.TotalBytesFreed));
        });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        Assert.Equal(
            new[] { (false, false), (true, true), (false, false), (true, true) },
            rust.Launches.Select(launch => (launch.Purge, launch.StepHeld)));
        var purges = rust.Launches.Where(launch => launch.Purge).Select(launch => launch.PurgeInput!.Value).ToList();
        for (var index = 0; index < 2; index++)
        {
            var datasource = index == 0 ? "alpha" : "beta";
            var urls = purges[index].GetProperty("urls").EnumerateArray().Select(url => url.GetString()).ToHashSet();
            var depots = purges[index].GetProperty("depot_ids").EnumerateArray().Select(depot => depot.GetInt64()).ToList();
            if (type == OperationType.GameRemoval)
            {
                // The stored depot already covers the lines of its rows, so their URLs stay out.
                Assert.Equal(new HashSet<string?> { "/report/url", "/live/" + (index + 1) }, urls);
                Assert.Equal(new[] { 1L }, depots);
                Assert.False(purges[index].TryGetProperty("service", out _));
            }
            else
            {
                Assert.Equal(
                    new HashSet<string?> { "/report/url", "/depot/1/" + datasource, "/live/" + (index + 1) },
                    urls);
                Assert.Empty(depots);
                Assert.Equal("steam", purges[index].GetProperty("service").GetString());
            }
        }

        await using (var check = rust.Contexts.CreateDbContext())
        {
            // Rows the import added while a cache step ran went with the lines the purge removed.
            Assert.False(await check.Downloads.AnyAsync(download => download.Service == "steam"));
            Assert.Equal(2, await check.Downloads.CountAsync(download => download.Service == "epicgames"));
            Assert.Equal(2, await check.LogEntries.CountAsync());
        }
        Assert.Equal(10UL, logEntriesRemoved);
        Assert.Equal(10UL, repair.Removal!.LogEntriesRemoved);
        Assert.All(repair.Sources, source =>
        {
            Assert.True(source.NativeCompletionAccepted);
            Assert.True(source.LogRewriteStarted);
            Assert.True(source.LogPositionsKept);
        });
        if (type == OperationType.GameRemoval)
        {
            Assert.Equal(new uint[] { 1 }, repair.Target!.SteamDepotIds);
        }
        Assert.DoesNotContain(
            harness.ReadMessages(),
            message => message.Value is RemovalProgressUpdate { StageKey: var stageKey }
                && stageKey.EndsWith(".complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelDuringTheCacheStepIsACancelAndOwesNoLogStepAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            Metrics(OperationType.GameRemoval),
            async (operationId, cancellationToken, report) =>
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        try
        {
            await harness.WaitForBetaProgressAsync();
            Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
            var terminal = await harness.WaitForTerminalAsync(operationId);
            var repair = await harness.WaitForCompletedRepairAsync(operationId);

            Assert.Equal(OperationStatus.Cancelled, terminal.Status);
            Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
            var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
            var beta = repair.Sources.Single(source => source.Datasource == "beta");
            Assert.True(alpha.LogRewriteStarted && alpha.LogPositionsKept);
            Assert.True(beta.NativeLaunchAuthorized);
            Assert.False(beta.NativeCompletionAccepted);
            Assert.False(beta.LogRewriteStarted);
            Assert.False(beta.LogPositionsKept);
            Assert.Equal(1, harness.Rust.Launches.Count(launch => launch.Purge));
            Assert.DoesNotContain(
                harness.ReadMessages(),
                message => message.Value is string text && text.Contains("One or more errors", StringComparison.Ordinal));
            Assert.True(Assert.IsType<SignalRNotifications.GameRemovalComplete>(
                await harness.WaitForCompleteMessageAsync()).Cancelled);
        }
        finally
        {
            harness.ReleaseRust();
            await harness.WaitForTerminalAsync(operationId);
        }
    }

    [Fact]
    public async Task CancelWhileTheLogStepWaitsForTheLockKeepsTheHistoryAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            await seed.SaveChangesAsync();
        }
        var other = harness.Tracker.RegisterOperation(
            OperationType.EvictionRemoval,
            "Other removal",
            new CancellationTokenSource());
        LogFileLock? held = null;
        // Another job's step takes the logs while this removal's first cache step runs.
        rust.OnRemoverRun = async _ => held = await harness.Owner.LockLogFilesAsync(
            other,
            OperationType.EvictionRemoval,
            LogFileLockKind.Rewrite,
            CancellationToken.None);
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            Metrics(OperationType.GameRemoval),
            async (operationId, cancellationToken, report) =>
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        await WaitUntilAsync(() => harness.Tracker.GetOperation(operationId)?.BlockedByName == "Other removal");
        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);
        await held!.DisposeAsync();

        Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.NativeCompletionAccepted);
        Assert.False(alpha.LogRewriteStarted);
        Assert.False(alpha.LogPositionsKept);
        Assert.False(repair.Sources.Single(source => source.Datasource == "beta").NativeLaunchAuthorized);
        Assert.Equal(new[] { false }, rust.Launches.Select(launch => launch.Purge));
        await using (var check = rust.Contexts.CreateDbContext())
        {
            Assert.Equal(1, await check.Downloads.CountAsync(download => download.GameAppId == GameAppId));
        }
        harness.Tracker.CompleteOperation(other, true);
    }

    [Fact]
    public async Task ALogStepThatStartedIsRedoneByTheRepairWithTheStoredDepotsAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.FailingPurges = 1;
        rust.PurgeLinesRemoved = 5;
        rust.ReportUrls.Add("/report/url");
        rust.ReportDepotIds.Add(1);
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            await seed.SaveChangesAsync();
        }
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            Metrics(OperationType.GameRemoval),
            async (operationId, cancellationToken, report) =>
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.Equal(1, harness.RustCalls);
        Assert.Equal(
            new[] { (false, false), (true, true), (true, true) },
            rust.Launches.Select(launch => (launch.Purge, launch.StepHeld)));
        var redo = rust.Launches.Last().PurgeInput!.Value;
        Assert.Equal(new[] { 1L }, redo.GetProperty("depot_ids").EnumerateArray().Select(depot => depot.GetInt64()));
        Assert.Equal(
            new[] { "/depot/2/alpha" },
            redo.GetProperty("urls").EnumerateArray().Select(url => url.GetString()));
        Assert.Equal(new uint[] { 1 }, repair.Target!.SteamDepotIds);
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.LogRewriteStarted);
        Assert.True(alpha.LogPositionsKept);
        Assert.Equal(5UL, repair.Removal!.LogEntriesRemoved);
        await using var check = rust.Contexts.CreateDbContext();
        Assert.False(await check.Downloads.AnyAsync(download => download.GameAppId == GameAppId));
    }

    [Fact]
    public async Task AFailedSaveAfterTheCacheStepRollsItsLogStepForwardAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.PurgeLinesRemoved = 5;
        rust.ReportDepotIds.Add(1);
        // The acceptance save right after alpha's cache step is the next repair write.
        rust.OnRemoverRun = _ =>
        {
            rust.State.FailNextRepairWrite = true;
            return Task.CompletedTask;
        };
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            Metrics(OperationType.GameRemoval),
            async (operationId, cancellationToken, report) =>
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.Equal(OperationStatus.Failed, repair.Outcome);
        Assert.Equal(new[] { (false, false), (true, true) }, rust.Launches.Select(launch => (launch.Purge, launch.StepHeld)));
        Assert.Equal(new uint[] { 1 }, repair.Target!.SteamDepotIds);
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.NativeCompletionAccepted);
        Assert.True(alpha.LogRewriteStarted);
        Assert.True(alpha.LogPositionsKept);
        Assert.Equal(9, repair.Removal!.FilesDeleted);
        Assert.Equal(5UL, repair.Removal.LogEntriesRemoved);
    }

    private static string Root() =>
        Path.Combine(Path.GetTempPath(), "lm-removal-log-step-" + Guid.NewGuid().ToString("N"));

    private static RemovalMetrics Metrics(OperationType type) => type == OperationType.ServiceRemoval
        ? new RemovalMetrics { EntityKey = "steam", EntityName = "steam", EntityKind = "service" }
        : new RemovalMetrics { EntityKey = GameAppId.ToString(), EntityName = "Dota 2", EntityKind = "steam" };

    private static Func<double, string, Dictionary<string, object?>?, int, long, Task> Progress(
        Func<RemovalProgressUpdate, Task> report) =>
        (percent, stage, context, files, bytes) => report(new RemovalProgressUpdate(percent, stage, context, files, bytes));

    private static void AddRow(
        AppDbContext context,
        string datasource,
        string service,
        long? gameAppId,
        long? depotId,
        string url)
    {
        var now = DateTime.UtcNow;
        var download = new Download
        {
            Service = service,
            ClientIp = "10.0.0.5",
            Datasource = datasource,
            GameAppId = gameAppId,
            DepotId = depotId,
            StartTimeUtc = now.AddMinutes(-1),
            EndTimeUtc = now
        };
        context.LogEntries.Add(new LogEntryRecord
        {
            Download = download,
            Service = service,
            Datasource = datasource,
            ClientIp = "10.0.0.5",
            Url = url,
            Timestamp = now,
            CreatedAt = now
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }
        Assert.True(condition());
    }
}
