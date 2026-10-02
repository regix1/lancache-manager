using System.Data.Common;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LancacheManager.Tests;

public sealed class RemovalCleanupRetryTests
{
    [Fact]
    public async Task GameAndServiceCleanupRetriesWholeTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seed = database.Factory.CreateDbContext())
        {
            await SeedHistoryAsync(seed, mismatch: false);
        }

        var recorder = new RecordingCommandInterceptor();
        var options = RetryOptions(database, recorder);
        await using (var control = new AppDbContext(options))
        {
            await using var transaction = await control.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => control.Downloads.AnyAsync());
        }

        var lockAttempts = 0;
        var observedTimeout = 0;
        recorder.OnExecuting = command =>
        {
            if (!IsDownloadsLock(command))
            {
                return;
            }

            observedTimeout = command.CommandTimeout;
            if (Interlocked.Increment(ref lockAttempts) == 1)
            {
                throw new PostgresException(
                    "Injected transient cleanup failure",
                    "ERROR",
                    "ERROR",
                    PostgresErrorCodes.SerializationFailure);
            }
        };

        await using (var retryContext = new AppDbContext(options))
        {
            var selection = new RemovalSelection(
                ["alpha"],
                RemovalKind.Service,
                Service: "steam");

            var result = await CacheManagementService.CleanupRemovalAsync(
                retryContext,
                selection,
                CancellationToken.None);

            Assert.Equal(1, result.DownloadsDeleted);
            Assert.Equal(1, result.LogEntriesDeleted);
            Assert.Equal(1800, retryContext.Database.GetCommandTimeout());
        }

        Assert.Equal(2, lockAttempts);
        Assert.Equal(1800, observedTimeout);
        AssertDownloadsLockComesFirst(recorder.Commands);
        await using (var verify = database.Factory.CreateDbContext())
        {
            Assert.False(await verify.Downloads.AnyAsync(row => row.Datasource.ToLower() == "alpha"));
            Assert.False(await verify.LogEntries.AnyAsync(row => row.Datasource.ToLower() == "alpha"));
            Assert.Equal(1, await verify.Downloads.CountAsync(row => row.Datasource.ToLower() == "beta"));
            Assert.Equal(1, await verify.LogEntries.CountAsync(row => row.Datasource.ToLower() == "beta"));
        }

        await using var mismatchDatabase = await TestDatabase.CreateAsync();
        await using (var seed = mismatchDatabase.Factory.CreateDbContext())
        {
            await SeedHistoryAsync(seed, mismatch: true);
        }

        await using (var mismatchContext = new AppDbContext(RetryOptions(
            mismatchDatabase,
            new RecordingCommandInterceptor())))
        {
            var selection = new RemovalSelection(
                ["alpha"],
                RemovalKind.Service,
                Service: "steam");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                CacheManagementService.CleanupRemovalAsync(
                    mismatchContext,
                    selection,
                    CancellationToken.None));
        }

        await AssertHistoryUnchangedAsync(mismatchDatabase);
    }

    private static DbContextOptions<AppDbContext> RetryOptions(
        TestDatabase database,
        RecordingCommandInterceptor recorder)
    {
        using var context = database.Factory.CreateDbContext();
        var connectionString = context.Database.GetConnectionString()!;
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, options =>
                options.EnableRetryOnFailure(3, TimeSpan.Zero, null))
            .AddInterceptors(recorder)
            .Options;
    }

    private static async Task SeedHistoryAsync(AppDbContext context, bool mismatch)
    {
        var alpha = DownloadRow("Alpha");
        var beta = DownloadRow("Beta");
        context.Downloads.AddRange(alpha, beta);
        await context.SaveChangesAsync();
        context.LogEntries.AddRange(
            LogRow(alpha.Id, mismatch ? "Beta" : "Alpha"),
            LogRow(beta.Id, "Beta"));
        await context.SaveChangesAsync();
    }

    private static Download DownloadRow(string datasource) => new()
    {
        Service = "steam",
        GameName = "Retry cleanup",
        Datasource = datasource,
        ClientIp = "10.0.0.5",
        StartTimeUtc = DateTime.UtcNow.AddMinutes(-1),
        EndTimeUtc = DateTime.UtcNow
    };

    private static LogEntryRecord LogRow(long downloadId, string datasource) => new()
    {
        DownloadId = downloadId,
        Service = "steam",
        Datasource = datasource,
        ClientIp = "10.0.0.5",
        Timestamp = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    private static bool IsDownloadsLock(DbCommand command) =>
        command.CommandText.Contains(
            "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
            StringComparison.Ordinal);

    private static void AssertDownloadsLockComesFirst(IReadOnlyList<string> commands)
    {
        var downloads = commands.ToList().FindIndex(
            command => command.Contains("LOCK TABLE \"Downloads\"", StringComparison.Ordinal));
        var logEntries = commands.ToList().FindIndex(
            command => command.Contains("LOCK TABLE \"LogEntries\"", StringComparison.Ordinal));
        Assert.True(downloads >= 0, "The cleanup did not lock Downloads");
        Assert.True(logEntries > downloads, "The cleanup did not lock Downloads before LogEntries");
    }

    private static async Task AssertHistoryUnchangedAsync(TestDatabase database)
    {
        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(2, await context.Downloads.CountAsync());
        Assert.Equal(2, await context.LogEntries.CountAsync());
        Assert.Equal(2, await context.LogEntries.CountAsync(row => row.DownloadId != null));
    }
}
