using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
        // The live import adds a row of the target while each cache step runs; the log step keeps
        // it and leaves its URL out of the purge.
        rust.OnRemoverRun = async run =>
        {
            await using var live = rust.Contexts.CreateDbContext();
            AddRow(live, run == 1 ? "alpha" : "beta", "steam", GameAppId, depotId: 2, "/live/" + run);
            await live.SaveChangesAsync();
        };
        var reads = new List<(string Datasource, int PurgesBefore)>();
        harness.Importer.OnRead = datasource =>
        {
            reads.Add((datasource.Name, rust.Launches.Count(launch => launch.Purge)));
            return Task.FromResult(true);
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
        // Each purge counts read lines against the logs as they are now, so the importer reads that
        // datasource to the end right before it.
        Assert.Equal(new[] { ("alpha", 0), ("beta", 1) }, reads);
        // A line past the importer's saved position is a download it has not imported yet, so the
        // purge must leave it in the log. It reads the importer's resume record to place that position
        // after logrotate deleted a log.
        Assert.All(
            rust.Launches.Where(launch => launch.Purge),
            launch => Assert.Matches("--resume \"[^\"]*rust_resume_[^\"]+\\.json\" --keep-unread-lines", launch.Arguments));
        var purges = rust.Launches.Where(launch => launch.Purge).Select(launch => launch.PurgeInput!.Value).ToList();
        for (var index = 0; index < 2; index++)
        {
            var datasource = index == 0 ? "alpha" : "beta";
            var urls = purges[index].GetProperty("urls").EnumerateArray().Select(url => url.GetString()).ToHashSet();
            var depots = purges[index].GetProperty("depot_ids").EnumerateArray().Select(depot => depot.GetInt64()).ToList();
            if (type == OperationType.GameRemoval)
            {
                // The stored depot already covers the lines of its rows, so their URLs stay out.
                Assert.Equal(new HashSet<string?> { "/report/url" }, urls);
                Assert.Equal(new[] { 1L }, depots);
                Assert.False(purges[index].TryGetProperty("service", out _));
            }
            else
            {
                Assert.Equal(new HashSet<string?> { "/report/url", "/depot/1/" + datasource }, urls);
                Assert.Empty(depots);
                Assert.Equal("steam", purges[index].GetProperty("service").GetString());
            }
        }

        await using (var check = rust.Contexts.CreateDbContext())
        {
            // Only the rows the import added while a cache step ran stay of the steam downloads.
            Assert.Equal(2, await check.Downloads.CountAsync(download => download.Service == "steam"));
            Assert.Equal(2, await check.Downloads.CountAsync(download => download.Service == "epicgames"));
            Assert.Equal(4, await check.LogEntries.CountAsync());
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
        harness.Rust.ReportUrls.Add("/report/url");
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

    [Theory]
    [InlineData(OperationType.GameRemoval, true)]
    [InlineData(OperationType.GameRemoval, false)]
    [InlineData(OperationType.ServiceRemoval, true)]
    [InlineData(OperationType.ServiceRemoval, false)]
    public async Task ALogStepPurgesOnlyAfterAnImportThatReadEveryLogAsync(OperationType type, bool betaBehind)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), type);
        var rust = harness.Rust;
        rust.BetaSucceeds = true;
        rust.ReportUrls.Add("/report/url");
        // The repair's redo purges by the rows of the target, as it has no report.
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            AddRow(seed, "beta", "steam", GameAppId, depotId: 2, "/depot/2/beta");
            await seed.SaveChangesAsync();
        }
        // The live run's imports stop short on alpha, and on beta too when betaBehind; the repair's read to the end.
        var behind = betaBehind ? 2 : 1;
        var reads = new List<(string Datasource, bool ReadEveryLog, int PurgesBefore)>();
        harness.Importer.OnRead = datasource =>
        {
            var readEveryLog = reads.Count >= behind;
            reads.Add((datasource.Name, readEveryLog, rust.Launches.Count(launch => launch.Purge)));
            return Task.FromResult(readEveryLog);
        };
        var config = harness.CreateConfig(type, Metrics(type), async (operationId, cancellationToken, report) =>
        {
            if (type == OperationType.GameRemoval)
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            }

            var service = await harness.Manager.RemoveServiceFromCacheAsync(
                "steam",
                cancellationToken,
                Progress(report),
                operationId);
            return (service.CacheFilesDeleted, checked((long)service.TotalBytesFreed));
        });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        if (betaBehind)
        {
            // No datasource finished its log step, so the run fails with the first refusal.
            Assert.Equal(OperationStatus.Failed, terminal.Status);
            Assert.Equal("errors.unmappedRemoval.logsBehind", terminal.Message);
            Assert.Equal(
                new[] { ("alpha", false, 0), ("beta", false, 0), ("alpha", true, 0), ("beta", true, 1) },
                reads);
        }
        else
        {
            // Beta finished, so the run ends as a partial removal that names alpha.
            Assert.Equal(OperationStatus.Completed, terminal.Status);
            Assert.Equal(
                "alpha",
                Assert.Single(
                    terminal.Warnings,
                    warning => warning.StageKey == "common.notifications.warnings.datasourcesFailed")
                    .Context["datasources"]?.ToString());
            Assert.Equal(new[] { ("alpha", false, 0), ("beta", true, 0), ("alpha", true, 1) }, reads);
        }
        // Each datasource is purged once, and only by the step whose import read every log.
        Assert.Equal(2, rust.Launches.Count(launch => launch.Purge));
        Assert.All(repair.Sources, source =>
        {
            Assert.True(source.LogRewriteStarted);
            Assert.True(source.LogPositionsKept);
        });
    }

    [Fact]
    public async Task CancelDuringTheLogStepImportEndsTheRunWithoutWaitingForThePassAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.ReportUrls.Add("/report/url");
        var pass = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var passStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Importer.OnRead = _ =>
        {
            passStarted.TrySetResult();
            return pass.Task;
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
        try
        {
            await passStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
            // The pass is still running when the run ends.
            var terminal = await harness.WaitForTerminalAsync(operationId);
            var repair = await harness.WaitForCompletedRepairAsync(operationId);

            Assert.Equal(OperationStatus.Cancelled, terminal.Status);
            Assert.False(pass.Task.IsCompleted);
            Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
            var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
            Assert.False(alpha.LogRewriteStarted);
            Assert.DoesNotContain(rust.Launches, launch => launch.Purge);
        }
        finally
        {
            pass.TrySetResult(true);
        }
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

        // Beta's cache step fails too, so no datasource finished and the run fails.
        harness.ReleaseRust();
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.Equal(2, harness.RustCalls);
        Assert.Equal(
            new[] { (false, false), (true, true), (false, false), (true, true) },
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
    public async Task APurgeThatRefusesBeforeItRewritesALogIsRedoneAfterAFreshImportAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        // The binary refuses when a rotation left no log that shows where the importer stopped.
        rust.RefusedPurges = 1;
        rust.PurgeLinesRemoved = 5;
        rust.ReportDepotIds.Add(1);
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            await seed.SaveChangesAsync();
        }
        var purgesBeforeRead = new List<int>();
        harness.Importer.OnRead = _ =>
        {
            purgesBeforeRead.Add(rust.Launches.Count(launch => launch.Purge));
            return Task.FromResult(true);
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

        // Beta's cache step fails too, so no datasource finished and the run fails.
        harness.ReleaseRust();
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        // The redo imports again after the refusal, so its purge has a fresh read point.
        Assert.Equal([0, 1], purgesBeforeRead);
        Assert.Equal(2, rust.Launches.Count(launch => launch.Purge));
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.LogRewriteStarted);
        Assert.True(alpha.LogPositionsKept);
        Assert.Equal(5UL, repair.Removal!.LogEntriesRemoved);
        await using var check = rust.Contexts.CreateDbContext();
        Assert.False(await check.Downloads.AnyAsync(download => download.GameAppId == GameAppId));
    }

    [Theory]
    [InlineData(OperationType.GameRemoval)]
    [InlineData(OperationType.ServiceRemoval)]
    public async Task ADownloadLoggedDuringTheCacheStepKeepsItsRowsAsync(OperationType type)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), type);
        var rust = harness.Rust;
        rust.BetaSucceeds = true;
        rust.PurgeLinesRemoved = 5;
        if (type == OperationType.GameRemoval)
        {
            rust.ReportDepotIds.Add(1);
        }
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            foreach (var datasource in new[] { "alpha", "beta" })
            {
                AddRow(seed, datasource, "steam", GameAppId, depotId: 1, "/old/" + datasource);
            }
            await seed.SaveChangesAsync();
        }
        // The live import logs two more downloads of the target while each cache step runs: one on a
        // depot the record already covers and one with no depot.
        rust.OnRemoverRun = async run =>
        {
            var datasource = run == 1 ? "alpha" : "beta";
            await using var live = rust.Contexts.CreateDbContext();
            AddRow(live, datasource, "steam", GameAppId, depotId: 1, "/late/covered/" + datasource);
            AddRow(live, datasource, "steam", GameAppId, depotId: null, "/late/free/" + datasource);
            await live.SaveChangesAsync();
        };
        var config = harness.CreateConfig(type, Metrics(type), async (operationId, cancellationToken, report) =>
        {
            if (type == OperationType.GameRemoval)
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            }

            var service = await harness.Manager.RemoveServiceFromCacheAsync(
                "steam",
                cancellationToken,
                Progress(report),
                operationId);
            return (service.CacheFilesDeleted, checked((long)service.TotalBytesFreed));
        });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        await using var check = rust.Contexts.CreateDbContext();
        var keptUrls = await check.LogEntries.Select(logEntry => logEntry.Url).OrderBy(url => url).ToListAsync();
        Assert.Equal(
            new[] { "/late/covered/alpha", "/late/covered/beta", "/late/free/alpha", "/late/free/beta" },
            keptUrls);
        Assert.Equal(4, await check.Downloads.CountAsync(download => download.Service == "steam"));
    }

    [Fact]
    public async Task ARepairedLogStepKeepsRowsLoggedAfterTheMarkAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.FailingPurges = 1;
        rust.PurgeLinesRemoved = 5;
        rust.ReportDepotIds.Add(1);
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/old/alpha");
            await seed.SaveChangesAsync();
        }
        rust.OnRemoverRun = async run =>
        {
            if (run != 1)
            {
                return;
            }

            await using var live = rust.Contexts.CreateDbContext();
            AddRow(live, "alpha", "steam", GameAppId, depotId: null, "/late/alpha");
            await live.SaveChangesAsync();
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

        harness.ReleaseRust();
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.True(repair.Sources.Single(source => source.Datasource == "alpha").LogPositionsKept);
        await using var check = rust.Contexts.CreateDbContext();
        Assert.Equal(
            new[] { "/late/alpha" },
            await check.LogEntries.Select(logEntry => logEntry.Url).ToListAsync());
        Assert.Equal(1, await check.Downloads.CountAsync(download => download.GameAppId == GameAppId));
    }

    [Fact]
    public async Task AFailedSaveAfterTheCacheStepRollsItsLogStepForwardAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.PurgeLinesRemoved = 5;
        rust.ReportDepotIds.Add(1);
        // The acceptance save right after alpha's cache step is the next repair write.
        rust.OnRemoverRun = run =>
        {
            if (run == 1)
            {
                rust.State.FailNextRepairWrite = true;
            }
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

        // Beta's cache step fails too, so no datasource finished and the run fails.
        harness.ReleaseRust();
        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.Equal(OperationStatus.Failed, repair.Outcome);
        Assert.Equal(
            new[] { (false, false), (false, false), (true, true) },
            rust.Launches.Select(launch => (launch.Purge, launch.StepHeld)));
        Assert.Equal(new uint[] { 1 }, repair.Target!.SteamDepotIds);
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.NativeCompletionAccepted);
        Assert.True(alpha.LogRewriteStarted);
        Assert.True(alpha.LogPositionsKept);
        Assert.Equal(9, repair.Removal!.FilesDeleted);
        Assert.Equal(5UL, repair.Removal.LogEntriesRemoved);
    }

    [Fact]
    public async Task CancelDuringThePurgeIsStoredAsACancelAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.HeldPurges = 1;
        rust.ReportDepotIds.Add(1);
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
        await WaitUntilAsync(() => rust.Launches.Any(launch => launch.Purge));
        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);

        Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        Assert.DoesNotContain(
            harness.ReadMessages(),
            message => message.Value is string text && text.Contains("One or more errors", StringComparison.Ordinal));
        // The repair redoes the step the cancel cut short.
        Assert.Equal(2, rust.Launches.Count(launch => launch.Purge));
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.LogRewriteStarted);
        Assert.True(alpha.LogPositionsKept);
    }

    [Theory]
    [InlineData(OperationType.GameRemoval)]
    [InlineData(OperationType.ServiceRemoval)]
    public async Task ADatasourceWithNothingToPurgeEndsCleanlyAsync(OperationType type)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), type);
        var rust = harness.Rust;
        rust.BetaSucceeds = true;
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            await seed.SaveChangesAsync();
        }
        // Beta's step removes no line, so the lines its import has read stay read.
        rust.State.SetLogSourcePositions("beta", new Dictionary<string, long> { ["access"] = 7 });
        var config = harness.CreateConfig(type, Metrics(type), async (operationId, cancellationToken, report) =>
        {
            if (type == OperationType.GameRemoval)
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            }

            var service = await harness.Manager.RemoveServiceFromCacheAsync(
                "steam",
                cancellationToken,
                Progress(report),
                operationId);
            return (service.CacheFilesDeleted, checked((long)service.TotalBytesFreed));
        });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);
        Assert.Equal(OperationStatus.Completed, repair.Outcome);
        var purge = Assert.Single(rust.Launches, launch => launch.Purge);
        Assert.Equal(
            new[] { "/depot/2/alpha" },
            purge.PurgeInput!.Value.GetProperty("urls").EnumerateArray().Select(url => url.GetString()));
        var beta = repair.Sources.Single(source => source.Datasource == "beta");
        Assert.True(beta.NativeCompletionAccepted);
        Assert.True(beta.LogRewriteStarted);
        Assert.True(beta.LogPositionsKept);
        Assert.Equal(7, rust.State.GetLogPosition("beta"));
        Assert.Equal(new Dictionary<string, long> { ["access"] = 7 }, rust.State.GetLogSourcePositions("beta"));
    }

    [Theory]
    [InlineData(OperationType.GameRemoval)]
    [InlineData(OperationType.ServiceRemoval)]
    public async Task ARemovalThatFailedOnOneDatasourceEndsAmberAndKeepsItListedAsync(OperationType type)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), type);
        var rust = harness.Rust;
        // Beta's cache step fails at once: the fake writes its report and exits 17, as a real remover
        // writes its report on a failure exit before the host enforces the exit code
        // (CacheManagementService RunRustRemovalProcessAsync).
        harness.ReleaseRust();
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            AddRow(seed, "beta", "steam", GameAppId, depotId: 2, "/depot/2/beta");
            if (type == OperationType.GameRemoval)
            {
                seed.CachedGameDetections.Add(new CachedGameDetection { GameAppId = GameAppId, GameName = "Dota 2" });
            }
            else
            {
                seed.CachedServiceDetections.Add(new CachedServiceDetection { ServiceName = "steam" });
            }
            await seed.SaveChangesAsync();
        }
        var entityKept = false;
        var config = harness.CreateConfig(type, Metrics(type), async (operationId, cancellationToken, report) =>
        {
            if (type == OperationType.GameRemoval)
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                entityKept = game.EntityKept;
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            }

            var service = await harness.Manager.RemoveServiceFromCacheAsync(
                "steam",
                cancellationToken,
                Progress(report),
                operationId);
            entityKept = service.EntityKept;
            return (service.CacheFilesDeleted, checked((long)service.TotalBytesFreed));
        });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(
            harness.Tracker,
            harness.NotificationService,
            config);
        var terminal = await harness.WaitForTerminalAsync(operationId);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);
        var complete = await harness.WaitForCompleteMessageAsync();

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var warning = Assert.Single(
            terminal.Warnings,
            item => item.StageKey == "common.notifications.warnings.datasourcesFailed");
        Assert.Equal("beta", warning.Context["datasources"]);
        Assert.True(entityKept);
        Assert.False(type == OperationType.GameRemoval
            ? Assert.IsType<SignalRNotifications.GameRemovalComplete>(complete).Cancelled
            : Assert.IsType<SignalRNotifications.ServiceRemovalComplete>(complete).Cancelled);
        await using (var check = rust.Contexts.CreateDbContext())
        {
            Assert.Equal(0, await check.Downloads.CountAsync(download => download.Datasource == "alpha" && download.Service == "steam"));
            Assert.Equal(1, await check.Downloads.CountAsync(download => download.Datasource == "beta" && download.Service == "steam"));
            Assert.True(type == OperationType.GameRemoval
                ? await check.CachedGameDetections.AnyAsync(detection => detection.GameAppId == GameAppId)
                : await check.CachedServiceDetections.AnyAsync(detection => detection.ServiceName == "steam"));
        }
        Assert.Equal(OperationStatus.Completed, repair.Outcome);
        Assert.Contains(repair.Warnings!, item => item.StageKey == "common.notifications.warnings.datasourcesFailed");
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.NativeCompletionAccepted);
        Assert.True(alpha.LogRewriteStarted);
        Assert.True(alpha.LogPositionsKept);
        var beta = repair.Sources.Single(source => source.Datasource == "beta");
        Assert.True(beta.NativeLaunchAuthorized);
        Assert.False(beta.NativeCompletionAccepted);
    }

    [Fact]
    public async Task ALogStepWithNoLogFileLeftStillRemovesTheHistoryAsync()
    {
        var root = Root();
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(root, OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.BetaSucceeds = true;
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            await seed.SaveChangesAsync();
        }
        // Alpha's log folder is emptied while its cache step runs, as a log mount that went empty.
        rust.OnRemoverRun = run =>
        {
            if (run == 1)
            {
                File.Delete(Path.Combine(root, "alpha-logs", "access.log"));
            }
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

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var repair = await harness.WaitForCompletedRepairAsync(operationId);
        Assert.DoesNotContain(rust.Launches, launch => launch.Purge);
        var alpha = repair.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(alpha.LogRewriteStarted);
        Assert.True(alpha.LogPositionsKept);
        await using var check = rust.Contexts.CreateDbContext();
        Assert.False(await check.Downloads.AnyAsync(download => download.GameAppId == GameAppId));
    }

    [Fact]
    public async Task AGameRemovalWhosePurgeHitAPermissionErrorNamesTheDatasourceAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.BetaSucceeds = true;
        rust.PurgeLinesRemoved = 5;
        rust.PurgePermissionErrors = 1;
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            foreach (var datasource in new[] { "alpha", "beta" })
            {
                AddRow(seed, datasource, "steam", GameAppId, depotId: 2, "/depot/2/" + datasource);
            }
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

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var warning = Assert.Single(terminal.Warnings);
        Assert.Equal("common.notifications.warnings.logLinesKept", warning.StageKey);
        Assert.Equal("alpha, beta", warning.Context["datasources"]);
        Assert.Equal("common.notifications.warnings.logLinesKept", Assert.Single(repair.Warnings!).StageKey);
    }

    [Theory]
    [InlineData(OperationType.GameRemoval)]
    [InlineData(OperationType.ServiceRemoval)]
    public async Task ARemovalCanceledAfterADatasourceFailedNamesItOnItsGrayCardAsync(OperationType type)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), type);
        var rust = harness.Rust;
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            AddRow(seed, "beta", "steam", GameAppId, depotId: 2, "/depot/2/beta");
            await seed.SaveChangesAsync();
        }
        // Alpha's remover cannot be launched, so its datasource fails; beta's remover then reports
        // progress and waits until the cancel.
        rust.OnRemoverRun = run => run == 1
            ? throw new InvalidOperationException("Injected alpha launch failure.")
            : Task.CompletedTask;
        var config = harness.CreateConfig(type, Metrics(type), async (operationId, cancellationToken, report) =>
        {
            if (type == OperationType.GameRemoval)
            {
                var game = await harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress(report),
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            }

            var service = await harness.Manager.RemoveServiceFromCacheAsync(
                "steam",
                cancellationToken,
                Progress(report),
                operationId);
            return (service.CacheFilesDeleted, checked((long)service.TotalBytesFreed));
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
            var warning = Assert.Single(terminal.Warnings);
            Assert.Equal("common.notifications.warnings.datasourcesFailed", warning.StageKey);
            Assert.Equal("alpha", warning.Context["datasources"]);
            Assert.Equal(
                "common.notifications.warnings.datasourcesFailed",
                Assert.Single(repair.Warnings!).StageKey);
        }
        finally
        {
            harness.ReleaseRust();
            await harness.WaitForTerminalAsync(operationId);
        }
    }

    [Fact]
    public async Task ARestoredKeptLinesWarningStillNamesItsDatasourceAfterAnotherPermissionErrorAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        rust.BetaSucceeds = true;
        rust.PurgeLinesRemoved = 5;
        rust.PurgePermissionErrors = 1;
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            foreach (var datasource in new[] { "alpha", "beta" })
            {
                AddRow(seed, datasource, "steam", GameAppId, depotId: 2, "/depot/2/" + datasource);
            }
            await seed.SaveChangesAsync();
        }
        var runId = Guid.Empty;
        // A warning restored after a restart holds its context values as JSON elements. Beta's remover
        // runs after alpha's purge named alpha, so this puts alpha's name back in the restored form.
        rust.OnRemoverRun = async run =>
        {
            if (run == 2)
            {
                await harness.Owner.SetRunWarningAsync(runId, new RunWarning(
                    "common.notifications.warnings.logLinesKept",
                    new Dictionary<string, object?> { ["datasources"] = JsonDocument.Parse("\"alpha\"").RootElement }));
            }
        };
        var config = harness.CreateConfig(
            OperationType.GameRemoval,
            Metrics(OperationType.GameRemoval),
            async (operationId, cancellationToken, report) =>
            {
                runId = operationId;
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

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        var warning = Assert.Single(terminal.Warnings);
        Assert.Equal("common.notifications.warnings.logLinesKept", warning.StageKey);
        Assert.Equal("alpha, beta", warning.Context["datasources"]);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task AGameRemovalThatFailedOnOneDatasourceKeepsItsDetectionRowThroughTheControllerAsync(
        bool betaSucceeds,
        int expectedDetectionRemovals)
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.GameRemoval);
        var rust = harness.Rust;
        // Beta's cache step fails at once unless it succeeds: the fake writes its report and exits 17.
        rust.BetaSucceeds = betaSucceeds;
        harness.ReleaseRust();
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            AddRow(seed, "beta", "steam", GameAppId, depotId: 2, "/depot/2/beta");
            await seed.SaveChangesAsync();
        }
        var controller = new GamesController(
            gameCacheDetectionService: null!,
            gameDetectionService: null!,
            cacheManagementService: harness.Manager,
            notifications: harness.NotificationService,
            logger: NullLogger<GamesController>.Instance,
            pathResolver: null!,
            operationTracker: harness.Tracker,
            conflictChecker: null!,
            operationQueue: null!,
            capabilityService: null!,
            cacheScanGate: CacheScanGateHarness.Idle());
        var detectionRemovals = new List<long>();
        Func<Guid, CancellationToken, Func<double, string, Dictionary<string, object?>?, int, long, Task>,
            Task<CacheManagementService.GameCacheRemovalReport>> removeFunc =
            (operationId, cancellationToken, onProgress) => harness.Manager.RemoveGameFromCacheAsync(
                GameAppId,
                cancellationToken,
                onProgress,
                operationId);
        Func<long, Task> onSuccess = appId =>
        {
            detectionRemovals.Add(appId);
            return Task.CompletedTask;
        };

        var operationId = await (Task<Guid>)typeof(GamesController)
            .GetMethod("StartRemovalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                controller,
                [GameAppId.ToString(), "Dota 2", "Game Removal: Dota 2", GameAppId, "steam", null, null, removeFunc, onSuccess])!;
        var terminal = await harness.WaitForTerminalAsync(operationId);
        // The controller's own config names the complete event after the SignalR event, not "complete".
        SignalRNotifications.GameRemovalComplete? complete = null;
        for (var attempt = 0; attempt < 400 && complete is null; attempt++)
        {
            complete = harness.ReadMessages()
                .Select(message => message.Value)
                .OfType<SignalRNotifications.GameRemovalComplete>()
                .SingleOrDefault();
            await Task.Delay(25);
        }

        Assert.Equal(OperationStatus.Completed, terminal.Status);
        Assert.Equal(!betaSucceeds, Assert.IsType<SignalRNotifications.GameRemovalComplete>(complete).EntityKept);
        Assert.Equal(expectedDetectionRemovals, detectionRemovals.Count);
    }

    // Beta's cache step fails, so alpha's log step decides how the run ends. The import of alpha
    // never reads every log: its log is behind, or its folder is empty with or without a series
    // read before, or the read itself fails. A reset to the beginning, while the log is there or
    // after it is gone, once or twice, still leaves the emptied folder's series missing. A reset
    // to the end after that folder empties keeps the series stored at 0. A series stored at 0
    // that disappears while another series remains is still missing.
    [Theory]
    [InlineData("behind", "errors.unmappedRemoval.logsBehind")]
    [InlineData("empty", null)]
    [InlineData("missing", "errors.unmappedRemoval.logsMissing")]
    [InlineData("fault", "signalr.gameRemove.error.fatal")]
    [InlineData("reset", "errors.unmappedRemoval.logsMissing")]
    [InlineData("resetAll", "errors.unmappedRemoval.logsMissing")]
    [InlineData("resetTwice", "errors.unmappedRemoval.logsMissing")]
    [InlineData("goneAtReset", "errors.unmappedRemoval.logsMissing")]
    [InlineData("ended", "errors.unmappedRemoval.logsMissing")]
    [InlineData("zeroBeside", "errors.unmappedRemoval.logsMissing")]
    public async Task AGameRemovalLogStepThatCannotReadEveryLogEndsByItsKeyAsync(string alphaLogs, string? failureKey)
    {
        var root = Root();
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(root, OperationType.GameRemoval);
        var rust = harness.Rust;
        harness.ReleaseRust();
        rust.ReportUrls.Add("/report/url");
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            AddRow(seed, "beta", "steam", GameAppId, depotId: 2, "/depot/2/beta");
            await seed.SaveChangesAsync();
        }
        if (alphaLogs is not ("behind" or "empty" or "fault"))
        {
            rust.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { [LogSourceLayout.MonolithicStem] = 5 });
        }
        if (alphaLogs == "zeroBeside")
        {
            File.WriteAllText(Path.Combine(root, "alpha-logs", "steam-access.log"), "GET /steam HTTP/1.1\n");
        }
        if (alphaLogs is "reset" or "resetTwice" or "ended" or "zeroBeside")
        {
            harness.CreateLogProcessor().ResetLogPosition("alpha");
        }
        if (alphaLogs is "reset" or "resetTwice")
        {
            // The reset keeps the series at 0, and a restart reads it back from disk.
            var restarted = OperationRepairTests.CreateFailingStateService(Path.Combine(root, "retained"));
            var kept = Assert.Single(restarted.GetLogSourcePositions("alpha"));
            Assert.Equal((LogSourceLayout.MonolithicStem, 0L), (kept.Key, kept.Value));
        }
        if (alphaLogs == "resetAll")
        {
            harness.CreateLogProcessor().ResetLogPosition();
        }
        if (alphaLogs is not ("behind" or "fault" or "zeroBeside"))
        {
            File.Delete(Path.Combine(root, "alpha-logs", LogSourceLayout.MonolithicStem));
        }
        if (alphaLogs == "zeroBeside")
        {
            File.Delete(Path.Combine(root, "alpha-logs", "steam-access.log"));
        }
        if (alphaLogs is "resetTwice" or "goneAtReset")
        {
            harness.CreateLogProcessor().ResetLogPosition("alpha");
        }
        if (alphaLogs == "ended")
        {
            // The folder is empty, so a count finds no series. The reset-to-end map is the
            // series saved before and not found now, including one stored at 0.
            var positions = RustLogProcessorService.MissingSeriesPositions(
                rust.State.GetLogSourcePositions("alpha"),
                LogSourceLayout.EnumerateStems(Path.Combine(root, "alpha-logs")));
            rust.State.SetLogSourcePositions("alpha", positions);
            rust.State.SetLogPosition("alpha", 0);
            rust.State.SetLogTotalLines("alpha", 0);
            var kept = Assert.Single(rust.State.GetLogSourcePositions("alpha"));
            Assert.Equal((LogSourceLayout.MonolithicStem, 0L), (kept.Key, kept.Value));
        }
        harness.Importer.OnRead = datasource =>
        {
            if (alphaLogs == "fault")
            {
                return Task.FromException<bool>(new InvalidOperationException("Injected read failure."));
            }

            if (alphaLogs == "zeroBeside"
                && string.Equals(datasource.Name, "alpha", StringComparison.OrdinalIgnoreCase))
            {
                datasource.RefreshLogSources();
                return Task.FromResult(!RustLogProcessorService.LogsOutOfSight(
                    rust.State.GetLogSourcePositions(datasource.Name),
                    datasource.LogSourceStems));
            }

            return Task.FromResult(false);
        };
        var controller = new GamesController(
            gameCacheDetectionService: null!,
            gameDetectionService: null!,
            cacheManagementService: harness.Manager,
            notifications: harness.NotificationService,
            logger: NullLogger<GamesController>.Instance,
            pathResolver: null!,
            operationTracker: harness.Tracker,
            conflictChecker: null!,
            operationQueue: null!,
            capabilityService: null!,
            cacheScanGate: CacheScanGateHarness.Idle());
        Func<Guid, CancellationToken, Func<double, string, Dictionary<string, object?>?, int, long, Task>,
            Task<CacheManagementService.GameCacheRemovalReport>> removeFunc =
            (operationId, cancellationToken, onProgress) => harness.Manager.RemoveGameFromCacheAsync(
                GameAppId,
                cancellationToken,
                onProgress,
                operationId);
        Func<long, Task> onSuccess = _ => Task.CompletedTask;

        var operationId = await (Task<Guid>)typeof(GamesController)
            .GetMethod("StartRemovalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                controller,
                [GameAppId.ToString(), "Dota 2", "Game Removal: Dota 2", GameAppId, "steam", null, null, removeFunc, onSuccess])!;
        var terminal = await harness.WaitForTerminalAsync(operationId);
        SignalRNotifications.GameRemovalComplete? complete = null;
        for (var attempt = 0; attempt < 400 && complete is null; attempt++)
        {
            complete = harness.ReadMessages()
                .Select(message => message.Value)
                .OfType<SignalRNotifications.GameRemovalComplete>()
                .SingleOrDefault();
            await Task.Delay(25);
        }

        Assert.NotNull(complete);
        if (failureKey is null)
        {
            // Nothing to purge: the step ends, the target's rows go and beta's failure keeps the game listed.
            Assert.Equal(OperationStatus.Completed, terminal.Status);
            Assert.True(complete.EntityKept);
            Assert.DoesNotContain(rust.Launches, launch => launch.Purge);
            var alpha = harness.ReadRepair(operationId).Sources.Single(source => source.Datasource == "alpha");
            Assert.True(alpha.LogRewriteStarted && alpha.LogPositionsKept);
            await using var check = rust.Contexts.CreateDbContext();
            Assert.False(await check.Downloads.AnyAsync(download => download.Datasource == "alpha"));
            return;
        }

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.Equal(failureKey, complete.StageKey);
        // A keyed refusal sends no English error, which the browser would show ahead of the key.
        Assert.Equal(alphaLogs == "fault" ? "Injected read failure." : null, complete.Error);
        // A card drawn from the run after a reload reads the refusal from the run's key.
        Assert.Equal(alphaLogs == "fault" ? "Injected read failure." : failureKey, terminal.Message);
        var progress = harness.ReadMessages()
            .Select(message => message.Value)
            .OfType<SignalRNotifications.GameRemovalProgress>()
            .Last();
        Assert.Equal(failureKey, progress.StageKey);
        Assert.DoesNotContain(rust.Launches, launch => launch.Purge);
    }

    [Fact]
    public async Task AServiceRemovalRefusedByItsLogStepEndsByTheRefusalKeyAsync()
    {
        await using var harness = await RemovalRepairHarness.CreateProducerAsync(Root(), OperationType.ServiceRemoval);
        var rust = harness.Rust;
        harness.ReleaseRust();
        await using (var seed = rust.Contexts.CreateDbContext())
        {
            AddRow(seed, "alpha", "steam", GameAppId, depotId: 2, "/depot/2/alpha");
            AddRow(seed, "beta", "steam", GameAppId, depotId: 2, "/depot/2/beta");
            await seed.SaveChangesAsync();
        }
        // Alpha's import never reads its log to the end and beta's cache step fails, so the run fails
        // with alpha's refusal.
        harness.Importer.OnRead = _ => Task.FromResult(false);

        var accepted = Assert.IsType<AcceptedResult>(await harness.CreateCacheController()
            .ClearServiceCacheAsync("steam", CancellationToken.None, CacheRemovalScope.CacheFiles));
        var operationId = Assert.IsType<CacheOperationResponse>(accepted.Value).OperationId!.Value;
        var terminal = await harness.WaitForTerminalAsync(operationId);
        SignalRNotifications.ServiceRemovalComplete? complete = null;
        for (var attempt = 0; attempt < 400 && complete is null; attempt++)
        {
            complete = harness.ReadMessages()
                .Select(message => message.Value)
                .OfType<SignalRNotifications.ServiceRemovalComplete>()
                .SingleOrDefault();
            await Task.Delay(25);
        }

        Assert.Equal(OperationStatus.Failed, terminal.Status);
        Assert.NotNull(complete);
        Assert.Equal("errors.unmappedRemoval.logsBehind", complete.StageKey);
        // A keyed refusal sends no English error, which the browser would show ahead of the key.
        Assert.Null(complete.Error);
        var progress = harness.ReadMessages()
            .Select(message => message.Value)
            .OfType<SignalRNotifications.ServiceRemovalProgress>()
            .Last();
        Assert.Equal("errors.unmappedRemoval.logsBehind", progress.StageKey);
        Assert.Equal("errors.unmappedRemoval.logsBehind", terminal.Message);
    }

    [Fact]
    public async Task ALogStepWaitingForAnotherPassLogsNoRefusedStartAsync()
    {
        using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var logger = new CapturingLogger<RustLogProcessorService>();
        var processor = new RustLogProcessorService(
            logger,
            pathResolver: null!,
            notifications: null!,
            stateService: null!,
            serviceProvider: null!,
            rustProcessHelper: null!,
            datasourceService: null!,
            tracker,
            TimeProvider.System);
        var reservation = await processor.TryReserveProcessingGateAsync();
        Assert.NotNull(reservation);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ReadToEndAsync(
            new ResolvedDatasource { Name = "alpha", LogPath = Root() },
            wait.Token));

        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("already running", StringComparison.Ordinal));
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
