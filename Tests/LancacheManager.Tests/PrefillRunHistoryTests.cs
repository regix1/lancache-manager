using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Data.Migrations;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LancacheManager.Tests;

public sealed class PrefillRunHistoryTests
{
    [Fact]
    public async Task IndependentHistoryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.History.StartEntryAsync(fixture.Session.Id, "10", "Old game");
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("10");
        fixture.Client.Set(first, "completed", 100, "success");
        fixture.Client.Set(second, "cancelled", 12, "cancelled");
        await fixture.RefreshAsync();
        await using var context = new AppDbContext(fixture.Options);
        var history = await context.PrefillHistoryEntries.ToListAsync();
        Assert.Equal(3, history.Count);
        Assert.Equal(PrefillHistoryEntryStatus.InProgress, history.Single(entry => entry.RunId is null).Status);
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
        await fixture.History.StartEntryAsync(fixture.Session.Id, "10", "Legacy game");
        await using (var context = new AppDbContext(fixture.Options))
        {
            var migration = new AddPrefillRuns();
            var sql = context.GetService<IMigrationsSqlGenerator>();
            foreach (var command in sql.Generate(migration.DownOperations))
                await context.Database.ExecuteSqlRawAsync(command.CommandText);
            foreach (var command in sql.Generate(migration.UpOperations))
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
        Assert.Single(await context.PrefillCachedApps.ToListAsync());
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
        var connection = new NpgsqlConnectionStringBuilder(existing.Database.GetConnectionString())
        {
            Database = $"lancache_migration_tests_{Guid.NewGuid():N}",
            SearchPath = "public",
            Pooling = false
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
        Assert.Equal(!terminal, duplicate.Applied);
        Assert.Empty(duplicate.Items);
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
                { Sequence = snapshot.Sequence + 1, State = "cancelled" }, [], true, cancellationToken);
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
        Assert.Equal("cancelled", row.State);
        Assert.Equal(snapshot.Sequence + 1, row.Sequence);
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
