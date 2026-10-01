using System.Data.Common;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

/// <summary>
/// Covers diagnostic orphan classification, cache-split aliases, retained observation history,
/// datasource attribution normalization, and empty game identity repair.
/// </summary>
public class DownloadCleanupServiceTests
{
    // ---------------------------------------------------------------------------------------------
    // Pure classification - data-loss guard (no DB provider needed)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ComputeOrphanedServices_XboxPresentViaWsusAlias_NotOrphaned()
    {
        // 'xbox' is absent from the logs under its own name, but its cache lives under 'wsus', which
        // IS present -> xbox must NOT be flagged orphaned (else every Xbox download gets deleted).
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam", "wsus" });

        Assert.DoesNotContain("xbox", orphans);
        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxOnlyService_PresentViaWsusAlias_NotOrphaned()
    {
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox" },
            new HashSet<string> { "wsus" });

        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxPresentViaXboxliveAlias_NotOrphaned()
    {
        // Prefill-daemon traffic is tagged 'xboxlive' rather than 'wsus', and on a box that pulls its
        // Xbox content that way 'wsus' is a small aging tail that rotates out of the logs first.
        // Either alias proves the Xbox cache is still in use, so 'xboxlive' alone must protect it.
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam", "xboxlive" });

        Assert.DoesNotContain("xbox", orphans);
        Assert.Empty(orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_XboxWithNoCacheAliasInLogs_IsOrphaned()
    {
        // The aliases only protect xbox while its cache is still present. With both wsus and xboxlive
        // gone, xbox is genuinely orphaned - the guard is conditional, not an unconditional whitelist.
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "xbox", "steam" },
            new HashSet<string> { "steam" });

        Assert.Contains("xbox", orphans);
    }

    [Fact]
    public void ComputeOrphanedServices_GenuineOrphanDetected_PresentServiceKept()
    {
        var orphans = DownloadCleanupService.ComputeOrphanedServices(
            new[] { "origin", "steam" },
            new HashSet<string> { "steam", "wsus" });

        Assert.Contains("origin", orphans);
        Assert.DoesNotContain("steam", orphans);
    }

    [Fact]
    public async Task PeriodicCleanupWaitsForIngestAndReadsCommittedContinuationAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        List<long> ids;
        await using (var seed = database.Factory.CreateDbContext())
        {
            var first = NewCleanupDownload("10.0.0.31", now.AddMinutes(-2), now.AddMinutes(-2));
            var second = NewCleanupDownload("10.0.0.32", now.AddMinutes(-2), now.AddMinutes(-2));
            seed.Downloads.AddRange(first, second);
            await seed.SaveChangesAsync();
            ids = [first.Id, second.Id];
        }

        var recorder = new RecordingCommandInterceptor();
        var options = RetryOptions(database, recorder);
        await using var cleanupContext = new AppDbContext(options);
        await cleanupContext.Database.OpenConnectionAsync();
        var cleanupPid = ((NpgsqlConnection)cleanupContext.Database.GetDbConnection()).ProcessID;

        await using var ingest = new NpgsqlConnection(ConnectionString(database));
        await ingest.OpenAsync();
        await using var ingestTransaction = await ingest.BeginTransactionAsync();
        await LockDownloadsAsync(ingest, ingestTransaction, CancellationToken.None);

        var cleanupTask = RunPeriodicAsync(cleanupContext, CancellationToken.None);
        var committed = false;
        try
        {
            await WaitForDownloadLockAsync(ConnectionString(database), cleanupPid);
            Assert.DoesNotContain(recorder.Commands, command => IsCleanupSelect(command));

            await WriteContinuationAsync(
                ingest,
                ingestTransaction,
                ids,
                now,
                CancellationToken.None);
            await ingestTransaction.CommitAsync();
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await ingestTransaction.RollbackAsync();
            }

            await cleanupTask;
        }

