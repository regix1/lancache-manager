using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the ownership rule that keeps log processing from wedging "busy" forever. The service is a
/// singleton and the interactive path clears IsProcessing before it completes its operation, so a
/// second run can register its own operation while the first is still finishing. A run must therefore complete
/// the operation it registered rather than whatever the field currently holds, and must tear down
/// only the state it installed - while a run that set the busy flag and installed nothing still has
/// to clear that flag on the way out.
/// </summary>
public sealed class LogProcessingOperationOwnershipTests
{
    [Fact]
    public void OwnsOperationState_TrueWhenTheFieldStillHoldsThisRunsId()
    {
        var operationId = Guid.NewGuid();

        Assert.True(RustLogProcessorService.OwnsOperationState(operationId, operationId));
    }

    [Fact]
    public void OwnsOperationState_FalseWhenALaterRunInstalledItsOwnId()
    {
        // The interleaving this exists for: interactive run A has cleared IsProcessing but not yet
        // completed when live tick B registers. A must leave B's id, cancellation source and busy flag alone.
        var interactiveId = Guid.NewGuid();
        var liveTickId = Guid.NewGuid();

        Assert.False(RustLogProcessorService.OwnsOperationState(liveTickId, interactiveId));
    }

    [Fact]
    public void OwnsOperationState_FalseWhenATerminalCleanupAlreadyClearedTheField()
    {
        // A force-kill ran the terminal cleanup, which already reset the busy flags and disposed the
        // cancellation source. Repeating that teardown would clear whatever came after it.
        Assert.False(RustLogProcessorService.OwnsOperationState(null, Guid.NewGuid()));
    }

    [Fact]
    public void OwnsOperationState_TrueWhenTheRunNeverRegisteredAnything()
    {
        // Both absent is the run that set IsProcessing and failed before registering. It owns the
        // flag it set, so it must still be allowed to clear it.
        Assert.True(RustLogProcessorService.OwnsOperationState(null, null));
    }

    [Fact]
    public async Task FailedRun_ClearsTheBusyFlagAndTheOperationIdAsync()
    {
        using var fixture = new ProcessorFixture();

        // The resolved rust executable does not exist, so the run fails after registering. Whichever
        // path it leaves by, it must not strand the busy flag: the conflict checker reads it, and a
        // stuck one blocks every other heavy operation until the process restarts.
        var started = await fixture.Processor.StartProcessingAsync(
            fixture.LogFilePath,
            liveIngest: true);

        Assert.False(started);
        Assert.False(fixture.Processor.IsProcessing);
        Assert.Null(fixture.Processor.CurrentOperationId);
    }

    [Fact]
    public async Task FailedRun_LeavesNoOperationRunningAsync()
    {
        using var fixture = new ProcessorFixture();

        await fixture.Processor.StartProcessingAsync(fixture.LogFilePath, liveIngest: true);

        var stillRunning = fixture.Tracker.GetActiveOperations()
            .Where(o => o.Type == OperationType.LogProcessing)
            .ToList();

        Assert.Empty(stillRunning);
    }

    [Fact]
    public async Task ALivePass_RegistersAHiddenScheduledNoticeAndTheLiveIngestFlagAsync()
    {
        using var fixture = new ProcessorFixture();

        await fixture.Processor.StartProcessingAsync(fixture.LogFilePath, liveIngest: true);

        var run = Assert.Single(fixture.Tracker.GetRuns().Runs);
        Assert.True(run.LiveIngest);
        var operation = fixture.Tracker.GetOperation(run.OperationId)!;
        Assert.True(operation.LiveIngest);
        Assert.Equal(NotificationMode.Hidden, operation.Notice!.Mode);
        Assert.Equal(RunTrigger.Scheduled, operation.Notice.Trigger);
    }

    [Fact]
    public async Task AnInteractivePass_RegistersNoNoticeAndIsNotLiveIngestAsync()
    {
        using var fixture = new ProcessorFixture();

        await fixture.Processor.StartProcessingAsync(fixture.LogFilePath);

        var run = Assert.Single(fixture.Tracker.GetRuns().Runs);
        Assert.False(run.LiveIngest);
        Assert.Equal(RunVisibility.Card, run.Visibility);
        Assert.Null(fixture.Tracker.GetOperation(run.OperationId)!.Notice);
    }

    [Fact]
    public async Task RestoredLivePassHasNoCardAsync()
    {
        using var fixture = new ProcessorFixture();
        var repair = RestoredRepair(
            new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled));

        await fixture.Processor.RestoreRepairAsync(repair, CancellationToken.None);

