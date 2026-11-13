using System.Data.Common;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A user stops a full depot crawl while its import restores the depots prefill cached, the last step of the import.
/// The crawl must end there as a stop: it must not log the stop as a failed restore and go on to apply the mappings
/// to the downloads.
/// </summary>
public sealed class StoppedCrawlPrefillRestoreTests
{
    private const string Cancel = "cancel";
    private const string ForceStop = "force stop";

    [Theory]
    [InlineData(Cancel)]
    [InlineData(ForceStop)]
    public async Task ACrawlStoppedDuringThePrefillRestore_EndsThereAsAStop(string stop)
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var database = (TestDatabase)CacheScanGateHarness.GetField(host, "_database")!;
        var tracker = (UnifiedOperationTracker)CacheScanGateHarness.GetField(host.Service, "_operationTracker")!;
        var logger = new CapturingLogger<SteamKit2Service>();
        CacheScanGateHarness.SetField(host.Service, "_logger", logger);
        await using (var db = database.Factory.CreateDbContext())
        {
            db.PrefillCachedDepots.Add(new PrefillCachedDepot
            {
                AppId = 77, DepotId = 770, ManifestId = 1, AppName = "Prefill only game", CachedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var restoreRead = new HeldPrefillRead();
        CacheScanGateHarness.SetField(host.Service, "_prefillCache", new PrefillCacheService(
            new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(restoreRead).Options),
            NullLogger<PrefillCacheService>.Instance));
        CacheScanGateHarness.SetField(host.Service, "_lastChangeNumberSeen", 200u);

        // The crawl's last stage, as the crawl reaches it once it has listed every app.
        using var run = new CancellationTokenSource();
        var reporter = (MappingOperationReporter)typeof(SteamKit2Service)
            .GetMethod("CreateTrackedRebuildReporter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(host.Service, [run, new RunNotice(NotificationMode.All, RunTrigger.Manual)])!;
        await using (reporter)
        {
            await reporter.StartAsync();
            var finishing = (Task)typeof(SteamKit2Service)
                .GetMethod("FinalizeAndNotifyAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(host.Service, [false, reporter.Token])!;
            await Task.WhenAny(restoreRead.Entered.Task, finishing).WaitAsync(ForceStoppedCrawlHost.Timeout);
            Assert.True(restoreRead.Entered.Task.IsCompleted, "The crawl's import never reached the prefill restore");

            if (stop == ForceStop)
            {
                await host.ForceStopAsync(reporter.OperationId);
            }
            else
            {
                tracker.CancelOperation(reporter.OperationId);
            }
            restoreRead.Release();

            var ended = await Record.ExceptionAsync(() => finishing.WaitAsync(ForceStoppedCrawlHost.Timeout));
            var phase = host.Service.GetProgress().Status;
            var restoreFailure = logger.Entries.FirstOrDefault(entry =>
                entry.Level == LogLevel.Warning && entry.Exception is OperationCanceledException);

            Assert.True(ended is OperationCanceledException && phase != DepotScanPhase.ApplyingMappings && restoreFailure is null,
                $"A crawl stopped ({stop}) during its prefill restore ended with " +
                (ended is null ? "no stop" : $"{ended.GetType().Name}") +
                $", its stage moved on to {phase}, and the log says \"{restoreFailure?.Message}\"; it must end in the import " +
                "as a stop, with no failed restore logged");
        }
    }

    /// <summary>Holds the restore's read of what prefill cached until <see cref="Release"/>.</summary>
    private sealed class HeldPrefillRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData commandEvent, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("PrefillCachedDepots", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await _release.Task;
            }

            return result;
        }
    }
}