        await AssertContinuationAsync(database, ids, now);
    }

    [Fact]
    public async Task PeriodicCleanupMakesIngestWaitAtTheTableBoundaryAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        List<long> ids;
        await using (var seed = database.Factory.CreateDbContext())
        {
            var first = NewCleanupDownload("10.0.0.33", now.AddMinutes(-2), now.AddMinutes(-2));
            var second = NewCleanupDownload("10.0.0.34", now.AddMinutes(-2), now.AddMinutes(-2));
            seed.Downloads.AddRange(first, second);
            await seed.SaveChangesAsync();
            ids = [first.Id, second.Id];
        }

        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var gate = new CleanupCommandGate
        {
            ReaderFinished = async (command, cancellationToken) =>
            {
                if (IsCleanupSelect(command) && Interlocked.Exchange(ref held, 1) == 0)
                {
                    selected.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            }
        };
        var options = RetryOptions(database, gate);
        await using var cleanupContext = new AppDbContext(options);
        var cleanupTask = RunPeriodicAsync(cleanupContext, CancellationToken.None);

        await using var ingest = new NpgsqlConnection(ConnectionString(database));
        await ingest.OpenAsync();
        await using var ingestTransaction = await ingest.BeginTransactionAsync();
        Task ingestTask = Task.CompletedTask;
        try
        {
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ingestTask = ContinueDownloadsAsync(
                ingest,
                ingestTransaction,
                ids,
                now,
                CancellationToken.None);
            await WaitForDownloadLockAsync(ConnectionString(database), ingest.ProcessID);
            Assert.False(ingestTask.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await cleanupTask;
            await ingestTask;
        }

        await AssertContinuationAsync(database, ids, now);
    }

    [Fact]
    public async Task ProtectedSelectionPreventsAContinuationCommitUntilCleanupCommitsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        long id;
        await using (var seed = database.Factory.CreateDbContext())
        {
            var row = NewCleanupDownload("10.0.0.35", now.AddMinutes(-2), now.AddMinutes(-2));
            seed.Downloads.Add(row);
            await seed.SaveChangesAsync();
            id = row.Id;
        }

        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var gate = new CleanupCommandGate
        {
            ReaderFinished = async (command, cancellationToken) =>
            {
                if (IsCleanupSelect(command) && Interlocked.Exchange(ref held, 1) == 0)
                {
                    selected.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            }
        };
        var options = RetryOptions(database, gate);
        await using var cleanupContext = new AppDbContext(options);
        var cleanupTask = RunPeriodicAsync(cleanupContext, CancellationToken.None);

        await using var ingest = new NpgsqlConnection(ConnectionString(database));
        await ingest.OpenAsync();
        await using var ingestTransaction = await ingest.BeginTransactionAsync();
        Task ingestTask = Task.CompletedTask;
        try
        {
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ingestTask = ContinueDownloadsAsync(
                ingest,
                ingestTransaction,
                [id],
                now,
                CancellationToken.None);
            await WaitForDownloadLockAsync(ConnectionString(database), ingest.ProcessID);
            Assert.False(ingestTask.IsCompleted);

            await using var observer = database.Factory.CreateDbContext();
            var beforeCommit = await observer.Downloads.SingleAsync(row => row.Id == id);
            Assert.Equal("/stale", beforeCommit.LastUrl);
            Assert.True(beforeCommit.IsActive);
        }
        finally
        {
            release.TrySetResult();
            await cleanupTask;
            await ingestTask;
        }

        await AssertContinuationAsync(database, [id], now);
    }

    [Fact]
    public async Task RetryLocksAgainAndSelectsFreshRowsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        List<long> ids;
        await using (var seed = database.Factory.CreateDbContext())
        {
            var first = NewCleanupDownload("10.0.0.36", now.AddMinutes(-2), now.AddMinutes(-2));
            var second = NewCleanupDownload("10.0.0.37", now.AddMinutes(-2), now.AddMinutes(-2));
            seed.Downloads.AddRange(first, second);
            await seed.SaveChangesAsync();
            ids = [first.Id, second.Id];
        }

        var secondLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockAttempts = 0;
        var updateAttempts = 0;
        var events = new List<string>();
        var gate = new CleanupCommandGate
        {
            NonQueryStarting = async (command, cancellationToken) =>
            {
                if (IsDownloadsLock(command))
                {
                    lock (events)
                    {
                        events.Add("lock");
                    }

                    if (Interlocked.Increment(ref lockAttempts) == 2)
                    {
                        secondLock.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                    }
                }
                else if (IsCleanupUpdate(command))
                {
                    lock (events)
                    {
                        events.Add("update");
                    }

                    if (Interlocked.Increment(ref updateAttempts) == 1)
                    {
                        throw new PostgresException(
                            "Injected transient cleanup failure",
                            "ERROR",
                            "ERROR",
                            PostgresErrorCodes.SerializationFailure);
                    }
                }
            },
            ReaderFinished = (command, _) =>
            {
                if (IsCleanupSelect(command))
                {
                    lock (events)
                    {
                        events.Add("select");
                    }
                }

                return Task.CompletedTask;
            }
        };
        var options = RetryOptions(database, gate);
        await using var cleanupContext = new AppDbContext(options);
        var cleanupTask = RunPeriodicAsync(cleanupContext, CancellationToken.None);

        try
        {
            await secondLock.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var ingest = new NpgsqlConnection(ConnectionString(database));
            await ingest.OpenAsync();
            await using var ingestTransaction = await ingest.BeginTransactionAsync();
            await ContinueDownloadsAsync(
                ingest,
                ingestTransaction,
                ids,
                now,
                CancellationToken.None);
        }
        finally
        {
            release.TrySetResult();
            await cleanupTask;
        }

        Assert.Equal(2, lockAttempts);
        Assert.Equal(1, updateAttempts);
        Assert.Equal(["lock", "select", "update", "lock", "select"], events);
        await AssertContinuationAsync(database, ids, now);
    }

    [Fact]
    public async Task PeriodicCleanupUsesTenRowBatchesWithoutTrackingSelectedDownloadsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        var eligibleIds = new List<long>();
        long unrelatedId;
        long inactiveId;
        long recentDefaultId;
        await using (var seed = database.Factory.CreateDbContext())
        {
            var eligibleRows = new List<Download>();
            for (var index = 0; index < 22; index++)
            {
                var row = NewCleanupDownload(
                    $"10.0.1.{index + 1}",
                    now.AddMinutes(-3).AddSeconds(index),
                    now.AddMinutes(-2).AddSeconds(index));
                row.CacheHitBytes = 100 + index;
                row.CacheMissBytes = 200 + index;
                row.LastUrl = $"/stale/{index}";
                seed.Downloads.Add(row);
                eligibleRows.Add(row);
            }

            var neverUpdated = NewCleanupDownload(
                "10.0.1.30",
                now.AddMinutes(-3),
                default);
            neverUpdated.LastUrl = "/never-updated";
            seed.Downloads.Add(neverUpdated);

            var unrelated = NewCleanupDownload(
                "10.0.1.31",
                now.AddSeconds(-10),
                now.AddSeconds(-10));
            unrelated.LastUrl = "/recent";
            seed.Downloads.Add(unrelated);

            var inactive = NewCleanupDownload(
                "10.0.1.32",
                now.AddMinutes(-3),
                now.AddMinutes(-2));
            inactive.IsActive = false;
            inactive.LastUrl = "/inactive";
            seed.Downloads.Add(inactive);

            var recentDefault = NewCleanupDownload(
                "10.0.1.33",
                now.AddSeconds(-30),
                default);
            recentDefault.LastUrl = "/recent-default";
            seed.Downloads.Add(recentDefault);

            await seed.SaveChangesAsync();
            eligibleIds.AddRange(eligibleRows.Select(row => row.Id));
            eligibleIds.Add(neverUpdated.Id);
            unrelatedId = unrelated.Id;
            inactiveId = inactive.Id;
            recentDefaultId = recentDefault.Id;
        }

        var batchSizes = new List<int>();
        var gate = new CleanupCommandGate
        {
            NonQueryStarting = (command, _) =>
            {
                if (IsCleanupUpdate(command))
                {
                    batchSizes.Add(CleanupIdCount(command));
                }

                return Task.CompletedTask;
            }
        };
        var options = RetryOptions(database, gate);
        await using var cleanupContext = new AppDbContext(options);
        var unrelatedTracked = await cleanupContext.Downloads.SingleAsync(row => row.Id == unrelatedId);
        unrelatedTracked.LastUrl = "/unsaved";

        await RunPeriodicAsync(cleanupContext, CancellationToken.None);

        Assert.Equal([10, 10, 3], batchSizes);
        var tracked = Assert.Single(cleanupContext.ChangeTracker.Entries<Download>());
        Assert.Equal(unrelatedId, tracked.Entity.Id);
        Assert.Equal(EntityState.Modified, tracked.State);

        await using var check = database.Factory.CreateDbContext();
        var eligible = await check.Downloads
            .Where(row => eligibleIds.Contains(row.Id))
            .OrderBy(row => row.Id)
            .ToListAsync();
        Assert.Equal(23, eligible.Count);
        Assert.All(eligible, row => Assert.False(row.IsActive));
        for (var index = 0; index < 22; index++)
        {
            var row = eligible.Single(item => item.Id == eligibleIds[index]);
            Assert.Equal(100L + index, row.CacheHitBytes);
            Assert.Equal(200L + index, row.CacheMissBytes);
            Assert.Equal($"/stale/{index}", row.LastUrl);
        }
        Assert.Equal("/never-updated", eligible.Single(row => row.Id == eligibleIds[^1]).LastUrl);
        Assert.Equal("/recent", (await check.Downloads.SingleAsync(row => row.Id == unrelatedId)).LastUrl);
        Assert.True((await check.Downloads.SingleAsync(row => row.Id == unrelatedId)).IsActive);
        Assert.False((await check.Downloads.SingleAsync(row => row.Id == inactiveId)).IsActive);
        Assert.True((await check.Downloads.SingleAsync(row => row.Id == recentDefaultId)).IsActive);
    }

    [Fact]
    public async Task CleanupBatchKeepsRowsAtBothCutoffBoundariesActiveAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var cutoff = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var staleCutoff = cutoff.AddSeconds(-45);
        long exactEndId;
        long exactStartId;
        long oldEndId;
        long oldStartId;
        await using (var seed = database.Factory.CreateDbContext())
        {
            var exactEnd = NewCleanupDownload(
                "10.0.2.1",
                cutoff.AddMinutes(-2),
                cutoff);
            var exactStart = NewCleanupDownload(
                "10.0.2.2",
                staleCutoff,
                default);
            var oldEnd = NewCleanupDownload(
                "10.0.2.3",
                cutoff.AddMinutes(-2),
                cutoff.AddTicks(-1));
            var oldStart = NewCleanupDownload(
                "10.0.2.4",
                staleCutoff.AddTicks(-1),
                default);
            seed.Downloads.AddRange(exactEnd, exactStart, oldEnd, oldStart);
            await seed.SaveChangesAsync();
            exactEndId = exactEnd.Id;
            exactStartId = exactStart.Id;
            oldEndId = oldEnd.Id;
            oldStartId = oldStart.Id;
        }

        await using (var run = new AppDbContext(RetryOptions(database)))
        {
            Assert.Equal(2, await DownloadCleanupService.CleanupBatchAsync(
                run,
                cutoff,
                staleCutoff,
                10,
                CancellationToken.None));
        }

        await using var check = database.Factory.CreateDbContext();
        Assert.True((await check.Downloads.SingleAsync(row => row.Id == exactEndId)).IsActive);
        Assert.True((await check.Downloads.SingleAsync(row => row.Id == exactStartId)).IsActive);
        Assert.False((await check.Downloads.SingleAsync(row => row.Id == oldEndId)).IsActive);
        Assert.False((await check.Downloads.SingleAsync(row => row.Id == oldStartId)).IsActive);
    }

    [Fact]
    public async Task CancellingWhileWaitingForTheTableLockReleasesTheTransactionAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Downloads.Add(NewCleanupDownload(
                "10.0.3.1",
                now.AddMinutes(-2),
                now.AddMinutes(-2)));
            await seed.SaveChangesAsync();
        }

        await using var blocker = new NpgsqlConnection(ConnectionString(database));
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await LockDownloadsAsync(blocker, blockerTransaction, CancellationToken.None);

        await using var cleanupContext = new AppDbContext(RetryOptions(database));
        await cleanupContext.Database.OpenConnectionAsync();
        var cleanupPid = ((NpgsqlConnection)cleanupContext.Database.GetDbConnection()).ProcessID;
        using var stopping = new CancellationTokenSource();
        var cleanupTask = DownloadCleanupService.CleanupBatchAsync(
            cleanupContext,
            now.AddSeconds(-15),
            now.AddSeconds(-60),
            10,
            stopping.Token);

        try
        {
            await WaitForDownloadLockAsync(ConnectionString(database), cleanupPid);
            stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanupTask);
        }
        finally
        {
            stopping.Cancel();
            await blockerTransaction.RollbackAsync();
            try
            {
                await cleanupTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await AssertDownloadLockAvailableAsync(ConnectionString(database));
    }

    [Fact]
    public async Task CancellingAfterSelectionKeepsCommittedBatchesAndReleasesTheTransactionAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Downloads.AddRange(Enumerable.Range(1, 11)
                .Select(index => NewCleanupDownload(
                    $"10.0.4.{index}",
                    now.AddMinutes(-2),
                    now.AddMinutes(-2))));
            await seed.SaveChangesAsync();
        }

        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selections = 0;
        var gate = new CleanupCommandGate
        {
            ReaderFinished = async (command, cancellationToken) =>
            {
                if (IsCleanupSelect(command) && Interlocked.Increment(ref selections) == 2)
                {
                    selected.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            }
        };
        var options = RetryOptions(database, gate);
        await using var cleanupContext = new AppDbContext(options);
        using var stopping = new CancellationTokenSource();
        var cleanupTask = RunPeriodicAsync(cleanupContext, stopping.Token);

        try
        {
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stopping.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanupTask);
        }
        finally
        {
            stopping.Cancel();
            release.TrySetResult();
            try
            {
                await cleanupTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await using (var check = database.Factory.CreateDbContext())
        {
            Assert.Equal(10, await check.Downloads.CountAsync(row => !row.IsActive));
            Assert.Equal(1, await check.Downloads.CountAsync(row => row.IsActive));
        }

        await AssertDownloadLockAvailableAsync(ConnectionString(database));
    }

    [Fact]
    public async Task StartupCleanupUsesBoundedBatchesWhileStartAndReadersStayAvailableAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        var eligibleIds = new List<long>();
        long recentId;
        long neverUpdatedId;
        long app0Id;
        await using (var seed = database.Factory.CreateDbContext())
        {
            var eligibleRows = new List<Download>();
            for (var index = 0; index < 21; index++)
            {
                var row = NewCleanupDownload(
                    $"10.0.5.{index + 1}",
                    now.AddMinutes(-2),
                    now.AddMinutes(-2));
                seed.Downloads.Add(row);
                eligibleRows.Add(row);
            }

            var recent = NewCleanupDownload(
                "10.0.5.30",
                now.AddSeconds(-10),
                now.AddSeconds(-10));
            var neverUpdated = NewCleanupDownload(
                "10.0.5.31",
                now.AddSeconds(-30),
                default);
            var app0 = NewCleanupDownload(
                "10.0.5.32",
                now.AddSeconds(-10),
                now.AddSeconds(-10));
            app0.GameAppId = 0;
            seed.Downloads.AddRange(recent, neverUpdated, app0);
            await seed.SaveChangesAsync();
            eligibleIds.AddRange(eligibleRows.Select(row => row.Id));
            recentId = recent.Id;
            neverUpdatedId = neverUpdated.Id;
            app0Id = app0.Id;
        }

        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var batchSizes = new List<int>();
        var gate = new CleanupCommandGate
        {
            NonQueryStarting = (command, _) =>
            {
                if (IsCleanupUpdate(command)
                    && command.Parameters.Cast<DbParameter>()
                        .Any(parameter => parameter.Value is IEnumerable<long>))
                {
                    batchSizes.Add(CleanupIdCount(command));
                }

                return Task.CompletedTask;
            },
            ReaderFinished = async (command, cancellationToken) =>
            {
                if (IsCleanupSelect(command) && Interlocked.Exchange(ref held, 1) == 0)
                {
                    selected.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            }
        };
        var options = RetryOptions(database, gate);
        var startup = CreateStartupService(options);
        await using var services = startup.Services;
        using var service = startup.Service;
        var started = false;
        try
        {
            var startTask = service.StartAsync(CancellationToken.None);
            await startTask;
            started = true;
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(startTask.IsCompletedSuccessfully);
            Assert.False(service.StartupCleanupFinished.IsCompleted);
            await using var reader = database.Factory.CreateDbContext();
            Assert.Equal(24, await reader.Downloads.CountAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            release.TrySetResult();
            await service.StartupCleanupFinished.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            if (started)
            {
                await service.StopAsync(CancellationToken.None);
            }
            Directory.Delete(startup.Root, recursive: true);
        }

        Assert.Equal([10, 10, 1], batchSizes);
        await using (var check = database.Factory.CreateDbContext())
        {
            Assert.Equal(21, await check.Downloads.CountAsync(
                row => eligibleIds.Contains(row.Id) && !row.IsActive));
            Assert.True((await check.Downloads.SingleAsync(row => row.Id == recentId)).IsActive);
            Assert.True((await check.Downloads.SingleAsync(row => row.Id == neverUpdatedId)).IsActive);
            Assert.False((await check.Downloads.SingleAsync(row => row.Id == app0Id)).IsActive);
        }

        var messages = startup.Logger.Entries.Select(entry => entry.Message).ToList();
        var checking = messages.IndexOf("Checking for stale active downloads...");
        var found = messages.IndexOf("Found 21 stale active downloads");
        var marked = messages.IndexOf("Marked 21 stale downloads as complete");
        var completed = messages.IndexOf("Initial database cleanup complete");
        Assert.True(checking >= 0);
        Assert.True(checking < found);
        Assert.True(found < marked);
        Assert.True(marked < completed);
    }

    [Fact]
    public async Task StartupFailureReleasesTheCompletionSignalAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var gate = new CleanupCommandGate
        {
            NonQueryStarting = (command, _) =>
            {
                if (IsDownloadsLock(command))
                {
                    throw new InvalidOperationException("Injected startup cleanup failure");
                }

                return Task.CompletedTask;
            }
        };
        var startup = CreateStartupService(RetryOptions(database, gate));
        await using var services = startup.Services;
        using var service = startup.Service;
        var started = false;
        try
        {
            await service.StartAsync(CancellationToken.None);
            started = true;
            await service.StartupCleanupFinished.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (started)
            {
                await service.StopAsync(CancellationToken.None);
            }
            Directory.Delete(startup.Root, recursive: true);
        }

        var failure = Assert.Single(startup.Logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Message == "Error during initial cleanup");
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    [Fact]
    public async Task StoppingStartupWhileItWaitsForTheTableLockReleasesTheCompletionSignalAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Downloads.Add(NewCleanupDownload(
                "10.0.6.1",
                now.AddMinutes(-2),
                now.AddMinutes(-2)));
            await seed.SaveChangesAsync();
        }

        await using var blocker = new NpgsqlConnection(ConnectionString(database));
        await blocker.OpenAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await LockDownloadsAsync(blocker, blockerTransaction, CancellationToken.None);

        var lockRequested = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new CleanupCommandGate
        {
            NonQueryStarting = (command, _) =>
            {
                if (IsDownloadsLock(command)
                    && command.Connection is NpgsqlConnection connection)
                {
                    lockRequested.TrySetResult(connection.ProcessID);
                }

                return Task.CompletedTask;
            }
        };
        var startup = CreateStartupService(RetryOptions(database, gate));
        await using var services = startup.Services;
        using var service = startup.Service;
        Task stopTask = Task.CompletedTask;
        var started = false;
        try
        {
            await service.StartAsync(CancellationToken.None);
            started = true;
            var cleanupPid = await lockRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForDownloadLockAsync(ConnectionString(database), cleanupPid);
            Assert.False(service.StartupCleanupFinished.IsCompleted);

            stopTask = service.StopAsync(CancellationToken.None);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            await service.StartupCleanupFinished;
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
            if (started)
            {
                await stopTask;
            }
            Directory.Delete(startup.Root, recursive: true);
        }

        Assert.True(service.StartupCleanupFinished.IsCompletedSuccessfully);
        await AssertDownloadLockAvailableAsync(ConnectionString(database));
    }

    // ---------------------------------------------------------------------------------------------
    // Integration - diagnostic orphan scans against PostgreSQL
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cleanup_XboxCacheSplit_NotDeleted_AndNoFkViolation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long xboxId;
        await using (var seed = new AppDbContext(options))
        {
            // 'steam' is a present service so the "all services orphaned" safety check does not trip.
            var steam = NewDownload("steam");
            var xbox = NewDownload("xbox");
            seed.Downloads.AddRange(steam, xbox);
            await seed.SaveChangesAsync();

            xboxId = xbox.Id;

            // Xbox cache LogEntry is recorded under 'wsus' and references the xbox Download by FK.
            seed.LogEntries.Add(NewLogEntry("wsus", xboxId));
            await seed.SaveChangesAsync();
        }

        // 'xbox' never appears in log-file service names; only steam + wsus do.
        var logServices = new HashSet<string> { "steam", "wsus" };

        await using (var run = new AppDbContext(options))
        {
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(0, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            // Data-loss guard: the Xbox download survives a cleanup where 'xbox' is absent from logs.
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "xbox"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));

            // The wsus LogEntry is untouched and still references the xbox Download.
            var wsusEntry = await assert.LogEntries.SingleAsync(le => le.Service == "wsus");
            Assert.Equal(xboxId, wsusEntry.DownloadId);
        }
    }

    [Fact]
    public async Task Cleanup_OrphanWithCrossServiceChild_NoViolation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long originId;
        await using (var seed = new AppDbContext(options))
        {
            var steam = NewDownload("steam");   // present -> safety check passes
            var origin = NewDownload("origin"); // genuinely orphaned (absent from logs, no alias)
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            originId = origin.Id;

            seed.LogEntries.Add(NewLogEntry("othercdn", originId));
            await seed.SaveChangesAsync();
        }

        var logServices = new HashSet<string> { "steam" };

        await using (var run = new AppDbContext(options))
        {
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));

            var child = await assert.LogEntries.SingleAsync(le => le.Service == "othercdn");
            Assert.Equal(originId, child.DownloadId);
        }
    }

    [Fact]
    public async Task Cleanup_OrphanWithSameServiceChild()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
            var steam = NewDownload("steam");
            var origin = NewDownload("origin");
            seed.Downloads.AddRange(steam, origin);
            await seed.SaveChangesAsync();

            seed.LogEntries.Add(NewLogEntry("origin", origin.Id));
            await seed.SaveChangesAsync();
        }

        var logServices = new HashSet<string> { "steam" };

        await using (var run = new AppDbContext(options))
        {
            var removed = await DownloadCleanupService.CleanupOrphanedServicesCoreAsync(
                run, logServices, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, removed);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "origin"));
            Assert.True(await assert.LogEntries.AnyAsync(le => le.Service == "origin"));
            Assert.True(await assert.Downloads.AnyAsync(d => d.Service == "steam"));
        }
    }

    [Theory]
    [InlineData(null, "Default")]
    [InlineData("", "Default")]
    [InlineData("primary", "Primary")]
    [InlineData("Retired", "Retired")]
    public void NormalizeDatasourceName_PreservesHistoryAndNormalizesOwnedValues(
        string? current,
        string expected)
    {
        var normalized = DownloadCleanupService.NormalizeDatasourceName(
            current,
            ["Default", "Primary"],
            "Default");

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public async Task NormalizeDatasourceMappings_Postgres_CanonicalizesKnownHistory()
    {
        var schema = Environment.GetEnvironmentVariable("DS_IMPL_CASE_CORRECTION_SCHEMA");
        if (string.IsNullOrWhiteSpace(schema))
        {
            await using var database = await TestDatabase.CreateAsync();
            await VerifyDatasourceNormalizationAsync(database.Options);
            return;
        }

        await VerifyDatasourceNormalizationAsync(CreatePostgresOptions(schema));
    }

    // ---------------------------------------------------------------------------------------------
    // Empty game identity repair - real ExecuteUpdate/ExecuteDelete against PostgreSQL
    //
    // The writers that stamped "" onto Downloads.EpicAppId, Downloads.GameName,
    // EpicCdnPatterns.AppId and the detection cache are guarded now (see
    // EpicEmptyAppIdIdentityTests and XboxEmptyTitleIdentityTests), so nothing new can be written.
    // These tests cover the rows already sitting in a user's database, which the guards do not
    // reach: NormalizeEmptyGameIdentitiesCoreAsync must repair every one of them and leave rows
    // holding real values exactly as they are.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NormalizeEmptyGameIdentities_ClearsEmptyDownloadEpicIdAndName_LeavesRealValues()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long emptyEpicId, emptyNameId, realEpicId, realNameId;
        await using (var seed = new AppDbContext(options))
        {
            var emptyEpic = NewDownload("epicgames");
            emptyEpic.EpicAppId = "";

            var emptyName = NewDownload("xbox");
            emptyName.GameName = "";

            var realEpic = NewDownload("epicgames");
            realEpic.EpicAppId = "Fortnite";

            var realName = NewDownload("xbox");
            realName.GameName = "Halo Infinite";

            seed.Downloads.AddRange(emptyEpic, emptyName, realEpic, realName);
            await seed.SaveChangesAsync();

            emptyEpicId = emptyEpic.Id;
            emptyNameId = emptyName.Id;
            realEpicId = realEpic.Id;
            realNameId = realName.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            // Both empty values become null, which is what every layer reads as "absent" - and for
            // the Xbox row it is what makes it a re-resolution candidate again.
            Assert.Null((await assert.Downloads.SingleAsync(d => d.Id == emptyEpicId)).EpicAppId);
            Assert.Null((await assert.Downloads.SingleAsync(d => d.Id == emptyNameId)).GameName);

            // Real values are untouched.
            Assert.Equal("Fortnite", (await assert.Downloads.SingleAsync(d => d.Id == realEpicId)).EpicAppId);
            Assert.Equal("Halo Infinite", (await assert.Downloads.SingleAsync(d => d.Id == realNameId)).GameName);
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
            var emptyName = NewDownload("xbox");
            emptyName.GameName = "";
            seed.Downloads.Add(emptyName);
            await seed.SaveChangesAsync();
        }

        await using (var first = new AppDbContext(options))
        {
            Assert.Equal(1, await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                first, NullLogger.Instance, CancellationToken.None));
        }

        await using (var second = new AppDbContext(options))
        {
            // The pass runs on every start, so a second run over an already-repaired database must
            // change nothing rather than churn rows.
            Assert.Equal(0, await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                second, NullLogger.Instance, CancellationToken.None));
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_ClearsDetectionEpicId_WithoutBreakingUniqueIndex()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long emptyEpicDetectionId, namedDetectionId, realEpicDetectionId;
        await using (var seed = new AppDbContext(options))
        {
            // The row being repaired lands on (0, null). A named row already sits on (0, null),
            // which is only legal because IX_CachedGameDetection_GameAppId_EpicAppId is
            // nulls-distinct - so this pair is the collision case if that assumption is wrong.
            var emptyEpic = NewDetection(0, "Some Epic Game", "epicgames");
            emptyEpic.EpicAppId = "";

            var named = NewDetection(0, "Overwatch", "blizzard");

            var realEpic = NewDetection(0, "Fortnite", "epicgames");
            realEpic.EpicAppId = "Fortnite";

            seed.CachedGameDetections.AddRange(emptyEpic, named, realEpic);
            await seed.SaveChangesAsync();

            emptyEpicDetectionId = emptyEpic.Id;
            namedDetectionId = named.Id;
            realEpicDetectionId = realEpic.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            // Repaired row keeps its name and now reads as a named game, matching how the download
            // side keys the same game once its empty Epic id is gone.
            var repairedRow = await assert.CachedGameDetections.SingleAsync(g => g.Id == emptyEpicDetectionId);
            Assert.Null(repairedRow.EpicAppId);
            Assert.Equal("Some Epic Game", repairedRow.GameName);

            // The pre-existing named row on the same (0, null) key survives alongside it.
            Assert.NotNull(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namedDetectionId));

            // A real Epic id is untouched.
            Assert.Equal("Fortnite", (await assert.CachedGameDetections.SingleAsync(g => g.Id == realEpicDetectionId)).EpicAppId);
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_RemovesNamelessDetection_KeepsIdentifiedOnes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        long namelessId, namelessEvictedId, namelessSteamId, namedId;
        await using (var seed = new AppDbContext(options))
        {
            // No name, no Steam app id, no Epic id: nothing can address this row, and its app id 0
            // un-evicts on any app 0 download.
            var nameless = NewDetection(0, "", "xbox");

            // The evicted variant is the one a full scan can never rebuild, so the repair has to
            // reach it here or it stays broken for good.
            var namelessEvicted = NewDetection(0, "", "wsus");
            namelessEvicted.IsEvicted = true;

            // Nameless but still addressable by its Steam app id - keys as steam:4000 either way,
            // and the scan refills the name, so deleting it would throw away eviction state.
            var namelessSteam = NewDetection(4000, "", "steam");

            var named = NewDetection(0, "Overwatch", "blizzard");

            seed.CachedGameDetections.AddRange(nameless, namelessEvicted, namelessSteam, named);
            await seed.SaveChangesAsync();

            namelessId = nameless.Id;
            namelessEvictedId = namelessEvicted.Id;
            namelessSteamId = namelessSteam.Id;
            namedId = named.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(2, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Null(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namelessId));
            Assert.Null(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namelessEvictedId));
            Assert.NotNull(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namelessSteamId));
            Assert.NotNull(await assert.CachedGameDetections.SingleOrDefaultAsync(g => g.Id == namedId));
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_RemovesEmptyCdnPattern_SoItsChunkUrlCanBeRecorded()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        const string blockedChunkUrl = "/Builds/Org/o-blocked/abc/default/";

        await using (var seed = new AppDbContext(options))
        {
            seed.EpicCdnPatterns.AddRange(
                NewCdnPattern("", "", blockedChunkUrl),
                NewCdnPattern("Fortnite", "Fortnite", "/Builds/Org/o-real/def/default/"));
            await seed.SaveChangesAsync();
        }

        await using (var run = new AppDbContext(options))
        {
            var repaired = await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(1, repaired);
        }

        await using (var assert = new AppDbContext(options))
        {
            // The pattern with a real app id is untouched.
            Assert.True(await assert.EpicCdnPatterns.AnyAsync(p => p.AppId == "Fortnite"));

            // The chunk URL the empty pattern held is free again. IX_EpicCdnPatterns_ChunkBaseUrl is
            // unique and the merge path only updates LastSeenAtUtc/Name on a URL it already has, so
            // while the empty row existed no real app id could ever be recorded for this URL.
            Assert.False(await assert.EpicCdnPatterns.AnyAsync(p => p.ChunkBaseUrl == blockedChunkUrl));

            assert.EpicCdnPatterns.Add(NewCdnPattern("RealApp", "Real Game", blockedChunkUrl));
            await assert.SaveChangesAsync();

            Assert.Equal("RealApp",
                (await assert.EpicCdnPatterns.SingleAsync(p => p.ChunkBaseUrl == blockedChunkUrl)).AppId);
        }
    }

    [Fact]
    public async Task NormalizeEmptyGameIdentities_CleanDatabase_ChangesNothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var options = database.Options;

        await using (var seed = new AppDbContext(options))
        {
            var steam = NewDownload("steam");
            steam.GameAppId = 730;
            steam.GameName = "Counter-Strike 2";

            var epic = NewDownload("epicgames");
            epic.EpicAppId = "Fortnite";

            seed.Downloads.AddRange(steam, epic);
            seed.CachedGameDetections.Add(NewDetection(730, "Counter-Strike 2", "steam"));
            seed.EpicCdnPatterns.Add(NewCdnPattern("Fortnite", "Fortnite", "/Builds/Org/o-real/def/default/"));
            await seed.SaveChangesAsync();
        }

        await using (var run = new AppDbContext(options))
        {
            Assert.Equal(0, await DownloadCleanupService.NormalizeEmptyGameIdentitiesCoreAsync(
                run, NullLogger.Instance, CancellationToken.None));
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Equal(2, await assert.Downloads.CountAsync());
            Assert.Equal(1, await assert.CachedGameDetections.CountAsync());
            Assert.Equal(1, await assert.EpicCdnPatterns.CountAsync());
            Assert.Equal("Counter-Strike 2",
                (await assert.Downloads.SingleAsync(d => d.GameAppId == 730)).GameName);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static DbContextOptions<AppDbContext> RetryOptions(
        TestDatabase database,
        params IInterceptor[] interceptors)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                ConnectionString(database),
                settings => settings.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(interceptors)
            .Options;
    }

    private static string ConnectionString(TestDatabase database)
    {
        using var context = database.Factory.CreateDbContext();
        return context.Database.GetConnectionString()!;
    }

    private static async Task RunPeriodicAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        using var service = new DownloadCleanupService(
            services,
            NullLogger<DownloadCleanupService>.Instance,
            new ConfigurationBuilder().Build(),
            cacheManagementService: null!,
            datasourceService: null!,
            NullProxy<IStateService>());
        var method = typeof(DownloadCleanupService).GetMethod(
            "CleanupStaleDownloadsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(service, [context, cancellationToken])!;
    }

    private static (
        ServiceProvider Services,
        DownloadCleanupService Service,
        CapturingLogger<DownloadCleanupService> Logger,
        string Root) CreateStartupService(DbContextOptions<AppDbContext> options)
    {
        var root = Path.Combine(Path.GetTempPath(), $"download-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "disabled",
                ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "logs"),
                ["LanCache:DataSources:0:Enabled"] = "false"
            })
            .Build();
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = root;
        var state = NullProxy<IStateService>();
        var datasources = new DatasourceService(
            configuration,
            paths,
            NullLogger<DatasourceService>.Instance);
        var services = new ServiceCollection()
            .AddScoped(_ => new AppDbContext(options))
            .BuildServiceProvider();
        var cache = new CacheManagementService(
            configuration,
            NullLogger<CacheManagementService>.Instance,
            paths,
            rustProcessHelper: null!,
            nginxLogRotationService: null!,
            datasources,
            state,
            new TestDbContextFactory(options),
            gameCacheDetectionService: null!,
            NullProxy<IUnifiedOperationTracker>(),
            NullProxy<ISignalRNotificationService>(),
            NullProxy<ILancacheEnvFileReader>(),
            NullProxy<IOperationConflictChecker>(),
            new DatasourceCapabilityService(datasources),
            CacheScanGateHarness.Idle(),
            (OperationStateService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(OperationStateService)));
        var logger = new CapturingLogger<DownloadCleanupService>();
        var service = new DownloadCleanupService(
            services,
            logger,
            configuration,
            cache,
            datasources,
            state);
        return (services, service, logger, root);
    }

    private static T NullProxy<T>() where T : class =>
        DispatchProxy.Create<T, NullReturningProxy>();

    private static Download NewCleanupDownload(
        string clientIp,
        DateTime start,
        DateTime end) => new()
        {
            Service = "steam",
            ClientIp = clientIp,
            StartTimeUtc = start,
            EndTimeUtc = end,
            CacheHitBytes = 10,
            CacheMissBytes = 20,
            IsActive = true,
            IsEvicted = false,
            GameAppId = 1,
            Datasource = "default",
            LastUrl = "/stale"
        };

    private static async Task LockDownloadsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "LOCK TABLE \"Downloads\" IN ROW EXCLUSIVE MODE",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ContinueDownloadsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<long> ids,
        DateTime endTime,
        CancellationToken cancellationToken)
    {
        await LockDownloadsAsync(connection, transaction, cancellationToken);
        await WriteContinuationAsync(
            connection,
            transaction,
            ids,
            endTime,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task WriteContinuationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<long> ids,
        DateTime endTime,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            var url = $"/continued/{ids[index]}";
            await using (var update = new NpgsqlCommand("""
                UPDATE "Downloads"
                SET "EndTimeUtc" = @endTime,
                    "CacheHitBytes" = @cacheHitBytes,
                    "CacheMissBytes" = @cacheMissBytes,
                    "LastUrl" = @url,
                    "IsActive" = TRUE
                WHERE "Id" = @id
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("endTime", endTime);
                update.Parameters.AddWithValue("cacheHitBytes", 110L + index);
                update.Parameters.AddWithValue("cacheMissBytes", 220L + index);
                update.Parameters.AddWithValue("url", url);
                update.Parameters.AddWithValue("id", ids[index]);
                Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
            }

            await using var insert = new NpgsqlCommand("""
                INSERT INTO "LogEntries"
                    ("Timestamp", "ClientIp", "Service", "Method", "Url", "StatusCode",
                     "BytesServed", "CacheStatus", "Datasource", "DownloadId", "CreatedAt")
                SELECT @endTime, d."ClientIp", d."Service", 'GET', @url, 200,
                       @bytesServed, 'HIT', d."Datasource", d."Id", @endTime
                FROM "Downloads" d
                WHERE d."Id" = @id
                """, connection, transaction);
            insert.Parameters.AddWithValue("endTime", endTime);
            insert.Parameters.AddWithValue("url", url);
            insert.Parameters.AddWithValue("bytesServed", 330L + (index * 2));
            insert.Parameters.AddWithValue("id", ids[index]);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(cancellationToken));
        }
    }

    private static async Task AssertContinuationAsync(
        TestDatabase database,
        List<long> ids,
        DateTime endTime)
    {
        await using var context = database.Factory.CreateDbContext();
        for (var index = 0; index < ids.Count; index++)
        {
            var row = await context.Downloads.SingleAsync(download => download.Id == ids[index]);
            Assert.True(row.IsActive);
            Assert.Equal(endTime, row.EndTimeUtc);
            Assert.Equal(110L + index, row.CacheHitBytes);
            Assert.Equal(220L + index, row.CacheMissBytes);
            Assert.Equal($"/continued/{ids[index]}", row.LastUrl);
        }

        var logs = await context.LogEntries.OrderBy(entry => entry.DownloadId).ToListAsync();
        Assert.Equal(ids.Count, logs.Count);
        Assert.All(logs, entry =>
        {
            Assert.NotNull(entry.DownloadId);
            Assert.Contains(entry.DownloadId.Value, ids);
        });
    }

    private static async Task WaitForDownloadLockAsync(
        string connectionString,
        int processId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks l
                    JOIN pg_class c ON c.oid = l.relation
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = current_schema()
                      AND c.relname = 'Downloads'
                      AND l.pid = @processId
                      AND NOT l.granted)
                """, connection);
            command.Parameters.AddWithValue("processId", processId);
            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("The Downloads table lock did not enter PostgreSQL wait state");
    }

    private static async Task AssertDownloadLockAvailableAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await LockDownloadsAsync(connection, transaction, watchdog.Token);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    private static bool IsDownloadsLock(DbCommand command) =>
        command.CommandText.StartsWith(
            "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
            StringComparison.Ordinal);

    private static bool IsCleanupSelect(DbCommand command) =>
        IsCleanupSelect(command.CommandText);

    private static bool IsCleanupSelect(string command) =>
        command.StartsWith("SELECT d.\"Id\"", StringComparison.Ordinal)
        && command.Contains("d.\"IsActive\"", StringComparison.Ordinal)
        && command.Contains("LIMIT", StringComparison.Ordinal);

    private static bool IsCleanupUpdate(DbCommand command) =>
        command.CommandText.StartsWith("UPDATE \"Downloads\"", StringComparison.Ordinal)
        && command.CommandText.Contains("\"IsActive\"", StringComparison.Ordinal);

    private static int CleanupIdCount(DbCommand command)
    {
        var idSets = command.Parameters
            .Cast<DbParameter>()
            .Select(parameter => parameter.Value)
            .OfType<IEnumerable<long>>()
            .ToList();
        return Assert.Single(idSets).Count();
    }

    private static async Task VerifyDatasourceNormalizationAsync(DbContextOptions<AppDbContext> options)
    {
        const string alphaClient = "10.0.0.11";
        var eventTime = new DateTime(2026, 9, 26, 18, 0, 0, DateTimeKind.Utc);
        long alphaDownloadId;
        long betaDownloadId;
        long retiredDownloadId;
        long emptyDownloadId;
        long alphaEntryId;
        long alphaHistoryId;
        long betaEntryId;
        long retiredEntryId;

        await using (var seed = new AppDbContext(options))
        {
            var alpha = NewDownload("steam");
            alpha.ClientIp = alphaClient;
            alpha.DepotId = 42;
            alpha.IsActive = true;
            alpha.Datasource = "alpha";

            var beta = NewDownload("steam");
            beta.ClientIp = alphaClient;
            beta.DepotId = 42;
            beta.IsActive = true;
            beta.Datasource = "beta";

            var retired = NewDownload("steam");
            retired.ClientIp = "10.0.0.13";
            retired.DepotId = 42;
            retired.Datasource = "retired";

            var empty = NewDownload("steam");
            empty.ClientIp = "10.0.0.14";
            empty.Datasource = "";

            seed.Downloads.AddRange(alpha, beta, retired, empty);
            await seed.SaveChangesAsync();

            alphaDownloadId = alpha.Id;
            betaDownloadId = beta.Id;
            retiredDownloadId = retired.Id;
            emptyDownloadId = empty.Id;

            var alphaEntry = NewLogEntry("steam", alpha.Id);
            alphaEntry.ClientIp = alphaClient;
            alphaEntry.Timestamp = eventTime;
            alphaEntry.Url = "/depot/42/chunk";
            alphaEntry.BytesServed = 4096;
            alphaEntry.Datasource = "alpha";

            var alphaHistory = NewLogEntry("steam", alpha.Id);
            alphaHistory.DownloadId = null;
            alphaHistory.ClientIp = alphaClient;
            alphaHistory.Timestamp = eventTime.AddSeconds(-1);
            alphaHistory.Url = "/depot/42/history";
            alphaHistory.BytesServed = 2048;
            alphaHistory.Datasource = "alpha";

            var betaEntry = NewLogEntry("steam", beta.Id);
            betaEntry.ClientIp = alphaClient;
            betaEntry.Timestamp = eventTime;
            betaEntry.Url = "/depot/42/chunk";
            betaEntry.BytesServed = 4096;
            betaEntry.Datasource = "beta";

            var retiredEntry = NewLogEntry("steam", retired.Id);
            retiredEntry.ClientIp = retired.ClientIp;
            retiredEntry.Timestamp = eventTime;
            retiredEntry.Url = "/depot/42/retired";
            retiredEntry.BytesServed = 1024;
            retiredEntry.Datasource = "retired";

            seed.LogEntries.AddRange(alphaEntry, alphaHistory, betaEntry, retiredEntry);
            await seed.SaveChangesAsync();

            alphaEntryId = alphaEntry.Id;
            alphaHistoryId = alphaHistory.Id;
            betaEntryId = betaEntry.Id;
            retiredEntryId = retiredEntry.Id;
        }

        await using (var run = new AppDbContext(options))
        {
            var updated = await DownloadCleanupService.NormalizeDatasourceMappingsCoreAsync(
                run,
                ["ALPHA", "beta"],
                "ALPHA",
                NullLogger.Instance,
                CancellationToken.None);

            Assert.Equal(4, updated);
        }

        await using (var assert = new AppDbContext(options))
        {
            Assert.Equal("ALPHA", (await assert.Downloads.SingleAsync(row => row.Id == alphaDownloadId)).Datasource);
            Assert.Equal("beta", (await assert.Downloads.SingleAsync(row => row.Id == betaDownloadId)).Datasource);
            Assert.Equal("retired", (await assert.Downloads.SingleAsync(row => row.Id == retiredDownloadId)).Datasource);
            Assert.Equal("ALPHA", (await assert.Downloads.SingleAsync(row => row.Id == emptyDownloadId)).Datasource);

            var alphaEntry = await assert.LogEntries.SingleAsync(row => row.Id == alphaEntryId);
            var alphaHistory = await assert.LogEntries.SingleAsync(row => row.Id == alphaHistoryId);
            var betaEntry = await assert.LogEntries.SingleAsync(row => row.Id == betaEntryId);
            var retiredEntry = await assert.LogEntries.SingleAsync(row => row.Id == retiredEntryId);

            Assert.Equal("ALPHA", alphaEntry.Datasource);
            Assert.Equal("ALPHA", alphaHistory.Datasource);
            Assert.Equal("beta", betaEntry.Datasource);
            Assert.Equal("retired", retiredEntry.Datasource);
            Assert.Equal(alphaDownloadId, alphaEntry.DownloadId);
            Assert.Null(alphaHistory.DownloadId);
            Assert.Equal(betaDownloadId, betaEntry.DownloadId);
            Assert.Equal(retiredDownloadId, retiredEntry.DownloadId);

            Assert.Empty(await (
                from entry in assert.LogEntries
                join download in assert.Downloads on entry.DownloadId equals download.Id
                where entry.Datasource == "ALPHA" && entry.Datasource != download.Datasource
                select entry.Id).ToListAsync());

            Assert.Equal(1, await assert.LogEntries.CountAsync(row =>
                row.Datasource == "ALPHA"
                && row.ClientIp == alphaEntry.ClientIp
                && row.Service == alphaEntry.Service
                && row.Timestamp == alphaEntry.Timestamp
                && row.Url == alphaEntry.Url
                && row.BytesServed == alphaEntry.BytesServed));

            Assert.Equal(alphaDownloadId, await assert.Downloads
                .Where(row => row.ClientIp == alphaClient
                    && row.Service == "steam"
                    && row.DepotId == 42
                    && row.IsActive
                    && row.Datasource == "ALPHA")
                .Select(row => row.Id)
                .SingleAsync());
        }
    }

    private static DbContextOptions<AppDbContext> CreatePostgresOptions(string schema)
    {
        var connection = Environment.GetEnvironmentVariable("DS_IMPL_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException("The PostgreSQL test connection is missing");
        }

        var settings = new NpgsqlConnectionStringBuilder(connection)
        {
            SearchPath = schema + ",public",
            ApplicationName = "ds-impl-b-case-correction"
        };

        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(settings.ConnectionString)
            .Options;
    }

    private static Download NewDownload(string service) => new Download
    {
        Service = service,
        ClientIp = "10.0.0.1",
        StartTimeUtc = DateTime.UtcNow,
        EndTimeUtc = DateTime.UtcNow,
        CacheHitBytes = 1,
        CacheMissBytes = 1,
        IsActive = false,
        Datasource = "default"
    };

    private static LogEntryRecord NewLogEntry(string service, long downloadId) => new LogEntryRecord
    {
        Service = service,
        ClientIp = "10.0.0.1",
        Url = "/cache/object",
        Timestamp = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        DownloadId = downloadId
    };

    private static CachedGameDetection NewDetection(long gameAppId, string gameName, string service) => new CachedGameDetection
    {
        GameAppId = gameAppId,
        GameName = gameName,
        Service = service,
        CacheFilesFound = 1,
        TotalSizeBytes = 1024,
        LastDetectedUtc = DateTime.UtcNow,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static EpicCdnPattern NewCdnPattern(string appId, string name, string chunkBaseUrl) => new EpicCdnPattern
    {
        AppId = appId,
        Name = name,
        CdnHost = "epicgames-download1.akamaized.net",
        ChunkBaseUrl = chunkBaseUrl,
        DiscoveredAtUtc = DateTime.UtcNow,
        LastSeenAtUtc = DateTime.UtcNow
    };

    private sealed class CleanupCommandGate : DbCommandInterceptor
    {
        public Func<DbCommand, CancellationToken, Task>? NonQueryStarting { get; init; }

        public Func<DbCommand, CancellationToken, Task>? ReaderFinished { get; init; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData evt,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (NonQueryStarting is not null)
            {
                await NonQueryStarting(command, cancellationToken);
            }

            return await base.NonQueryExecutingAsync(
                command,
                evt,
                result,
                cancellationToken);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData evt,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (ReaderFinished is not null)
            {
                await ReaderFinished(command, cancellationToken);
            }

            return await base.ReaderExecutedAsync(
                command,
                evt,
                result,
                cancellationToken);
        }
    }
}
