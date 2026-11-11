using System.Data.Common;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Data.Migrations;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

public sealed class PrefillRunHistoryTests
{
    [Fact]
    public async Task IndependentHistoryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("10");
        fixture.Client.Set(first, "completed", 100, "success");
        fixture.Client.Set(second, "canceled", 12, "canceled");
        await fixture.RefreshAsync();
        await using var context = new AppDbContext(fixture.Options);
        var history = await context.PrefillHistoryEntries.ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(PrefillHistoryEntryStatus.Completed, history.Single(entry => entry.RunId == first.PrefillRunId).Status);
        Assert.Equal(12, history.Single(entry => entry.RunId == second.PrefillRunId).BytesDownloaded);
    }

    [Fact]
    public void RunItemUniqueIndexExcludesLegacyRows()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=lancache").Options);
        var entry = context.Model.FindEntityType(typeof(PrefillHistoryEntry))!;
        var index = Assert.Single(entry.GetIndexes(), index => index.IsUnique);
        Assert.Equal([nameof(PrefillHistoryEntry.RunId), nameof(PrefillHistoryEntry.AppId)],
            index.Properties.Select(property => property.Name));
        Assert.Equal("\"RunId\" IS NOT NULL", index.GetFilter());
        Assert.True(entry.FindProperty(nameof(PrefillHistoryEntry.RunId))!.IsNullable);
    }

    [Fact]
    public async Task MigrationHistoryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true);
        await using (var context = new AppDbContext(fixture.Options))
        {
            context.PrefillHistoryEntries.Add(new PrefillHistoryEntry
            {
                SessionId = fixture.Session.Id, AppId = "10", AppName = "Legacy game",
                StartedAtUtc = DateTime.UtcNow, Status = PrefillHistoryEntryStatus.InProgress
            });
            await context.SaveChangesAsync();
            var migration = new AddPrefillRuns();
            var sql = context.GetService<IMigrationsSqlGenerator>();
            foreach (var command in sql.Generate(migration.DownOperations))
                await context.Database.ExecuteSqlRawAsync(command.CommandText);
            foreach (var command in sql.Generate(migration.UpOperations))
                await context.Database.ExecuteSqlRawAsync(command.CommandText);
            // A later migration adds a PrefillRuns column; the recreated table needs it before a run is saved.
            var laterColumns = new AddPrefillTrafficAndSliceSizes().UpOperations
                .OfType<AddColumnOperation>()
                .Where(operation => operation.Table == "PrefillRuns")
                .ToList();
            foreach (var command in sql.Generate(laterColumns))
                await context.Database.ExecuteSqlRawAsync(command.CommandText);
            var legacy = await context.PrefillHistoryEntries.SingleAsync();
            Assert.Null(legacy.RunId);
            Assert.Equal("Legacy game", legacy.AppName);
        }
        var run = await fixture.StartAsync("10");
        await using (var context = new AppDbContext(fixture.Options))
        {
            context.PrefillHistoryEntries.Add(new PrefillHistoryEntry
            { SessionId = fixture.Session.Id, RunId = run.PrefillRunId, AppId = "10", StartedAtUtc = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        await using (var context = new AppDbContext(fixture.Options))
        {
            context.PrefillHistoryEntries.Add(new PrefillHistoryEntry
            { SessionId = fixture.Session.Id, AppId = "10", StartedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
            Assert.Equal(2, await context.PrefillHistoryEntries.CountAsync(entry => entry.RunId == null));
        }
    }

    [Fact]
    public async Task FinalItemBytesAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.Set(run, "downloading", 50, "success");
        await fixture.RefreshAsync();
        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();
        await using var context = new AppDbContext(fixture.Options);
        var row = await context.PrefillHistoryEntries.SingleAsync(entry => entry.RunId == run.PrefillRunId);
        Assert.Equal(100, row.BytesDownloaded);
        Assert.Equal(PrefillHistoryEntryStatus.Completed, row.Status);
        Assert.Empty(await context.PrefillCachedApps.ToListAsync());
    }

    [Fact]
    public async Task PersistedTerminalAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();
        var result = await fixture.History.SaveRunAsync(run, run.Snapshot with
        {
            State = "failed",
            Sequence = run.Snapshot.Sequence + 10,
            BytesTransferred = 1000
        }, [], true, CancellationToken.None);
        Assert.False(result.Applied);
        await using var context = new AppDbContext(fixture.Options);
        var saved = await context.PrefillRuns.SingleAsync(row => row.Id == run.PrefillRunId);
        Assert.Equal("completed", saved.State);
        Assert.Equal(run.Snapshot.Sequence, saved.Sequence);
    }

    [Fact]
    public async Task FreshMigrationAsync()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using var existing = new AppDbContext(fixture.Options);
        // MigrateAsync creates this database, and CREATE DATABASE waits for a checkpoint. In the full suite
        // that wait was measured at 28.4 s against Npgsql's 30 s default, so commands get 120 s.
        var connection = new NpgsqlConnectionStringBuilder(existing.Database.GetConnectionString())
        {
            Database = $"lancache_migration_tests_{Guid.NewGuid():N}",
            SearchPath = "public",
            Pooling = false,
            CommandTimeout = 120
        };
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection.ConnectionString, options =>
                options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)).Options);
        Assert.False(context.Database.HasPendingModelChanges());
        try
        {
            await context.Database.MigrateAsync();
            Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await context.PrefillRuns.ToListAsync());
            Assert.Empty(await context.PrefillHistoryEntries.ToListAsync());
            await context.Database.MigrateAsync();
            Assert.False(context.Database.HasPendingModelChanges());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task CanceledSpellingMigrationAsync()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using var existing = new AppDbContext(fixture.Options);
        // Same scratch database and command timeout as FreshMigrationAsync: CREATE DATABASE waits for a checkpoint.
        var connection = new NpgsqlConnectionStringBuilder(existing.Database.GetConnectionString())
        {
            Database = $"lancache_migration_tests_{Guid.NewGuid():N}",
            SearchPath = "public",
            Pooling = false,
            CommandTimeout = 120
        };
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection.ConnectionString, options =>
                options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)).Options);
        var finishedRun = Guid.NewGuid();
        var runningRun = Guid.NewGuid();
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261110120000_AddCachedGameDetectionUnresolvedDepot");
            // 'Halted' and 'halted' stand for a stored name that is not one of today's names.
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"PrefillSessions\" (\"SessionId\", \"CreatedBySessionId\", \"Status\", \"Platform\", \"CreatedAtUtc\", " +
                "\"ExpiresAtUtc\", \"LastLoginAtUtc\", \"IsAuthenticated\", \"IsPersistent\", \"IsPrefilling\", " +
                "\"LoggedInWhilePersonCanceled\", \"LoginCanceledByPerson\", \"SignedOut\") " +
                "VALUES ('legacy', 'creator', 'Halted', 'Steam', now(), now(), now(), false, false, false, false, false, false)");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"PrefillRuns\" (\"Id\", \"SessionId\", \"DaemonInstanceId\", \"OptionsJson\", \"SnapshotJson\", \"Source\", " +
                "\"State\", \"StartedAtUtc\", \"CompletedAtUtc\", \"Revision\", \"Sequence\", \"CancelRequested\", \"HistoryIncomplete\") " +
                "VALUES ({0}, 'legacy', 'daemon', '{{}}', {1}, 'manual', 'halted', now(), now(), 1, 1, false, false), " +
                "({2}, 'legacy', 'daemon', '{{}}', {1}, 'manual', 'halted', now(), NULL, 1, 1, false, false)",
                finishedRun,
                """{"OperationId":"op","DaemonInstanceId":"daemon","State":"halted","CurrentItem":{"AppId":"10","Name":"Legacy game"}}""",
                runningRun);
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"PrefillHistoryEntries\" (\"SessionId\", \"RunId\", \"AppId\", \"AppName\", \"Status\", \"StartedAtUtc\", " +
                "\"BytesDownloaded\", \"TotalBytes\", \"Sequence\") VALUES ('legacy', {0}, '10', 'Legacy game', 'Halted', now(), 0, 0, 1)",
                finishedRun);
            // 'Operation halted' stands for the sentence the old version stored on a canceled run.
            var haltedExecution = Guid.NewGuid();
            var failedExecution = Guid.NewGuid();
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"ScheduleExecutions\" (\"ServiceKey\", \"OperationId\", \"Status\", \"ActorKind\", \"StartedAt\", \"CompletedAt\", " +
                "\"WorkerStarted\", \"Detail\") VALUES ('logProcessing', {0}, 'Halted', 'Server', now(), now(), true, 'Operation halted'), " +
                "('logProcessing', {1}, 'Failed', 'Server', now(), now(), true, 'Disk full')",
                haltedExecution,
                failedExecution);

            await context.Database.MigrateAsync();

            var session = await context.PrefillSessions.AsNoTracking().SingleAsync();
            Assert.Equal(PrefillSessionStatus.Canceled, session.Status);
            var entry = await context.PrefillHistoryEntries.AsNoTracking().SingleAsync();
            Assert.Equal(PrefillHistoryEntryStatus.Canceled, entry.Status);
            Assert.Equal("Legacy game", entry.AppName);
            var finished = await context.PrefillRuns.AsNoTracking().SingleAsync(run => run.Id == finishedRun);
            Assert.Equal("canceled", finished.State);
            var snapshot = JsonSerializer.Deserialize<DaemonRunSnapshot>(finished.SnapshotJson)!;
            Assert.Equal("canceled", snapshot.State);
            Assert.Equal("Legacy game", snapshot.CurrentItem!.Name);
            var running = await context.PrefillRuns.AsNoTracking().SingleAsync(run => run.Id == runningRun);
            Assert.Equal("halted", running.State);
            var execution = await context.ScheduleExecutions.AsNoTracking()
                .SingleAsync(row => row.OperationId == haltedExecution);
            Assert.Equal(OperationStatus.Canceled, execution.Status);
            Assert.Equal("signalr.generic.canceled", execution.Detail);
            var failed = await context.ScheduleExecutions.AsNoTracking()
                .SingleAsync(row => row.OperationId == failedExecution);
            Assert.Equal(OperationStatus.Failed, failed.Status);
            Assert.Equal("Disk full", failed.Detail);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetryPersistenceAsync(bool terminal, bool retry)
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true);
        var run = await fixture.StartAsync("10");
        await using var verification = new AppDbContext(fixture.Options);
        var revision = await verification.PrefillRuns.Where(row => row.Id == run.PrefillRunId)
            .Select(row => row.Revision).SingleAsync();
        var writes = new RunSave(retry);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(verification.Database.GetConnectionString(), connection =>
                connection.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(writes).Options;
        var history = new PrefillSessionService(new TestDbContextFactory(options),
            NullLogger<PrefillSessionService>.Instance);
        var snapshot = run.Snapshot with
        {
            Sequence = run.Snapshot.Sequence + 10,
            BytesTransferred = 123,
            State = terminal ? "completed" : "downloading",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var item = new DaemonRunItem
        {
            AppId = "10",
            Sequence = snapshot.Sequence,
            BytesTransferred = 123,
            State = terminal ? "completed" : "downloading",
            Result = terminal ? "success" : null
        };

        var saved = await history.SaveRunAsync(run, snapshot, [item], terminal, CancellationToken.None);

        Assert.True(saved.Applied);
        Assert.Single(saved.Items);
        Assert.Equal(retry ? 2 : 1, writes.Contexts.Count);
        Assert.Equal(writes.Contexts.Count, writes.Contexts.Distinct().Count());
        var row = await verification.PrefillRuns.SingleAsync(row => row.Id == run.PrefillRunId);
        Assert.Equal(revision + 1, row.Revision);
        Assert.Equal(snapshot.Sequence, row.Sequence);
        Assert.Equal(terminal, row.CompletedAtUtc.HasValue);
        var entry = await verification.PrefillHistoryEntries.SingleAsync(entry => entry.RunId == run.PrefillRunId);
        Assert.Equal(123, entry.BytesDownloaded);
        Assert.Equal(terminal ? PrefillHistoryEntryStatus.Completed : PrefillHistoryEntryStatus.InProgress, entry.Status);

        var duplicate = await history.SaveRunAsync(run, snapshot, [item], terminal, CancellationToken.None);
        // A terminal save that finds its own committed row answers the call's items, the case of a retry whose
        // first commit landed but whose answer was lost.
        Assert.True(duplicate.Applied);
        Assert.Equal(terminal ? 1 : 0, duplicate.Items.Count);
        if (terminal)
        {
            var late = await history.SaveRunAsync(run, snapshot with
            { Sequence = snapshot.Sequence + 1, State = "failed", BytesTransferred = 999 }, [], true, CancellationToken.None);
            Assert.False(late.Applied);
            await verification.Entry(row).ReloadAsync();
            Assert.Equal(revision + 1, row.Revision);
            Assert.Equal("completed", row.State);
        }
    }

    [Fact]
    public async Task LostTerminalCommitAnswerAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true);
        var run = await fixture.StartAsync("10");
        var commits = new LostCommitAnswer();
        await using var verification = new AppDbContext(fixture.Options);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(verification.Database.GetConnectionString(), connection =>
                connection.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(commits).Options;
        PrefillRunTests.SetField(fixture.Daemon, "_sessionService",
            new PrefillSessionService(new TestDbContextFactory(options), NullLogger<PrefillSessionService>.Instance));
        fixture.Client.Set(run, "completed", 100, "success");
        var page = fixture.Client.Pages[run.PrefillRunId];
        fixture.Client.Pages[run.PrefillRunId] = page with
        {
            Items = [page.Items[0] with
            {
                Depots = [new DepotManifestProgressInfo { DepotId = 11, ManifestId = 12, TotalBytes = 100 }]
            }]
        };

        await fixture.RefreshAsync();

        Assert.True(commits.Lost);
        Assert.Equal("completed", (await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Snapshot.State);
        Assert.Equal("10", (await verification.PrefillCachedApps.SingleAsync()).AppId);
    }

    [Fact]
    public async Task RevisionContentionAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true);
        var run = await fixture.StartAsync("10");
        await using var verification = new AppDbContext(fixture.Options);
        var snapshot = run.Snapshot with
        { Sequence = run.Snapshot.Sequence + 10, State = "completed", UpdatedAt = DateTimeOffset.UtcNow };
        var writes = new RunSave(false)
        {
            BeforeSave = async cancellationToken =>
            {
                var winner = await fixture.History.SaveRunAsync(run, snapshot with
                { Sequence = snapshot.Sequence + 1, State = "canceled" }, [], true, cancellationToken);
                Assert.True(winner.Applied);
            }
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(verification.Database.GetConnectionString(), connection =>
                connection.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(writes).Options;
        var history = new PrefillSessionService(new TestDbContextFactory(options),
            NullLogger<PrefillSessionService>.Instance);

        var loser = await history.SaveRunAsync(run, snapshot,
            [new DaemonRunItem { AppId = "20", Sequence = snapshot.Sequence, Result = "success" }],
            true, CancellationToken.None);

        Assert.False(loser.Applied);
        Assert.Empty(loser.Items);
        Assert.Single(writes.Contexts);
        Assert.False(await verification.PrefillHistoryEntries.AnyAsync(entry => entry.RunId == run.PrefillRunId && entry.AppId == "20"));
        var row = await verification.PrefillRuns.SingleAsync(row => row.Id == run.PrefillRunId);
        Assert.Equal("canceled", row.State);
        Assert.Equal(snapshot.Sequence + 1, row.Sequence);
    }

    [Fact]
    public async Task FailedGamesReturnsOnlyFailedRowsInSequence()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await MarkPersistentAsync(fixture);
        var run = await fixture.StartAsync("10");
        var other = await fixture.StartAsync("90");
        var sequence = run.Snapshot.Sequence + 10;
        await fixture.History.SaveRunAsync(run, run.Snapshot with
        { Sequence = sequence + 10, State = "completed", UpdatedAt = DateTimeOffset.UtcNow },
        [
            new DaemonRunItem { AppId = "40", Name = "Delta", Sequence = sequence + 4, Result = "failed", Reason = "runtime-exceeded" },
            new DaemonRunItem { AppId = "30", Name = "Charlie", Sequence = sequence + 3, Result = "failed" },
            new DaemonRunItem { AppId = "20", Name = "Bravo", Sequence = sequence + 2, Result = "success" },
            new DaemonRunItem { AppId = "10", Name = "Alpha", Sequence = sequence + 1, Result = "failed", Reason = "auth-lost" }
        ], true, CancellationToken.None);
        await fixture.History.SaveRunAsync(other, other.Snapshot with
        { Sequence = other.Snapshot.Sequence + 10, State = "completed", UpdatedAt = DateTimeOffset.UtcNow },
        [new DaemonRunItem { AppId = "90", Sequence = other.Snapshot.Sequence + 10, Result = "failed", Reason = "stalled" }],
        true, CancellationToken.None);

        var games = await fixture.History.GetFailedGamesAsync(run.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);

        Assert.NotNull(games);
        Assert.Equal(["10", "30", "40"], games.Select(game => game.AppId));
        Assert.Equal(["Alpha", "Charlie", "Delta"], games.Select(game => game.Name));
        Assert.Equal(["errors.prefill.signInLost", "errors.prefill.gameDownloadFailed", "signalr.scheduledPrefill.failedMaxRuntime"],
            games.Select(game => game.ReasonKey));
    }

    [Fact]
    public async Task FailedGamesTellsAnEpicAccountToLogInAgainWhenSignedOut()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await using (var context = new AppDbContext(fixture.Options))
        {
            var session = await context.PrefillSessions.SingleAsync(row => row.SessionId == fixture.Session.Id);
            session.IsPersistent = true;
            session.Platform = PrefillPlatform.Epic;
            await context.SaveChangesAsync();
        }
        var run = await fixture.StartAsync("10");
        await fixture.History.SaveRunAsync(run, run.Snapshot with
        { Sequence = run.Snapshot.Sequence + 10, State = "completed", UpdatedAt = DateTimeOffset.UtcNow },
        [new DaemonRunItem { AppId = "10", Sequence = run.Snapshot.Sequence + 1, Result = "failed", Reason = "auth-lost" }],
        true, CancellationToken.None);

        var games = await fixture.History.GetFailedGamesAsync(run.PrefillRunId, PrefillPlatform.Epic, CancellationToken.None);

        Assert.Equal("errors.prefill.loginRequired", Assert.Single(games!).ReasonKey);
    }

    [Fact]
    public async Task FailedGamesTellsTheUserToEnableTheCacheDomainsWhenTheCacheReturnedNothing()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await using (var context = new AppDbContext(fixture.Options))
        {
            var session = await context.PrefillSessions.SingleAsync(row => row.SessionId == fixture.Session.Id);
            session.IsPersistent = true;
            session.Platform = PrefillPlatform.Xbox;
            await context.SaveChangesAsync();
        }
        var run = await fixture.StartAsync("10");
        var sequence = run.Snapshot.Sequence + 10;
        await fixture.History.SaveRunAsync(run, run.Snapshot with
        { Sequence = sequence + 10, State = "failed", Reason = "download-failed", UpdatedAt = DateTimeOffset.UtcNow },
        [
            new DaemonRunItem { AppId = "10", Sequence = sequence + 1, Result = "failed", Reason = "cache-domain-missing" },
            new DaemonRunItem { AppId = "20", Sequence = sequence + 2, Result = "failed", Reason = "reason-this-version-does-not-know" }
        ], true, CancellationToken.None);

        var games = await fixture.History.GetFailedGamesAsync(run.PrefillRunId, PrefillPlatform.Xbox, CancellationToken.None);

        Assert.Equal(["errors.prefill.cacheDomainMissing", "errors.prefill.gameDownloadFailed"],
            games!.Select(game => game.ReasonKey));
    }

    [Fact]
    public async Task FailedGamesIncludesGamesLeftUnfinishedByStalledRun()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await MarkPersistentAsync(fixture);
        var run = await fixture.StartAsync("10");
        var sequence = run.Snapshot.Sequence + 10;
        await fixture.History.SaveRunAsync(run, run.Snapshot with
        { Sequence = sequence + 10, State = "failed", Reason = "stalled", UpdatedAt = DateTimeOffset.UtcNow },
        [
            new DaemonRunItem { AppId = "10", Sequence = sequence + 1, Result = "success" },
            new DaemonRunItem { AppId = "20", Name = "Pending game", Sequence = sequence + 2 }
        ], true, CancellationToken.None);

        var games = await fixture.History.GetFailedGamesAsync(run.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);

        var game = Assert.Single(games!);
        Assert.Equal("20", game.AppId);
        Assert.Equal("signalr.scheduledPrefill.failedStalled", game.ReasonKey);
    }

    [Fact]
    public async Task FailedGamesIncludesGamesTheDaemonMarkedCanceledOrSkippedWhenTheRunFailed()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await MarkPersistentAsync(fixture);
        var stalled = await fixture.StartAsync("10");
        var sequence = stalled.Snapshot.Sequence + 10;
        // The daemon's terminal page after a server cancel: every game without a result is canceled.
        await fixture.History.SaveRunAsync(stalled, stalled.Snapshot with
        { Sequence = sequence + 10, State = "failed", Reason = "stalled", UpdatedAt = DateTimeOffset.UtcNow },
        [
            new DaemonRunItem { AppId = "10", Sequence = sequence + 1, Result = "success" },
            new DaemonRunItem { AppId = "20", Name = "In flight", Sequence = sequence + 2, State = "canceled", Result = "canceled", Reason = "notAttempted", BytesTransferred = 5 },
            new DaemonRunItem { AppId = "30", Name = "Not started", Sequence = sequence + 3, State = "canceled", Result = "canceled", Reason = "notAttempted" }
        ], true, CancellationToken.None);
        var signedOut = await fixture.StartAsync("40");
        sequence = signedOut.Snapshot.Sequence + 10;
        // The daemon failed the run itself: unfinished games are skipped with the run's reason.
        var unfinishedItem = new DaemonRunItem { AppId = "40", Name = "Unfinished", Sequence = sequence + 1, State = "skipped", Result = "skipped", Reason = "auth-lost" };
        await fixture.History.SaveRunAsync(signedOut, signedOut.Snapshot with
        { Sequence = sequence + 10, State = "failed", Reason = "auth-lost", CurrentItem = unfinishedItem, UpdatedAt = DateTimeOffset.UtcNow },
        [
            unfinishedItem,
            new DaemonRunItem { AppId = "45", Sequence = sequence + 2, State = "skipped", Result = "skipped", Reason = "auth-lost" },
            new DaemonRunItem { AppId = "50", Sequence = sequence + 3, State = "skipped", Result = "skipped", Reason = "skippedOverlap" }
        ], true, CancellationToken.None);
        var canceled = await fixture.StartAsync("60");
        sequence = canceled.Snapshot.Sequence + 10;
        await fixture.History.SaveRunAsync(canceled, canceled.Snapshot with
        { Sequence = sequence + 10, State = "canceled", UpdatedAt = DateTimeOffset.UtcNow },
        [new DaemonRunItem { AppId = "60", Sequence = sequence + 1, State = "canceled", Result = "canceled", Reason = "notAttempted" }],
        true, CancellationToken.None);

        var stalledGames = await fixture.History.GetFailedGamesAsync(stalled.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);
        var signedOutGames = await fixture.History.GetFailedGamesAsync(signedOut.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);
        var canceledGames = await fixture.History.GetFailedGamesAsync(canceled.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);

        var inFlight = Assert.Single(stalledGames!);
        Assert.Equal("20", inFlight.AppId);
        Assert.Equal("signalr.scheduledPrefill.failedStalled", inFlight.ReasonKey);
        var unfinished = Assert.Single(signedOutGames!);
        Assert.Equal("40", unfinished.AppId);
        Assert.Equal("errors.prefill.signInLost", unfinished.ReasonKey);
        Assert.Empty(canceledGames!);
    }

    [Fact]
    public async Task FailedGamesListsGameInFlightWhenRunFailedWithoutOrWithUnnamedReason()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await MarkPersistentAsync(fixture);
        var noCode = await fixture.StartAsync("10");
        var sequence = noCode.Snapshot.Sequence + 10;
        // The daemon failed the run on an exception that carries no error code.
        var downloading = new DaemonRunItem { AppId = "10", Name = "Downloading", Sequence = sequence + 1, State = "skipped", Result = "skipped", Reason = "notAttempted" };
        await fixture.History.SaveRunAsync(noCode, noCode.Snapshot with
        { Sequence = sequence + 10, State = "failed", CurrentItem = downloading, UpdatedAt = DateTimeOffset.UtcNow },
        [
            downloading,
            new DaemonRunItem { AppId = "20", Sequence = sequence + 2, State = "skipped", Result = "skipped", Reason = "notAttempted" }
        ], true, CancellationToken.None);
        var changed = await fixture.StartAsync("30");
        sequence = changed.Snapshot.Sequence + 10;
        await fixture.History.SaveRunAsync(changed, changed.Snapshot with
        { Sequence = sequence + 10, State = "failed", Reason = "instance-changed", UpdatedAt = DateTimeOffset.UtcNow },
        [new DaemonRunItem { AppId = "30", Sequence = sequence + 1, State = "canceled", Result = "canceled", Reason = "notAttempted", BytesTransferred = 7 }],
        true, CancellationToken.None);

        var noCodeGames = await fixture.History.GetFailedGamesAsync(noCode.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);
        var changedGames = await fixture.History.GetFailedGamesAsync(changed.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);

        var game = Assert.Single(noCodeGames!);
        Assert.Equal("10", game.AppId);
        Assert.Equal("errors.prefill.gameDownloadFailed", game.ReasonKey);
        Assert.Equal("errors.prefill.instanceChanged", Assert.Single(changedGames!).ReasonKey);
    }

    [Fact]
    public async Task FailedGamesIsEmptyForRunWithoutFailures()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await MarkPersistentAsync(fixture);
        var run = await fixture.StartAsync("10");
        await fixture.History.SaveRunAsync(run, run.Snapshot with
        { Sequence = run.Snapshot.Sequence + 10, State = "completed", UpdatedAt = DateTimeOffset.UtcNow },
        [new DaemonRunItem { AppId = "10", Sequence = run.Snapshot.Sequence + 10, Result = "success" }],
        true, CancellationToken.None);

        var games = await fixture.History.GetFailedGamesAsync(run.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);

        Assert.NotNull(games);
        Assert.Empty(games);
    }

    [Fact]
    public async Task FailedGamesIsNullForAnotherServiceGuestSessionOrUnknownRun()
    {
        await using var persistent = await RunFixture.CreateAsync();
        await MarkPersistentAsync(persistent);
        var run = await persistent.StartAsync("10");
        await using var guest = await RunFixture.CreateAsync();
        var guestRun = await guest.StartAsync("10");

        Assert.Null(await persistent.History.GetFailedGamesAsync(run.PrefillRunId, PrefillPlatform.Epic, CancellationToken.None));
        Assert.Null(await guest.History.GetFailedGamesAsync(guestRun.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None));
        Assert.Null(await persistent.History.GetFailedGamesAsync(Guid.NewGuid(), PrefillPlatform.Steam, CancellationToken.None));
    }

    [Fact]
    public async Task FailedGamesReadsRunOfEndedSession()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await MarkPersistentAsync(fixture);
        var run = await fixture.StartAsync("10");
        await fixture.History.SaveRunAsync(run, run.Snapshot with
        { Sequence = run.Snapshot.Sequence + 10, State = "completed", UpdatedAt = DateTimeOffset.UtcNow },
        [new DaemonRunItem { AppId = "10", Sequence = run.Snapshot.Sequence + 10, Result = "failed", Reason = "game-details-unavailable" }],
        true, CancellationToken.None);
        await using (var context = new AppDbContext(fixture.Options))
        {
            var session = await context.PrefillSessions.SingleAsync(row => row.SessionId == fixture.Session.Id);
            session.Status = PrefillSessionStatus.Terminated;
            session.EndedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        var games = await fixture.History.GetFailedGamesAsync(run.PrefillRunId, PrefillPlatform.Steam, CancellationToken.None);

        Assert.Equal("errors.prefill.gameDetailsUnavailable", Assert.Single(games!).ReasonKey);
    }

    [Fact]
    public void DaemonCommandStageKeysKeepTheirValues()
    {
        Assert.Equal("errors.prefill.runLimit", new DaemonCommandException("run-limit").StageKey);
        Assert.Equal("errors.steam.signInLost", new DaemonCommandException("auth-lost").StageKey);
        Assert.Equal("errors.prefill.requestFailed", new DaemonCommandException("unknown-code").StageKey);
        var outdated = new DaemonCommandException("daemon-outdated");
        Assert.Equal("daemon-outdated", outdated.ErrorCode);
        Assert.Equal("errors.prefill.daemonOutdated", outdated.StageKey);
        Assert.False(outdated.RequiresLogin);
    }

    private static async Task MarkPersistentAsync(RunFixture fixture)
    {
        await using var context = new AppDbContext(fixture.Options);
        var session = await context.PrefillSessions.SingleAsync(row => row.SessionId == fixture.Session.Id);
        session.IsPersistent = true;
        session.Platform = PrefillPlatform.Steam;
        await context.SaveChangesAsync();
    }

    /// <summary>Lets the first commit land, then reports a transient failure as if its answer was lost.</summary>
    private sealed class LostCommitAnswer : DbTransactionInterceptor
    {
        public bool Lost { get; private set; }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData commitEvent, CancellationToken cancellationToken = default)
        {
            if (Lost) return Task.CompletedTask;
            Lost = true;
            throw new PostgresException("Connection lost", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
        }
    }

    private sealed class RunSave(bool retry) : SaveChangesInterceptor
    {
        private int _saved;
        public List<Guid> Contexts { get; } = [];
        public Func<CancellationToken, Task>? BeforeSave { get; init; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(eventData.Context!.ContextId.InstanceId);
            if (BeforeSave is not null) await BeforeSave(cancellationToken);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (retry && ++_saved == 1)
                throw new PostgresException("Transaction interrupted", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
            return ValueTask.FromResult(result);
        }
    }
}