        Assert.Null(fixture.Tracker.GetOperation(repair.Id));
        Assert.Empty(fixture.Tracker.GetRuns().Runs);
        Assert.Empty(fixture.Messages.Completions);
    }

    [Fact]
    public async Task RestoredInteractivePassKeepsItsCompletionEmitterAsync()
    {
        using var fixture = new ProcessorFixture();
        var repair = RestoredRepair(notice: null);

        await fixture.Processor.RestoreRepairAsync(repair, CancellationToken.None);

        var operation = Assert.IsType<OperationInfo>(fixture.Tracker.GetOperation(repair.Id));
        Assert.False(operation.LiveIngest);
        fixture.Tracker.CompleteOperation(repair.Id, true);
        Assert.Single(fixture.Messages.Completions);
    }

    [Fact]
    public async Task ALivePass_SendsNoLogProcessingEventAsync()
    {
        using var fixture = new ProcessorFixture();

        await fixture.Processor.StartProcessingAsync(fixture.LogFilePath, liveIngest: true);

        Assert.DoesNotContain(fixture.Messages.EventNames, name => name.StartsWith("LogProcessing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInteractivePass_SendsItsStartedAndCompleteEventsAsync()
    {
        using var fixture = new ProcessorFixture();

        await fixture.Processor.StartProcessingAsync(fixture.LogFilePath);

        Assert.Contains("LogProcessingStarted", fixture.Messages.EventNames);
        Assert.Contains("DownloadsRefresh", fixture.Messages.EventNames);
        Assert.Contains("LogProcessingComplete", fixture.Messages.EventNames);
        Assert.True(
            fixture.Messages.EventNames.IndexOf("DownloadsRefresh")
            < fixture.Messages.EventNames.IndexOf("LogProcessingComplete"));
    }

    [Fact]
    public async Task FailedCheckpointWithExitOneKeepsCountsAndAllowsALaterLivePassAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        Assert.Equal(operationId, Assert.Single(fixture.RepairOwner.GetPendingRepairs()).Id);

        await fixture.Pipe.SendAsync(
            TerminalProgress(7, 11, "failed", sourcePosition: 25),
            exitCode: 1);

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(
            Assert.Single(fixture.Messages.Completions));
        Assert.Equal(operationId, complete.OperationId);
        Assert.False(complete.Success);
        Assert.False(complete.Cancelled);
        Assert.Equal(OperationStatus.Failed, complete.Status);
        Assert.Equal(7, complete.EntriesProcessed);
        Assert.Equal(11, complete.LinesProcessed);
        Assert.Equal(
            OperationStatus.Failed,
            Assert.IsType<OperationInfo>(fixture.Tracker.GetOperation(operationId)).Status);
        Assert.Equal(
            5,
            fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
        Assert.Empty(fixture.RepairOwner.GetPendingRepairs());
        Assert.False(fixture.RepairOwner.OwnsRepair(operationId));
        Assert.Equal(
            1,
            fixture.Messages.EventNames.Count(name => name == SignalREvents.DownloadsRefresh));

        var liveRun = fixture.Track(fixture.Processor.StartProcessingAsync(
            fixture.LogFilePath,
            liveIngest: true));
        connection = await fixture.Pipe.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        Assert.NotEqual(operationId, fixture.Processor.CurrentOperationId);
        await fixture.Pipe.SendAsync(
            TerminalProgress(0, 0, "completed", sourcePosition: 5),
            exitCode: 0);

        Assert.True(await liveRun.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();
        Assert.False(fixture.Processor.IsProcessing);
        Assert.Null(fixture.Processor.CurrentOperationId);
        Assert.Empty(fixture.Tracker.GetActiveOperations(OperationType.LogProcessing));
        Assert.Single(fixture.Messages.Completions);
    }

    [Fact]
    public async Task FailedCheckpointWithExitZeroStillKeepsItsConfirmedCountsAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        await fixture.Pipe.SendAsync(
            TerminalProgress(7, 11, "failed", sourcePosition: 25),
            exitCode: 0);

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(
            Assert.Single(fixture.Messages.Completions));
        Assert.Equal(operationId, complete.OperationId);
        Assert.Equal(OperationStatus.Failed, complete.Status);
        Assert.Equal(7, complete.EntriesProcessed);
        Assert.Equal(11, complete.LinesProcessed);
        Assert.Equal(
            5,
            fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
        Assert.Empty(fixture.RepairOwner.GetPendingRepairs());
        Assert.False(fixture.RepairOwner.OwnsRepair(operationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APartialPassCompletesWithAWarningNamingTheSkippedFilesAsync(bool batch)
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(batch
            ? fixture.Processor.StartProcessingAsync()
            : fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        // log_processor after one rotated member failed: terminal "partial", exit 0, the file named
        // "path: reason".
        var partial = TerminalProgress(7, 11, "partial", sourcePosition: 25);
        partial.FilesWithErrors =
        [
            Path.Combine(Path.GetDirectoryName(fixture.LogFilePath)!, "access.log.2.gz") + ": unexpected end of file"
        ];
        await fixture.Pipe.SendAsync(partial, exitCode: 0);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var row = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationId == operationId);
        Assert.Equal("completed", row.Status);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.logFilesSkipped", warning.StageKey);
        Assert.Equal("access.log.2.gz", warning.Context["fileNames"]);
        Assert.Equal(7L, warning.Context["entriesSaved"]);
        Assert.True(row.Retained);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADatabaseErrorAfterSomeEntriesWereSavedEndsAmberNamingTheErrorAsync(bool batch)
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(batch
            ? fixture.Processor.StartProcessingAsync()
            : fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        // log_processor when the database stops answering after some entries were saved: it names the file
        // it was reading as "path: error", records the error as database_error, stops every source and ends
        // "partial" with exit 0.
        const string databaseError = "pool timed out while waiting for an open connection";
        var partial = TerminalProgress(7, 11, "partial", sourcePosition: 25);
        partial.FilesWithErrors = [fixture.LogFilePath + ": " + databaseError];
        partial.DatabaseError = databaseError;
        await fixture.Pipe.SendAsync(partial, exitCode: 0);

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var row = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationId == operationId);
        Assert.Equal("completed", row.Status);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.databaseErrorAfterSaving", warning.StageKey);
        Assert.Equal(databaseError, warning.Context["error"]);
        Assert.Equal(7L, warning.Context["entriesSaved"]);
        // The positions it reached stay saved, so the next pass reads on from them.
        Assert.Equal(25, fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADatabaseErrorBeforeAnyEntryWasSavedFailsNamingTheErrorAsync(bool batch)
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(batch
            ? fixture.Processor.StartProcessingAsync()
            : fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        // The same ending as above when the database stops answering before the first entry is saved.
        const string databaseError = "pool timed out while waiting for an open connection";
        var partial = TerminalProgress(0, 11, "partial", sourcePosition: 25);
        partial.FilesWithErrors = [fixture.LogFilePath + ": " + databaseError];
        partial.DatabaseError = databaseError;
        await fixture.Pipe.SendAsync(partial, exitCode: 0);

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var row = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationId == operationId);
        Assert.Equal("failed", row.Status);
        Assert.Empty(row.Warnings);
        Assert.Contains(databaseError, row.Error);
        Assert.Equal(25, fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APartialPassTagsTheDownloadsItSavedAsync(bool batch)
    {
        await using var fixture = new ProcessorFixture(
            transport: true,
            events: DispatchProxy.Create<IEventsService, TaggingEvents>());

        var run = fixture.Track(batch
            ? fixture.Processor.StartProcessingAsync()
            : fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        await fixture.Pipe!.ConnectAsync();
        // log_processor after one rotated member failed: terminal "partial", exit 0, the file named
        // "path: reason".
        var partial = TerminalProgress(7, 11, "partial", sourcePosition: 25);
        partial.FilesWithErrors =
        [
            Path.Combine(Path.GetDirectoryName(fixture.LogFilePath)!, "access.log.2.gz") + ": unexpected end of file"
        ];
        await fixture.Pipe.SendAsync(partial, exitCode: 0);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("auto-tag", fixture.Messages.RefreshSources);
    }

    [Fact]
    public async Task BatchUsesOneOperationAndKeepsTheFirstChildCountsAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true, datasourceCount: 2);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });
        fixture.State.SetLogPosition("beta", 7);

        var run = fixture.Track(fixture.Processor.StartProcessingAsync());
        var alpha = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(alpha, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        Assert.Equal(operationId, Assert.Single(fixture.RepairOwner.GetPendingRepairs()).Id);
        Assert.False(Assert.IsType<OperationInfo>(fixture.Tracker.GetOperation(operationId)).Status.IsTerminal());
        await fixture.Pipe.SendAsync(
            TerminalProgress(10, 20, "completed", sourcePosition: 25),
            exitCode: 0);

        var beta = await fixture.Pipe.ConnectAsync();
        fixture.AssertConnection(beta, "beta", 7);
        Assert.NotEqual(
            alpha.GetProperty("progressPath").GetString(),
            beta.GetProperty("progressPath").GetString());
        Assert.Equal(operationId, fixture.Processor.CurrentOperationId);
        Assert.Equal(operationId, Assert.Single(fixture.RepairOwner.GetPendingRepairs()).Id);
        Assert.False(Assert.IsType<OperationInfo>(fixture.Tracker.GetOperation(operationId)).Status.IsTerminal());
        await fixture.Pipe.SendAsync(
            TerminalProgress(0, 0, "completed", sourcePosition: 7),
            exitCode: 0);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(
            Assert.Single(fixture.Messages.Completions));
        Assert.Equal(operationId, complete.OperationId);
        Assert.True(complete.Success);
        Assert.Equal(10, complete.EntriesProcessed);
        Assert.Equal(20, complete.LinesProcessed);
        Assert.Equal(
            25,
            fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
        Assert.Equal(
            7,
            fixture.State.GetLogSourcePositions("beta")[LogSourceLayout.MonolithicStem]);
        Assert.Empty(fixture.RepairOwner.GetPendingRepairs());
        Assert.False(fixture.RepairOwner.OwnsRepair(operationId));
    }

    [Fact]
    public async Task AForceStopAfterProcessAllSavedItsSuccessEndsTheRunGreenAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true, datasourceCount: 2);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });
        fixture.State.SetLogPosition("beta", 7);
        var cancellation = new OperationCancellationService(
            fixture.Tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            fixture.RepairOwner,
            NullLogger<OperationCancellationService>.Instance);
        var operationId = Guid.Empty;
        Task<bool>? stop = null;
        // The force stop starts inside the batch's own outcome save, so it lands after the batch took its cancel flag.
        fixture.State.OnRepairWrite = contents =>
        {
            if (stop is not null) return;
            var repairs = JsonSerializer.Deserialize<List<OperationRepair>>(contents)!;
            if (!repairs.Any(repair => repair.Id == operationId && repair.Outcome == OperationStatus.Completed)) return;
            stop = Task.Run(() => cancellation.ForceKillAsync(operationId));
            SpinWait.SpinUntil(() => fixture.Tracker.GetOperation(operationId)!.Cancelled, TimeSpan.FromSeconds(10));
        };

        var run = fixture.Track(fixture.Processor.StartProcessingAsync());
        await fixture.Pipe!.ConnectAsync();
        operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        await fixture.Pipe.SendAsync(
            TerminalProgress(10, 20, "completed", sourcePosition: 25),
            exitCode: 0);
        await fixture.Pipe.ConnectAsync();
        await fixture.Pipe.SendAsync(
            TerminalProgress(0, 0, "completed", sourcePosition: 7),
            exitCode: 0);

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await stop!.WaitAsync(TimeSpan.FromSeconds(10)));
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(
            Assert.Single(fixture.Messages.Completions));
        Assert.True(complete.Success);
        Assert.False(complete.Cancelled);
        Assert.Equal(OperationStatus.Completed, fixture.Tracker.GetOperation(operationId)!.Status);
    }

    [Fact]
    public async Task ABatchThatFailedAfterADatasourceSkippedFilesStillNamesThemAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true, datasourceCount: 2);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });
        fixture.State.SetLogPosition("beta", 7);

        var run = fixture.Track(fixture.Processor.StartProcessingAsync());
        await fixture.Pipe!.ConnectAsync();
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        // log_processor after one rotated member failed: terminal "partial", exit 0, the file named
        // "path: reason".
        var partial = TerminalProgress(7, 11, "partial", sourcePosition: 25);
        partial.FilesWithErrors =
        [
            Path.Combine(Path.GetDirectoryName(fixture.LogFilePath)!, "access.log.2.gz") + ": unexpected end of file"
        ];
        await fixture.Pipe.SendAsync(partial, exitCode: 0);

        await fixture.Pipe.ConnectAsync();
        // The same ending FailedCheckpointWithExitOneKeepsCountsAndAllowsALaterLivePassAsync sends.
        await fixture.Pipe.SendAsync(
            TerminalProgress(0, 0, "failed", sourcePosition: 7),
            exitCode: 1);

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var row = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationId == operationId);
        Assert.Equal("completed", row.Status);
        Assert.Equal(2, row.Warnings.Count);
        var warning = Assert.Single(row.Warnings, item => item.StageKey == "common.notifications.warnings.logFilesSkipped");
        Assert.Equal("access.log.2.gz", warning.Context["fileNames"]);
        Assert.Contains(row.Warnings, item => item.StageKey == "common.notifications.warnings.datasourcesFailed");
    }

    [Fact]
    public async Task ABatchInWhichOneDatasourceFailedEndsAmberNamingItAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true, datasourceCount: 2);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });
        fixture.State.SetLogPosition("beta", 7);

        var run = fixture.Track(fixture.Processor.StartProcessingAsync());
        await fixture.Pipe!.ConnectAsync();
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        await fixture.Pipe.SendAsync(
            TerminalProgress(10, 20, "completed", sourcePosition: 25),
            exitCode: 0);

        await fixture.Pipe.ConnectAsync();
        // The same ending FailedCheckpointWithExitOneKeepsCountsAndAllowsALaterLivePassAsync sends.
        await fixture.Pipe.SendAsync(
            TerminalProgress(0, 0, "failed", sourcePosition: 7),
            exitCode: 1);

        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        var row = Assert.Single(fixture.Tracker.GetRuns().Runs, item => item.OperationId == operationId);
        Assert.Equal("completed", row.Status);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("common.notifications.warnings.datasourcesFailed", warning.StageKey);
        Assert.Equal("beta", warning.Context["datasources"]);
    }

    [Fact]
    public async Task BatchCancellationAfterAcceptedCountsKeepsCountsAndOffsetsAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true, datasourceCount: 2);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });
        fixture.State.SetLogSourcePositions("beta", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 7
        });

        var run = fixture.Track(fixture.Processor.StartProcessingAsync());
        var alpha = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(alpha, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        await fixture.Pipe.SendAsync(
            TerminalProgress(10, 20, "completed", sourcePosition: 25),
            exitCode: 0);

        var beta = await fixture.Pipe.ConnectAsync();
        fixture.AssertConnection(beta, "beta", 7);
        Assert.Equal(operationId, fixture.Processor.CurrentOperationId);
        var retained = Assert.Single(fixture.RepairOwner.GetPendingRepairs());
        Assert.Equal(operationId, retained.Id);
        Assert.Equal(10, retained.LogProcessing!.EntriesProcessed);
        Assert.Equal(20, retained.LogProcessing.LinesProcessed);
        var accepted = retained.Sources.Single(source => source.Datasource == "alpha");
        Assert.True(accepted.NativeCompletionAccepted);
        Assert.False(accepted.RefreshDownloads);
        Assert.Equal(
            25,
            fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);

        Assert.Equal(OperationCancelResult.Requested, fixture.Tracker.CancelOperation(operationId));
        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();

        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(
            Assert.Single(fixture.Messages.Completions));
        Assert.Equal(operationId, complete.OperationId);
        Assert.True(complete.Cancelled);
        Assert.Equal(OperationStatus.Cancelled, complete.Status);
        Assert.Equal(10, complete.EntriesProcessed);
        Assert.Equal(20, complete.LinesProcessed);
        Assert.Equal(
            25,
            fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
        Assert.Equal(
            7,
            fixture.State.GetLogSourcePositions("beta")[LogSourceLayout.MonolithicStem]);
        Assert.Empty(fixture.RepairOwner.GetPendingRepairs());
        Assert.False(fixture.RepairOwner.OwnsRepair(operationId));
        Assert.False(fixture.Processor.IsProcessing);
        Assert.Null(fixture.Processor.CurrentOperationId);
        Assert.Empty(fixture.Tracker.GetActiveOperations(OperationType.LogProcessing));
    }

    [Fact]
    public async Task FailedCommittedRefreshKeepsRepairAndCountsUntilRepairRefreshCompletesAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.Messages.HoldRepairRefresh = true;
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        await fixture.Pipe.SendAsync(
            TerminalProgress(7, 11, "completed", sourcePosition: 25),
            exitCode: 0);
        await fixture.Messages.RepairRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var succeeded = false;
        try
        {
            Assert.False(run.IsCompleted);
            Assert.Equal(2, fixture.Messages.RefreshAttempts);
            Assert.Equal(new string?[] { "rust-insert-early", null }, fixture.Messages.RefreshSources);
            // A log pass that owes only the refresh stores its outcome as completed and refreshes inline.
            Assert.Empty(fixture.RepairOwner.GetPendingRepairs());
            var retained = Assert.Single(fixture.State.LoadOperationRepairs());
            Assert.Equal(operationId, retained.Id);
            Assert.Equal(OperationRepairPhase.Completed, retained.Phase);
            Assert.Equal(7, retained.LogProcessing!.EntriesProcessed);
            Assert.Equal(11, retained.LogProcessing.LinesProcessed);
            var source = Assert.Single(retained.Sources);
            Assert.True(source.NativeCompletionAccepted);
            Assert.True(source.RefreshDownloads);
            Assert.Equal(
                25,
                fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
            Assert.Empty(fixture.Messages.Completions);
            Assert.False(Assert.IsType<OperationInfo>(fixture.Tracker.GetOperation(operationId)).Status.IsTerminal());
        }
        finally
        {
            fixture.Messages.ReleaseRepairRefresh.TrySetResult();
            succeeded = await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await fixture.Pipe.WaitForExitAsync();
        Assert.True(succeeded);
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(
            Assert.Single(fixture.Messages.Completions));
        Assert.Equal(operationId, complete.OperationId);
        Assert.True(complete.Success);
        Assert.Equal(7, complete.EntriesProcessed);
        Assert.Equal(11, complete.LinesProcessed);
        Assert.Empty(fixture.State.LoadOperationRepairs());
        Assert.Empty(fixture.RepairOwner.GetPendingRepairs());
        Assert.False(fixture.RepairOwner.OwnsRepair(operationId));
    }

    [Fact]
    public async Task StepArrivingDuringTheFirstDatasourceRunsBetweenTheBatchChildrenAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true, datasourceCount: 2);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });
        fixture.State.SetLogSourcePositions("beta", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 7
        });

        var run = fixture.Track(fixture.Processor.StartProcessingAsync());
        var alpha = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(alpha, "alpha", 5);
        var operationId = Assert.IsType<Guid>(fixture.Processor.CurrentOperationId);
        var step = LockStepAsync(fixture);
        Assert.False(step.IsCompleted);

        // The datasource the batch is on runs to its end and persists before the step gets the logs.
        await fixture.Pipe.SendAsync(
            TerminalProgress(10, 20, "completed", sourcePosition: 25),
            exitCode: 0);
        await using (await step.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(
                25,
                fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
            fixture.State.SetLogSourcePositions("beta", new Dictionary<string, long>
            {
                [LogSourceLayout.MonolithicStem] = 3
            });
        }

        var beta = await fixture.Pipe.ConnectAsync();
        fixture.AssertConnection(beta, "beta", 3);
        await fixture.Pipe.SendAsync(
            TerminalProgress(4, 6, "completed", sourcePosition: 9),
            exitCode: 0);

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(
            Assert.Single(fixture.Messages.Completions));
        Assert.Equal(operationId, complete.OperationId);
        Assert.True(complete.Success);
        Assert.Equal(OperationStatus.Completed, complete.Status);
        Assert.Equal(14, complete.EntriesProcessed);
        Assert.Equal(26, complete.LinesProcessed);
        Assert.Equal(
            9,
            fixture.State.GetLogSourcePositions("beta")[LogSourceLayout.MonolithicStem]);
    }

    [Fact]
    public async Task PassThatWaitedForAStepStartsFromThePositionsTheStepLeftAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 25
        });
        var step = await LockStepAsync(fixture);

        var run = fixture.Track(fixture.Processor.StartProcessingAsync(
            fixture.LogFilePath,
            liveIngest: true));
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 10
        });
        await step.DisposeAsync();

        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 10);
        await fixture.Pipe.SendAsync(
            TerminalProgress(0, 0, "completed", sourcePosition: 10),
            exitCode: 0);
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();
    }

    [Fact]
    public async Task PassGivesTheLogsBackAfterItsPositionsPersistAndBeforeItsRepairFinishesAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.Messages.HoldRepairRefresh = true;
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var step = LockStepAsync(fixture);
        Assert.False(step.IsCompleted);

        await fixture.Pipe.SendAsync(
            TerminalProgress(7, 11, "completed", sourcePosition: 25),
            exitCode: 0);
        await using (await step.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(
                25,
                fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
            await fixture.Messages.RepairRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
        }

        fixture.Messages.ReleaseRepairRefresh.TrySetResult();
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();
    }

    [Fact]
    public async Task FailedPassGivesTheLogsBackBeforeItsRepairFinishesAsync()
    {
        await using var fixture = new ProcessorFixture(transport: true);
        fixture.Messages.HoldRepairRefresh = true;
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long>
        {
            [LogSourceLayout.MonolithicStem] = 5
        });

        var run = fixture.Track(fixture.Processor.StartProcessingAsync(fixture.LogFilePath));
        var connection = await fixture.Pipe!.ConnectAsync();
        fixture.AssertConnection(connection, "alpha", 5);
        var step = LockStepAsync(fixture);
        Assert.False(step.IsCompleted);

        await fixture.Pipe.SendAsync(
            TerminalProgress(7, 11, "failed", sourcePosition: 25),
            exitCode: 1);
        await using (await step.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            await fixture.Messages.RepairRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
            Assert.Equal(
                5,
                fixture.State.GetLogSourcePositions("alpha")[LogSourceLayout.MonolithicStem]);
        }

        fixture.Messages.ReleaseRepairRefresh.TrySetResult();
        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Pipe.WaitForExitAsync();
    }

    [Fact]
    public async Task RegistrationFailure_DisposesTheUnadoptedCancellationSourceAsync()
    {
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, RegistrationFailureTracker>();
        var failedTracker = (RegistrationFailureTracker)(object)tracker;
        using var fixture = new ProcessorFixture(tracker);

        var started = await fixture.Processor.StartProcessingAsync(
            fixture.LogFilePath,
            liveIngest: true);

        Assert.False(started);
        var source = Assert.IsType<CancellationTokenSource>(failedTracker.Source);
        Assert.Throws<ObjectDisposedException>(() => _ = source.Token);
    }

    [Fact]
    public void BatchTerminalCleanup_LeavesCancellationSourceDisposalToTheTracker()
    {
        using var fixture = new ProcessorFixture();
        var begin = typeof(RustLogProcessorService).GetMethod("BeginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sourceField = typeof(RustLogProcessorService).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var operationId = (Guid)begin.Invoke(fixture.Processor, null)!;
        var registeredSource = fixture.Tracker.GetOperation(operationId)!.CancellationTokenSource!;
        using var cleanupSource = new DisposeTrackingCancellationTokenSource();
        sourceField.SetValue(fixture.Processor, cleanupSource);

        fixture.Tracker.CompleteOperation(operationId, false, cancelled: true);

        Assert.Throws<ObjectDisposedException>(() => _ = registeredSource.Token);
        Assert.Equal(0, cleanupSource.DisposeCalls);
        Assert.Null(sourceField.GetValue(fixture.Processor));
    }

    [Fact]
    public void EarlierTerminal_UsesItsOwnMetricsAndLeavesTheNextRunRegistered()
    {
        using var fixture = new ProcessorFixture();
        var begin = typeof(RustLogProcessorService).GetMethod("BeginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (Guid)begin.Invoke(fixture.Processor, null)!;
        var next = (Guid)begin.Invoke(fixture.Processor, null)!;
        var metricType = typeof(RustLogProcessorService).GetNestedType("LogProcessingTerminalMetrics", BindingFlags.NonPublic)!;
        var metrics = Activator.CreateInstance(metricType, [7L, 11L, 1.5, "first completed", "complete"])!;

        fixture.Tracker.CompleteOperation(first, true, onCompleting: operation => operation.Metadata = metrics);
        var losingPublication = false;
        fixture.Tracker.CompleteOperation(first, false, error: "late failure", onCompleting: _ => losingPublication = true);

        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(Assert.Single(fixture.Messages.Completions));
        Assert.Equal(first, complete.OperationId);
        Assert.Equal(7, complete.EntriesProcessed);
        Assert.Equal(11, complete.LinesProcessed);
        Assert.False(losingPublication);
        Assert.Equal(next, fixture.Processor.CurrentOperationId);
        Assert.True(fixture.Processor.IsProcessing);
        Assert.False(fixture.Tracker.GetOperation(next)!.Status.IsTerminal());
        fixture.Tracker.CompleteOperation(next, false, cancelled: true);
    }

    [Fact]
    public void BatchProgress_AggregatesCountsAndNeverRegresses()
    {
        var batch = new LogProcessingBatchState { ChildCount = 2 };

        Assert.Equal(25, RustLogProcessorService.ScaleBatchProgress(batch, 50));
        batch.CurrentEntriesProcessed = 3;
        batch.CurrentLinesProcessed = 5;
        batch.CurrentTotalLines = 8;
        batch.CurrentBytesProcessed = 100;
        batch.CurrentTotalBytes = 200;
        RustLogProcessorService.RecordBatchChild(
            batch,
            "alpha",
            succeeded: false,
            new LogProcessingProgress
            {
                EntriesSaved = 4,
                LinesParsed = 6,
                TotalLines = 9,
                BytesProcessed = 110,
                TotalBytes = 210
            });

        Assert.Equal(50, batch.PercentComplete);
        Assert.Equal(4, batch.EntriesProcessed);
        Assert.Equal(6, batch.LinesProcessed);
        Assert.Equal(9, batch.TotalLines);
        Assert.Equal(110, batch.BytesProcessed);
        Assert.Equal(210, batch.TotalBytes);
        Assert.Equal("alpha", batch.FailedDatasourceName);
        Assert.Equal(60, RustLogProcessorService.ScaleBatchProgress(batch, 20));
        Assert.Equal(60, RustLogProcessorService.ScaleBatchProgress(batch, 10));

        RustLogProcessorService.RecordBatchChild(
            batch,
            "beta",
            succeeded: true,
            new LogProcessingProgress
            {
                EntriesSaved = 7,
                LinesParsed = 11,
                TotalLines = 13,
                BytesProcessed = 220,
                TotalBytes = 230
            });

        Assert.Equal(100, batch.PercentComplete);
        Assert.Equal(11, batch.EntriesProcessed);
        Assert.Equal(17, batch.LinesProcessed);
        Assert.Equal(22, batch.TotalLines);
        Assert.Equal(330, batch.BytesProcessed);
        Assert.Equal(440, batch.TotalBytes);
        Assert.Equal("alpha", batch.FailedDatasourceName);
    }

    [Fact]
    public async Task AcceptedSourceCountsAccumulateOnceAcrossTheRepairAsync()
    {
        using var fixture = new ProcessorFixture();
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogProcessing,
            Name = "Log Processing",
            StartedAt = DateTime.UtcNow,
            Sources =
            [
                ProcessingSource("alpha", "logs/alpha"),
                ProcessingSource("beta", "logs/beta"),
                ProcessingSource("gamma", "logs/gamma")
            ],
            LogProcessing = new LogProcessingRepair()
        };
        await fixture.RepairOwner.PrepareRepairAsync(repair, CancellationToken.None);
        await fixture.RepairOwner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        await fixture.RepairOwner.StartWorkAsync(repair.Id, "beta", CancellationToken.None);
        await fixture.RepairOwner.StartWorkAsync(repair.Id, "gamma", CancellationToken.None);

        await InvokeSaveProcessingSourceAsync(
            fixture.Processor,
            fixture.RepairOwner,
            repair.Id,
            "alpha",
            TerminalProgress(entries: 10, lines: 20),
            refreshSatisfied: true);
        await InvokeSaveProcessingSourceAsync(
            fixture.Processor,
            fixture.RepairOwner,
            repair.Id,
            "beta",
            TerminalProgress(entries: 0, lines: 0),
            refreshSatisfied: true);
        await InvokeSaveProcessingSourceAsync(
            fixture.Processor,
            fixture.RepairOwner,
            repair.Id,
            "gamma",
            TerminalProgress(entries: 2, lines: 3),
            refreshSatisfied: true);
        await InvokeSaveProcessingSourceAsync(
            fixture.Processor,
            fixture.RepairOwner,
            repair.Id,
            "gamma",
            TerminalProgress(entries: 2, lines: 3),
            refreshSatisfied: true);

        var retained = Assert.Single(fixture.RepairOwner.GetPendingRepairs());
        Assert.Equal(12, retained.LogProcessing!.EntriesProcessed);
        Assert.Equal(23, retained.LogProcessing.LinesProcessed);
        Assert.All(retained.Sources, source =>
        {
            Assert.True(source.NativeCompletionAccepted);
            Assert.False(source.RefreshDownloads);
        });

        await fixture.RepairOwner.FinishRepairAsync(repair.Id, true, false, null)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(fixture.State.LoadOperationRepairs());
    }

    [Fact]
    public async Task LogRepairWithAChangedRootCompletesItsRefreshWithoutRootResolutionAsync()
    {
        using var fixture = new ProcessorFixture();
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogProcessing,
            Name = "Log Processing",
            StartedAt = DateTime.UtcNow,
            Sources = [ProcessingSource("removed", "logs/removed")],
            LogProcessing = new LogProcessingRepair()
        };
        await fixture.RepairOwner.PrepareRepairAsync(repair, CancellationToken.None);
        await fixture.RepairOwner.StartWorkAsync(repair.Id, "removed", CancellationToken.None);

        await fixture.RepairOwner.FinishRepairAsync(repair.Id, false, false, "interrupted")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("DownloadsRefresh", fixture.Messages.EventNames);
        Assert.Empty(fixture.State.LoadOperationRepairs());
    }

    [Fact]
    public async Task FinalPresentationKeepsPreviouslyAcceptedCountsAsync()
    {
        using var fixture = new ProcessorFixture();
        var repair = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogProcessing,
            Name = "Log Processing",
            StartedAt = DateTime.UtcNow,
            Sources = [ProcessingSource("alpha", "logs/alpha")],
            LogProcessing = new LogProcessingRepair()
        };
        await fixture.RepairOwner.PrepareRepairAsync(repair, CancellationToken.None);
        await fixture.RepairOwner.StartWorkAsync(repair.Id, "alpha", CancellationToken.None);
        await InvokeSaveProcessingSourceAsync(
            fixture.Processor,
            fixture.RepairOwner,
            repair.Id,
            "alpha",
            TerminalProgress(entries: 7, lines: 11),
            refreshSatisfied: false);

        var pending = Assert.Single(fixture.RepairOwner.GetPendingRepairs());
        Assert.Equal(7, pending.LogProcessing!.EntriesProcessed);
        Assert.Equal(11, pending.LogProcessing.LinesProcessed);
        Assert.True(pending.Sources.Single().RefreshDownloads);

        var confirmed = await InvokeFinishProcessingRepairAsync(
            fixture.Processor,
            fixture.RepairOwner,
            repair.Id);

        Assert.Equal(7, confirmed.Entries);
        Assert.Equal(11, confirmed.Lines);
        Assert.Empty(fixture.State.LoadOperationRepairs());
    }

    [Fact]
    public void FailedBatch_PublishesOneAggregateTerminal()
    {
        using var fixture = new ProcessorFixture();
        var begin = typeof(RustLogProcessorService).GetMethod("BeginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var operationId = (Guid)begin.Invoke(fixture.Processor, null)!;
        var batch = new LogProcessingBatchState
        {
            ChildCount = 1,
            CompletedChildren = 1,
            LinesProcessed = 17,
            PercentComplete = 100,
            FailedDatasourceName = "alpha",
            FailedDatasourceNames = { "alpha" }
        };

        fixture.Processor.CompleteBatchOperation(
            operationId,
            batch,
            entriesProcessed: 10,
            linesProcessed: 20,
            cancelled: false,
            batchFinished: true);
        fixture.Processor.CompleteBatchOperation(
            operationId,
            batch,
            entriesProcessed: 10,
            linesProcessed: 20,
            cancelled: false,
            batchFinished: true);

        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(Assert.Single(fixture.Messages.Completions));
        Assert.False(complete.Success);
        Assert.Equal(OperationStatus.Failed, complete.Status);
        Assert.Equal(10, complete.EntriesProcessed);
        Assert.Equal(20, complete.LinesProcessed);
        Assert.Contains("alpha", complete.Message, StringComparison.Ordinal);
        Assert.Null(fixture.Processor.CurrentOperationId);
        Assert.False(fixture.Processor.IsProcessing);
    }

    [Fact]
    public void Status_ExposesOnlyTheActiveDatasource()
    {
        using var fixture = new ProcessorFixture();
        var processing = typeof(RustLogProcessorService).GetProperty(nameof(RustLogProcessorService.IsProcessing))!;
        var datasource = typeof(RustLogProcessorService).GetField("_currentDatasourceName", BindingFlags.Instance | BindingFlags.NonPublic)!;

        processing.SetValue(fixture.Processor, true);
        datasource.SetValue(fixture.Processor, "alpha");
        Assert.Equal("alpha", fixture.Processor.GetStatus().DatasourceName);

        datasource.SetValue(fixture.Processor, null);
        Assert.Null(fixture.Processor.GetStatus().DatasourceName);
        processing.SetValue(fixture.Processor, false);
        Assert.Null(fixture.Processor.GetStatus().DatasourceName);
    }

    [Fact]
    public async Task ExternalCompletionBeforeWorkerFailure_PreservesTheNextRun()
    {
        using var fixture = new ProcessorFixture();
        fixture.Messages.Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Messages.Resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = fixture.Processor.StartProcessingAsync(fixture.LogFilePath);
        await fixture.Messages.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var original = fixture.Processor.CurrentOperationId!.Value;
        var registeredSource = fixture.Tracker.GetOperation(original)!.CancellationTokenSource!;
        var sourceField = typeof(RustLogProcessorService).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var cleanupSource = new DisposeTrackingCancellationTokenSource();
        sourceField.SetValue(fixture.Processor, cleanupSource);
        fixture.Tracker.CompleteOperation(original, false, error: "external failure");
        Assert.Throws<ObjectDisposedException>(() => _ = registeredSource.Token);
        Assert.Equal(0, cleanupSource.DisposeCalls);
        var begin = typeof(RustLogProcessorService).GetMethod("BeginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var next = (Guid)begin.Invoke(fixture.Processor, null)!;
        fixture.Messages.Resume.TrySetResult();
        Assert.False(await task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(next, fixture.Processor.CurrentOperationId);
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(Assert.Single(fixture.Messages.Completions));
        Assert.Equal(original, complete.OperationId);
        Assert.Equal("external failure", complete.Message);
        fixture.Tracker.CompleteOperation(next, false, cancelled: true);
    }

    public class CompletionMessages : DispatchProxy
    {
        public List<object> Completions { get; } = [];
        public List<string> EventNames { get; } = [];
        public List<string?> RefreshSources { get; } = [];
        public TaskCompletionSource? Started { get; set; }
        public TaskCompletionSource? Resume { get; set; }
        public TaskCompletionSource RepairRefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRepairRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldRepairRefresh { get; set; }
        public int RefreshAttempts { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                var eventName = (string)args![0]!;
                EventNames.Add(eventName);
                if (eventName == SignalREvents.DownloadsRefresh)
                {
                    var source = args[1] is null
                        ? null
                        : JsonSerializer.SerializeToElement(args[1]).GetProperty("source").GetString();
                    RefreshAttempts++;
                    RefreshSources.Add(source);
                    if (HoldRepairRefresh && RefreshAttempts == 1 && source == "rust-insert-early")
                    {
                        throw new IOException("Injected committed refresh failure.");
                    }
                    if (HoldRepairRefresh && RefreshAttempts == 2 && args[1] is null)
                    {
                        RepairRefreshStarted.TrySetResult();
                        return ReleaseRepairRefresh.Task;
                    }
                }
                if (args[1] is SignalRNotifications.LogProcessingComplete completion)
                    Completions.Add(completion);
                if (eventName == "LogProcessingStarted" && Started != null)
                {
                    Started.TrySetResult();
                    return Resume!.Task;
                }
                return Task.CompletedTask;
            }
            return targetMethod.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    /// <summary>Tags one download whenever a pass runs its auto-tag, so the "auto-tag" refresh shows it ran.</summary>
    internal class TaggingEvents : NullReturningProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IEventsService.AutoTagActiveEventsAsync)
                ? Task.FromResult(1)
                : base.Invoke(targetMethod, args);
    }

    private static OperationRepair RestoredRepair(RunNotice? notice)
    {
        return new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogProcessing,
            Name = "Log Processing",
            StartedAt = DateTime.UtcNow,
            Phase = OperationRepairPhase.Repairing,
            Outcome = OperationStatus.Failed,
            Error = "restored failure",
            Notice = notice,
            Sources =
            [
                new OperationRepairSource
                {
                    Datasource = "default",
                    LogRoot = "logs/default",
                    RefreshDownloads = true,
                    NativeLaunchAuthorized = true
                }
            ],
            LogProcessing = new LogProcessingRepair
            {
                EntriesProcessed = 3,
                LinesProcessed = 5,
                Message = "restored failure"
            }
        };
    }

    private static Task<LogFileLock> LockStepAsync(ProcessorFixture fixture) =>
        fixture.RepairOwner.LockLogFilesAsync(
            null,
            OperationType.GameRemoval,
            LogFileLockKind.Rows,
            CancellationToken.None);

    private static OperationRepairSource ProcessingSource(string datasource, string logRoot)
    {
        return new OperationRepairSource
        {
            Datasource = datasource,
            LogRoot = logRoot,
            RefreshDownloads = true
        };
    }

    private static LogProcessingProgress TerminalProgress(
        long entries,
        long lines,
        string terminalStatus = "completed",
        long? sourcePosition = null)
    {
        return new LogProcessingProgress
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid().ToString("N"),
            TerminalStatus = terminalStatus,
            Status = terminalStatus == "failed"
                ? OperationStatus.Failed.ToWireString()
                : OperationStatus.Completed.ToWireString(),
            EntriesSaved = entries,
            LinesParsed = lines,
            TotalLines = lines,
            PercentComplete = 100,
            StageKey = terminalStatus == "failed"
                ? "signalr.logProcessing.failed"
                : "signalr.logProcessing.complete",
            SourcePositions = sourcePosition.HasValue
                ? new Dictionary<string, long>
                {
                    [LogSourceLayout.MonolithicStem] = sourcePosition.Value
                }
                : new Dictionary<string, long>()
        };
    }

    private static async Task InvokeSaveProcessingSourceAsync(
        RustLogProcessorService processor,
        OperationStateService repairOwner,
        Guid operationId,
        string datasource,
        LogProcessingProgress progress,
        bool refreshSatisfied)
    {
        var method = typeof(RustLogProcessorService).GetMethod(
            "SaveProcessingSourceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            processor,
            [repairOwner, operationId, datasource, progress, refreshSatisfied]));
        await task;
    }

    private static async Task<(long Entries, long Lines)> InvokeFinishProcessingRepairAsync(
        RustLogProcessorService processor,
        OperationStateService repairOwner,
        Guid operationId)
    {
        var metricsType = typeof(RustLogProcessorService).GetNestedType(
            "LogProcessingTerminalMetrics",
            BindingFlags.NonPublic)!;
        var metrics = Activator.CreateInstance(
            metricsType,
            [0L, 0L, null, "failed", "logs.failed"])!;
        var method = typeof(RustLogProcessorService).GetMethod(
            "FinishProcessingRepairAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            processor,
            [repairOwner, operationId, metrics, false, false, "failed"]));
        await task;
        var confirmed = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return (
            (long)metricsType.GetProperty("EntriesProcessed")!.GetValue(confirmed)!,
            (long)metricsType.GetProperty("LinesProcessed")!.GetValue(confirmed)!);
    }

    public class RegistrationFailureTracker : DispatchProxy
    {
        public CancellationTokenSource? Source { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IUnifiedOperationTracker.RegisterOperation))
            {
                Source = (CancellationTokenSource)args![2]!;
                throw new InvalidOperationException("Registration failed");
            }
            if (targetMethod.Name == nameof(IUnifiedOperationTracker.GetActiveOperations))
            {
                return Array.Empty<OperationInfo>();
            }

            throw new NotSupportedException(targetMethod.Name);
        }
    }

    private sealed class DisposeTrackingCancellationTokenSource : CancellationTokenSource
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProcessorFixture : IDisposable, IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _services;
        private readonly OperationStateService _repairOwner;
        private readonly Dictionary<string, string> _logs = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Task<bool>> _runs = [];
        private readonly SemaphoreSlim? _postPassLock;

        public ProcessorFixture(
            IUnifiedOperationTracker? tracker = null,
            bool transport = false,
            int datasourceCount = 1,
            IEventsService? events = null)
        {
            if (datasourceCount is < 1 or > 2)
            {
                throw new ArgumentOutOfRangeException(nameof(datasourceCount));
            }

            _root = Path.Combine(Path.GetTempPath(), $"log-processing-ownership-{Guid.NewGuid():N}");
            var logPath = Path.Combine(_root, "logs");
            Directory.CreateDirectory(_root);
            var operationsPath = Path.Combine(_root, "GetOperationsDirectory");
            Directory.CreateDirectory(operationsPath);

            var settings = new Dictionary<string, string?>();
            for (var index = 0; index < datasourceCount; index++)
            {
                var name = transport
                    ? index == 0 ? "alpha" : "beta"
                    : index == 0 ? "default" : "secondary";
                var cachePath = datasourceCount == 1 && !transport
                    ? Path.Combine(_root, "cache")
                    : Path.Combine(_root, name, "cache");
                var sourceLogPath = datasourceCount == 1 && !transport
                    ? logPath
                    : Path.Combine(_root, name, "logs");
                Directory.CreateDirectory(cachePath);
                Directory.CreateDirectory(sourceLogPath);
                var logFile = Path.Combine(sourceLogPath, LogSourceLayout.MonolithicStem);
                File.WriteAllText(logFile, string.Empty);
                _logs[name] = logFile;
                settings[$"LanCache:DataSources:{index}:Name"] = name;
                settings[$"LanCache:DataSources:{index}:CachePath"] = cachePath;
                settings[$"LanCache:DataSources:{index}:LogPath"] = sourceLogPath;
                settings[$"LanCache:DataSources:{index}:Enabled"] = "true";
            }
            LogFilePath = _logs.Values.First();

            IPathResolver pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = _root;
            if (transport)
            {
                Pipe = new LogPipe(operationsPath);
                var executable = Path.Combine(
                    AppContext.BaseDirectory,
                    "log-process",
                    OperatingSystem.IsWindows() ? "LogProcess.exe" : "LogProcess");
                Assert.True(File.Exists(executable), $"Built log process is missing: {executable}");
                var forwarded = DispatchProxy.Create<IPathResolver, LogPaths>();
                ((LogPaths)(object)forwarded).Inner = pathResolver;
                ((LogPaths)(object)forwarded).Executable = executable;
                pathResolver = forwarded;
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();
            var notifications = DispatchProxy.Create<ISignalRNotificationService, CompletionMessages>();
            Messages = (CompletionMessages)(object)notifications;
            events ??= DispatchProxy.Create<IEventsService, NullReturningProxy>();
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            Tracker = tracker ?? new UnifiedOperationTracker(
                processManager,
                NullLogger<UnifiedOperationTracker>.Instance);
            State = CreateStateService(_root, configuration, pathResolver);
            var datasources = new DatasourceService(
                configuration,
                pathResolver,
                NullLogger<DatasourceService>.Instance);
            var contexts = new TestDbContextFactory(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase("log-processing-" + Guid.NewGuid().ToString("N"))
                    .Options);
            OperationStateService? repairOwner = null;
            RustLogProcessorService? processor = null;
            _services = new ServiceCollection()
                .AddSingleton(_ => repairOwner!)
                .AddSingleton(_ => processor!)
                .AddSingleton(datasources)
                .AddSingleton(new DatasourceCapabilityService(datasources))
                .AddSingleton(notifications)
                .AddSingleton<IEventsService>(events)
                .AddSingleton<IDbContextFactory<AppDbContext>>(contexts)
                .AddScoped(_ => contexts.CreateDbContext())
                .BuildServiceProvider();
            _repairOwner = repairOwner = new OperationStateService(
                NullLogger<OperationStateService>.Instance,
                configuration,
                State,
                _services.GetRequiredService<IServiceScopeFactory>(),
                DispatchProxy.Create<IHostApplicationLifetime, NullReturningProxy>(),
                processManager,
                Tracker);
            _repairOwner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            Processor = processor = new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                pathResolver,
                notifications,
                State,
                _services,
                new RustProcessHelper(
                    NullLogger<RustProcessHelper>.Instance,
                    processManager,
                    pathResolver,
                    Tracker),
                datasources,
                Tracker);
            if (transport)
            {
                _postPassLock = Assert.IsType<SemaphoreSlim>(typeof(RustLogProcessorService)
                    .GetField("_postPassLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(Processor));
                _postPassLock.Wait();
            }
        }

        public string LogFilePath { get; }
        public IUnifiedOperationTracker Tracker { get; }
        public RustLogProcessorService Processor { get; }
        public CompletionMessages Messages { get; }
        public OperationStateService RepairOwner => _repairOwner;
        public OperationRepairTests.FailingStateService State { get; }
        public LogPipe? Pipe { get; }

        public Task<bool> Track(Task<bool> run)
        {
            _runs.Add(run);
            return run;
        }

        public void AssertConnection(JsonElement connection, string datasource, long position)
        {
            Assert.Equal("log-processing", connection.GetProperty("command").GetString());
            Assert.True(connection.GetProperty("processId").GetInt32() > 0);
            var arguments = connection.GetProperty("arguments")
                .EnumerateArray()
                .Select(argument => argument.GetString()!)
                .ToArray();
            Assert.Equal(5, arguments.Length);
            Assert.Equal(
                Path.GetFullPath(Path.GetDirectoryName(_logs[datasource])!),
                Path.GetFullPath(arguments[0]));
            Assert.Equal(arguments[1], connection.GetProperty("progressPath").GetString());
            Assert.Equal("1", arguments[2]);
            Assert.Equal(datasource, arguments[3]);

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root))
                + Path.DirectorySeparatorChar;
            Assert.All(
                new[] { arguments[0], arguments[1], arguments[4] },
                path => Assert.True(
                    Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase),
                    $"Helper path escaped the fixture root: {path}"));
            using var positions = JsonDocument.Parse(File.ReadAllText(arguments[4]));
            Assert.Equal(1, positions.RootElement.GetProperty("schema_version").GetInt32());
            Assert.Equal(
                position,
                positions.RootElement
                    .GetProperty("sources")
                    .GetProperty(LogSourceLayout.MonolithicStem)
                    .GetInt64());
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            Messages.Resume?.TrySetResult();
            Messages.ReleaseRepairRefresh.TrySetResult();
            foreach (var operation in Tracker.GetActiveOperations(OperationType.LogProcessing))
            {
                Tracker.ForceKillOperation(operation.Id);
            }
            if (Pipe != null)
            {
                await Pipe.DisposeAsync();
            }
            foreach (var run in _runs)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception) when (run.IsCompleted)
                {
                    // The test owns the outcome; teardown only waits for the task to settle.
                }
            }
            _postPassLock?.Release();
            await _repairOwner.StopAsync(CancellationToken.None);
            await _services.DisposeAsync();
            if (!string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(_root)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The log-processing fixture directory is outside the temporary directory");
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static OperationRepairTests.FailingStateService CreateStateService(
            string root,
            IConfiguration configuration,
            IPathResolver pathResolver)
        {
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "dp-keys")));
            var apiKeyService = new ApiKeyService(
                NullLogger<ApiKeyService>.Instance,
                configuration,
                pathResolver);
            var encryption = new SecureStateEncryptionService(
                dataProtection,
                apiKeyService,
                NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance,
                pathResolver,
                encryption);
            var state = new OperationRepairTests.FailingStateService(
                NullLogger<StateService>.Instance,
                pathResolver,
                encryption,
                steamAuthStorage);

            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(state, new AppState { SetupCompleted = true });
            return state;
        }
    }

    public class LogPaths : DispatchProxy
    {
        public IPathResolver Inner { get; set; } = null!;
        public string Executable { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IPathResolver.GetRustLogProcessorPath)
                ? Executable
                : targetMethod.Invoke(Inner, args);
    }

    internal sealed class LogPipe : IAsyncDisposable
    {
        private readonly string _name = "log-processing-" + Guid.NewGuid().ToString("N");
        private readonly List<Process> _processes = [];
        private NamedPipeServerStream? _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public LogPipe(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "log-processing-pipe"), _name);
            _pipe = CreateServer();
        }

        public async Task<JsonElement> ConnectAsync()
        {
            if (_reader != null)
            {
                _reader.Dispose();
                await _writer!.DisposeAsync();
                await _pipe!.DisposeAsync();
                _pipe = CreateServer();
            }

            await _pipe!.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _reader = new StreamReader(_pipe, leaveOpen: true);
            _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var connection = JsonDocument.Parse(line!);
            var process = Process.GetProcessById(connection.RootElement.GetProperty("processId").GetInt32());
            _ = process.Handle;
            _processes.Add(process);
            return connection.RootElement.Clone();
        }

        public async Task SendAsync(LogProcessingProgress checkpoint, int? exitCode = null)
        {
            var element = JsonSerializer.SerializeToElement(checkpoint);
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Checkpoint = element,
                ExitCode = exitCode
            })).WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task WaitForExitAsync()
        {
            foreach (var process in _processes)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(process.HasExited);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _reader?.Dispose();
            if (_writer != null)
            {
                await _writer.DisposeAsync();
            }
            if (_pipe != null)
            {
                await _pipe.DisposeAsync();
            }
            foreach (var process in _processes)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                process.Dispose();
            }
        }

        private NamedPipeServerStream CreateServer()
        {
            return new NamedPipeServerStream(
                _name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }
    }
}
