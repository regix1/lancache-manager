using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class LogsControllerRustFileOperationsTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ResetToBeginning_IsStateOnlyAndDoesNotLaunchRustAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        fixture.State.SetLogTotalLines("alpha", 41);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");

        var result = await fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 0 });

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, response.Position);
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
        Assert.Empty(fixture.RustHelper.CountRequests);
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.True(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetAllToBeginning_IsStateOnlyForEveryDatasourceAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        fixture.State.SetLogPosition("beta", 19);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");

        var result = await fixture.Controller.ResetLogPositionAsync(
            new UpdateLogPositionRequest { Position = 0 });

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, response.Position);
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogPosition("beta"));
        Assert.Empty(fixture.RustHelper.CountRequests);
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.False(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetToEnd_CountsEachDatasourceOnceAndReturnsAggregateAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");
        fixture.RustHelper.CountHandler = (path, _) => Task.FromResult(
            new LogLineCountResult(path == fixture.AlphaLogPath ? 3 : 5, 1, new Dictionary<string, long>()));

        var result = await fixture.Controller.ResetLogPositionAsync(request: null);

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(8, response.Position);
        Assert.Equal(3, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(3, fixture.State.GetLogTotalLines("alpha"));
        Assert.Equal(5, fixture.State.GetLogPosition("beta"));
        Assert.Equal(5, fixture.State.GetLogTotalLines("beta"));
        Assert.Equal(new[] { fixture.AlphaLogPath, fixture.BetaLogPath }, fixture.RustHelper.CountRequests);
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.False(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetToEnd_RustFailureDoesNotOverwriteDatasourceStateAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 12);
        fixture.State.SetLogTotalLines("alpha", 13);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.RustHelper.CountHandler = (_, _) =>
            throw new RustProcessException("log_service_manager", 1, "fixture failure", "count-lines");

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.ResetDatasourceLogPositionAsync("alpha", request: null));

        Assert.Equal(12, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(13, fixture.State.GetLogTotalLines("alpha"));
        Assert.True(File.Exists(fixture.ResumePath("alpha")));
    }

    [Fact]
    public async Task ResetToEnd_CancellationDoesNotPersistFallbackZeroAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 21);
        fixture.State.SetLogTotalLines("alpha", 22);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.RustHelper.CountHandler = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LogLineCountResult(0, 0, new Dictionary<string, long>()));
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Controller.ResetDatasourceLogPositionAsync(
                "alpha",
                request: null,
                cancellation.Token));

        Assert.Equal(21, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(22, fixture.State.GetLogTotalLines("alpha"));
        Assert.True(File.Exists(fixture.ResumePath("alpha")));
    }

    [Fact]
    public async Task ResetToEnd_MultiDatasourceFailureKeepsEarlierSuccessfulCountAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("beta", 30);
        fixture.State.SetLogTotalLines("beta", 31);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");
        fixture.RustHelper.CountHandler = (path, _) =>
        {
            if (path == fixture.AlphaLogPath)
            {
                return Task.FromResult(new LogLineCountResult(4, 1, new Dictionary<string, long>()));
            }

            throw new RustProcessException("log_service_manager", 1, "fixture failure", "count-lines");
        };

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.ResetLogPositionAsync(request: null));

        Assert.Equal(4, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(4, fixture.State.GetLogTotalLines("alpha"));
        Assert.Equal(30, fixture.State.GetLogPosition("beta"));
        Assert.Equal(31, fixture.State.GetLogTotalLines("beta"));
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.True(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetToEnd_HoldsProcessingGateWhileCountRunsAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        fixture.WriteResume("alpha", "alpha-resume");
        var countStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RustHelper.CountHandler = async (path, _) =>
        {
            countStarted.TrySetResult(true);
            await releaseCount.Task;
            return new LogLineCountResult(
                7,
                1,
                new Dictionary<string, long> { ["access.log"] = 7 });
        };

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 });
        await countStarted.Task;

        Assert.True(fixture.Processor.IsProcessing);
        Assert.False(await fixture.Processor.StartProcessingAsync(
            fixture.AlphaLogPath,
            liveIngest: true,
            datasourceName: "alpha"));
        var conflict = await fixture.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);
        Assert.NotNull(conflict);
        Assert.Equal(nameof(OperationType.LogProcessing), conflict!.ActiveOperationType);

        releaseCount.TrySetResult(true);
        var result = await reset;

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.False(fixture.Processor.IsProcessing);
        Assert.Empty(fixture.Tracker.GetActiveOperations());
    }

    [Fact]
    public async Task ProcessingCleanupCannotClearResetReservationAsync()
    {
        using var fixture = new ControllerFixture();
        var reservation = await fixture.Processor.TryReserveProcessingGateAsync();
        Assert.NotNull(reservation);

        SetRunFlag(fixture.Processor, false);

        Assert.True(fixture.Processor.IsProcessing);
        fixture.Processor.ReleaseProcessingGate(reservation.Value);
        Assert.False(fixture.Processor.IsProcessing);
    }

    [Fact]
    public void StaleReservationReleaseCannotClearRunningPass()
    {
        using var fixture = new ControllerFixture();
        SetRunFlag(fixture.Processor, true);

        fixture.Processor.ReleaseProcessingGate(Guid.NewGuid());

        Assert.True(fixture.Processor.IsProcessing);
        SetRunFlag(fixture.Processor, false);
    }

    [Fact]
    public async Task ResetToEnd_ActiveLogProcessingConflictDoesNotCountAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Existing log processing",
            new CancellationTokenSource());

        var result = await fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 });

        var refusal = Assert.IsType<ErrorResponse>(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.Equal("errors.logs.processingActive", refusal.StageKey);
        Assert.Empty(fixture.RustHelper.CountRequests);
    }

    [Theory]
    [InlineData(OperationType.DownloadHistoryUpgrade, false)]
    [InlineData(OperationType.DownloadHistoryUpgrade, true)]
    [InlineData(OperationType.CacheSizeScan, false)]
    [InlineData(OperationType.CacheSizeScan, true)]
    public async Task ResetToEnd_RunsBesideAJobThatIsNotLogProcessingAsync(OperationType activeType, bool resetAll)
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        fixture.State.SetLogPosition("beta", 19);
        fixture.RustHelper.CountHandler = (_, _) => Task.FromResult(
            new LogLineCountResult(3, 1, new Dictionary<string, long>()));
        fixture.Tracker.RegisterOperation(activeType, "Other job", new CancellationTokenSource());

        var result = resetAll
            ? await fixture.Controller.ResetLogPositionAsync(request: null)
            : await fixture.Controller.ResetDatasourceLogPositionAsync("alpha", request: null);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(resetAll ? 3 : 19, fixture.State.GetLogPosition("beta"));
    }

    [Fact]
    public async Task ResetToEnd_WaitsForAStepThenHoldsTheLogsWhileItCountsAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        fixture.RustHelper.CountHandler = async (_, _) =>
        {
            // Counting outside the lock would let a step lower positions that this write then undoes.
            await fixture.RepairOwner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
            return new LogLineCountResult(9, 1, new Dictionary<string, long> { ["access.log"] = 9 });
        };
        var step = await HoldStepAsync(fixture);

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 });
        Assert.False(reset.IsCompleted);
        Assert.Empty(fixture.RustHelper.CountRequests);

        await step.DisposeAsync();
        Assert.IsType<OkObjectResult>(await reset.WaitAsync(_wait));
        Assert.Equal(9, fixture.State.GetLogPosition("alpha"));
        Assert.False(fixture.Processor.IsProcessing);
        await fixture.RepairOwner.WaitForLogStepAsync(active: false, CancellationToken.None).WaitAsync(_wait);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    public async Task Reset_WaitsForAStepBeforeRefusingForThePassBehindItAsync(long position)
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        var step = await HoldStepAsync(fixture);
        // A live pass that started during the step has set IsProcessing and waits at the lock.
        SetRunFlag(fixture.Processor, true);
        var pass = fixture.RepairOwner.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = position });
        Assert.False(reset.IsCompleted);

        await step.DisposeAsync();
        await using var passLock = await pass.WaitAsync(_wait);
        var result = await reset.WaitAsync(_wait);

        var refusal = Assert.IsType<ErrorResponse>(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.Equal("errors.logs.processingActive", refusal.StageKey);
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        Assert.Empty(fixture.RustHelper.CountRequests);
        SetRunFlag(fixture.Processor, false);
    }

    [Fact]
    public async Task ResetToEnd_CancelledWhileWaitingForTheLogsLeavesProcessingFreeAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        using var cancellation = new CancellationTokenSource();
        // A step that holds the logs still waits for the running speed tracker child to exit.
        Assert.True(fixture.RepairOwner.TryBeginSpeedTrackerRun());

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 },
            cancellation.Token);
        await fixture.RepairOwner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
        Assert.True(fixture.Processor.IsProcessing);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset.WaitAsync(_wait));

        Assert.False(fixture.Processor.IsProcessing);
        Assert.Empty(fixture.Tracker.GetActiveOperations());
        Assert.Empty(fixture.RustHelper.CountRequests);
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        await fixture.RepairOwner.WaitForLogStepAsync(active: false, CancellationToken.None).WaitAsync(_wait);
        fixture.RepairOwner.EndSpeedTrackerRun();
    }

    [Fact]
    public async Task ResetToBeginning_WaitsForAStepHoldingTheLogsAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        var step = await HoldStepAsync(fixture);

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 0 });
        Assert.False(reset.IsCompleted);
        Assert.Equal(17, fixture.State.GetLogPosition("alpha"));

        await step.DisposeAsync();
        Assert.IsType<OkObjectResult>(await reset.WaitAsync(_wait));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
    }

    [Fact]
    public async Task GetLogPositions_FirstRunUsesRustLineCountFallbackAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.RustHelper.CountHandler = (path, _) => Task.FromResult(
            new LogLineCountResult(path == fixture.AlphaLogPath ? 2 : 6, 1, new Dictionary<string, long>()));

        var result = await fixture.Controller.GetLogPositionsAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var positions = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, positions[0].GetProperty("totalLines").GetInt64());
        Assert.Equal(6, positions[1].GetProperty("totalLines").GetInt64());
        Assert.Equal(new[] { fixture.AlphaLogPath, fixture.BetaLogPath }, fixture.RustHelper.CountRequests);
    }

    [Fact]
    public async Task DeleteLogFile_UsesRustThenResetsStateAndToleratesNginxReopenFailureAsync()
    {
        using var fixture = new ControllerFixture();
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, "sixsix");
        fixture.State.SetLogPosition("alpha", 9);
        fixture.State.SetLogTotalLines("alpha", 9);
        fixture.RustHelper.DeleteHandler = (path, _) =>
        {
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            return Task.FromResult(new LogFileDeletionResult(bytes));
        };

        var result = await fixture.Controller.DeleteLogFileAsync("alpha");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { logPath }, fixture.RustHelper.DeleteRequests);
        Assert.False(File.Exists(logPath));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_AbortedRequestStillResetsThePositionsAsync()
    {
        ReopenWaitingNginx? nginx = null;
        using var fixture = new ControllerFixture(paths => nginx = new ReopenWaitingNginx(paths));
        await File.WriteAllTextAsync(Path.Combine(fixture.AlphaLogPath, "access.log"), "sixsix");
        fixture.State.SetLogPosition("alpha", 9);
        fixture.State.SetLogTotalLines("alpha", 9);
        fixture.RustHelper.DeleteHandler = (path, _) =>
        {
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            return Task.FromResult(new LogFileDeletionResult(bytes));
        };
        using var request = new CancellationTokenSource();

        var delete = fixture.Controller.DeleteLogFileAsync("alpha", request.Token);
        await nginx!.ReopenReached.Task.WaitAsync(_wait);
        await request.CancelAsync();
        nginx.ReleaseReopen.SetResult();

        Assert.IsType<OkObjectResult>(await delete.WaitAsync(_wait));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_AbortDuringTheDeleteStillResetsThePositionsAsync()
    {
        using var fixture = new ControllerFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.AlphaLogPath, "access.log"), "sixsix");
        fixture.State.SetLogPosition("alpha", 9);
        fixture.State.SetLogTotalLines("alpha", 9);
        using var request = new CancellationTokenSource();
        fixture.RustHelper.DeleteHandler = (path, token) =>
        {
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            request.Cancel();
            // The real helper checks the token again after the child exits 0.
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new LogFileDeletionResult(bytes));
        };

        var result = await fixture.Controller.DeleteLogFileAsync("alpha", request.Token);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_FailedDeleteStillResetsThePositionsAsync()
    {
        using var fixture = new ControllerFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.AlphaLogPath, "access.log"), "sixsix");
        fixture.State.SetLogPosition("alpha", 9);
        fixture.State.SetLogTotalLines("alpha", 9);
        fixture.RustHelper.DeleteHandler = (path, _) =>
        {
            File.Delete(path);
            throw new IOException("Injected unlink failure.");
        };

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Controller.DeleteLogFileAsync("alpha"));

        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_WaitsForAStepThenHoldsTheLogsWhileDeletingAsync()
    {
        using var fixture = new ControllerFixture();
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, "sixsix");
        fixture.State.SetLogPosition("alpha", 9);
        fixture.RustHelper.DeleteHandler = async (path, _) =>
        {
            await fixture.RepairOwner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            return new LogFileDeletionResult(bytes);
        };
        var step = await HoldStepAsync(fixture);

        var delete = fixture.Controller.DeleteLogFileAsync("alpha");
        Assert.False(delete.IsCompleted);
        Assert.Empty(fixture.RustHelper.DeleteRequests);
        Assert.Equal(9, fixture.State.GetLogPosition("alpha"));

        await step.DisposeAsync();
        Assert.IsType<OkObjectResult>(await delete.WaitAsync(_wait));
        Assert.False(File.Exists(logPath));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        await fixture.RepairOwner.WaitForLogStepAsync(active: false, CancellationToken.None).WaitAsync(_wait);
    }

    [Fact]
    public async Task DeleteLogFile_RustFailureLeavesFileAndStateUntouchedAsync()
    {
        using var fixture = new ControllerFixture();
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, "keep");
        fixture.State.SetLogPosition("alpha", 7);
        fixture.State.SetLogTotalLines("alpha", 8);
        fixture.RustHelper.DeleteHandler = (_, _) =>
            throw new RustProcessException("log_service_manager", 1, "fixture failure", "delete-file");

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.DeleteLogFileAsync("alpha"));

        Assert.True(File.Exists(logPath));
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(8, fixture.State.GetLogTotalLines("alpha"));
    }

    public sealed record LogDeleteCase(
        string Name,
        (string File, int Lines)[] Files,
        Dictionary<string, long> Saved,
        long LegacySaved,
        string[] Deleted,
        bool Fails,
        Dictionary<string, long> Expected,
        long ExpectedTotal,
        int ReplacementLines = 0,
        long StaleRecords = 0,
        (string From, string To)[]? Renames = null);

    public static TheoryData<LogDeleteCase> LogDeleteCases => new()
    {
        new LogDeleteCase(
            "monolithic success, no rotation",
            new[] { ("access.log", 6) },
            new Dictionary<string, long> { ["access.log"] = 6 },
            0,
            new[] { "access.log" },
            false,
            new Dictionary<string, long> { ["access.log"] = 0 },
            0),
        new LogDeleteCase(
            "monolithic success keeps the rotation's read lines",
            new[] { ("access.log.1", 4), ("access.log", 3) },
            new Dictionary<string, long> { ["access.log"] = 7 },
            0,
            new[] { "access.log" },
            false,
            new Dictionary<string, long> { ["access.log"] = 4 },
            4),
        new LogDeleteCase(
            "monolithic success, datasource with only a legacy total",
            new[] { ("access.log.1", 4), ("access.log", 3) },
            new Dictionary<string, long>(),
            7,
            new[] { "access.log" },
            false,
            new Dictionary<string, long> { ["access.log"] = 4 },
            4),
        new LogDeleteCase(
            "monolithic success, saved position inside the rotation",
            new[] { ("access.log.1", 4), ("access.log", 3) },
            new Dictionary<string, long> { ["access.log"] = 2 },
            0,
            new[] { "access.log" },
            false,
            new Dictionary<string, long> { ["access.log"] = 2 },
            4),
        new LogDeleteCase(
            "per-service success removes every series",
            new[] { ("steam-access.log.1", 4), ("steam-access.log", 3), ("epicgames-access.log", 2) },
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 2 },
            0,
            new[] { "steam-access.log", "steam-access.log.1", "epicgames-access.log" },
            false,
            new Dictionary<string, long> { ["steam-access.log"] = 0, ["epicgames-access.log"] = 0 },
            0),
        new LogDeleteCase(
            "per-service failure before any unlink",
            new[] { ("steam-access.log.1", 4), ("steam-access.log", 3), ("epicgames-access.log", 2) },
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 2 },
            0,
            Array.Empty<string>(),
            true,
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 2 },
            9),
        new LogDeleteCase(
            "per-service failure after a current file, before its rotation",
            new[] { ("steam-access.log.1", 4), ("steam-access.log", 3), ("epicgames-access.log", 2) },
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 2 },
            0,
            new[] { "steam-access.log" },
            true,
            new Dictionary<string, long> { ["steam-access.log"] = 4, ["epicgames-access.log"] = 2 },
            6),
        new LogDeleteCase(
            "per-service failure after a whole series",
            new[] { ("steam-access.log.1", 4), ("steam-access.log", 3), ("epicgames-access.log", 2) },
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 2 },
            0,
            new[] { "epicgames-access.log" },
            true,
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 0 },
            7),
        new LogDeleteCase(
            "a rotation of a series that keeps its current file disappears during the delete, which then fails before any unlink",
            new[] { ("steam-access.log.1", 4), ("steam-access.log", 3), ("epicgames-access.log", 2) },
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 2 },
            0,
            new[] { "steam-access.log.1" },
            true,
            new Dictionary<string, long> { ["steam-access.log"] = 7, ["epicgames-access.log"] = 2 },
            5),
        new LogDeleteCase(
            "monolithic failure after the unlink",
            new[] { ("access.log.1", 4), ("access.log", 3) },
            new Dictionary<string, long> { ["access.log"] = 7 },
            0,
            new[] { "access.log" },
            true,
            new Dictionary<string, long> { ["access.log"] = 4 },
            4),
        new LogDeleteCase(
            "monolithic success, nginx re-creates access.log before the check",
            new[] { ("access.log.1", 4), ("access.log", 3) },
            new Dictionary<string, long> { ["access.log"] = 7 },
            0,
            new[] { "access.log" },
            false,
            new Dictionary<string, long> { ["access.log"] = 4 },
            4,
            ReplacementLines: 2),
        new LogDeleteCase(
            "per-service success after a reset to the beginning for all",
            new[] { ("steam-access.log.1", 4), ("steam-access.log", 3), ("epicgames-access.log", 2) },
            new Dictionary<string, long>(),
            0,
            new[] { "steam-access.log", "steam-access.log.1", "epicgames-access.log" },
            false,
            new Dictionary<string, long>(),
            0),
        new LogDeleteCase(
            "monolithic success after logrotate removed a read rotation since the last import",
            new[] { ("access.log.1", 6), ("access.log", 3) },
            new Dictionary<string, long> { ["access.log"] = 10 },
            0,
            new[] { "access.log" },
            false,
            new Dictionary<string, long> { ["access.log"] = 4 },
            6,
            StaleRecords: 6),
        new LogDeleteCase(
            "monolithic success, logrotate shifts the rotation during the delete",
            new[] { ("access.log.1", 4), ("access.log", 3) },
            new Dictionary<string, long> { ["access.log"] = 7 },
            0,
            new[] { "access.log" },
            false,
            new Dictionary<string, long> { ["access.log"] = 4 },
            4,
            Renames: new[] { ("access.log.1", "access.log.2") })
    };

    [Theory]
    [MemberData(nameof(LogDeleteCases))]
    public async Task DeleteLogFile_KeepsTheReadLinesStillOnDiskAsync(LogDeleteCase deleteCase)
    {
        using var fixture = new ControllerFixture();
        foreach (var (file, lines) in deleteCase.Files)
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.AlphaLogPath, file),
                string.Concat(Enumerable.Repeat("x\n", lines)));
        }
        if (deleteCase.LegacySaved > 0)
        {
            fixture.State.SetLogPosition("alpha", deleteCase.LegacySaved);
        }
        else
        {
            fixture.State.SetLogSourcePositions("alpha", deleteCase.Saved);
        }
        fixture.State.SetLogTotalLines("alpha", deleteCase.Files.Sum(entry => entry.Lines));
        var countsPath = Path.Combine(fixture.OperationsPath, "log_count_progress_alpha.json");
        await File.WriteAllTextAsync(countsPath, "{}");
        fixture.RustHelper.CountHandler = (path, _) =>
        {
            var perStem = new Dictionary<string, long>();
            var perFile = new Dictionary<string, long>();
            var files = 0;
            foreach (var file in Directory.GetFiles(path))
            {
                var stem = LogSourceLayout.LogicalStem(Path.GetFileName(file));
                if (stem is null)
                {
                    continue;
                }
                var lines = File.ReadAllText(file).Count(c => c == '\n');
                perStem[stem] = perStem.GetValueOrDefault(stem) + lines;
                perFile[Path.GetFileName(file)] = lines;
                files++;
            }
            return Task.FromResult(new LogLineCountResult(perStem.Values.Sum(), files, perStem)
            {
                FileLineCounts = perFile,
                StaleReadRecords = deleteCase.StaleRecords > 0
                    ? deleteCase.Saved.ToDictionary(
                        pair => pair.Key,
                        pair => new StaleReadRecords(pair.Value, deleteCase.StaleRecords))
                    : new Dictionary<string, StaleReadRecords>()
            });
        };
        var positionDuringDelete = -1L;
        fixture.RustHelper.DeleteHandler = (_, _) =>
        {
            positionDuringDelete = fixture.State.GetLogPosition("alpha");
            // Written beside the file it replaces and moved into place after the delete, so the new
            // file cannot be handed the deleted file's inode.
            var replacement = Path.Combine(fixture.AlphaLogPath, "replacement.tmp");
            if (deleteCase.ReplacementLines > 0)
            {
                File.WriteAllText(replacement, string.Concat(Enumerable.Repeat("x\n", deleteCase.ReplacementLines)));
            }
            foreach (var file in deleteCase.Deleted)
            {
                File.Delete(Path.Combine(fixture.AlphaLogPath, file));
            }
            foreach (var (from, to) in deleteCase.Renames ?? Array.Empty<(string From, string To)>())
            {
                File.Move(Path.Combine(fixture.AlphaLogPath, from), Path.Combine(fixture.AlphaLogPath, to));
            }
            if (deleteCase.ReplacementLines > 0)
            {
                File.Move(replacement, Path.Combine(fixture.AlphaLogPath, "access.log"));
            }
            if (deleteCase.Fails)
            {
                throw new IOException("Injected unlink failure.");
            }
            return Task.FromResult(new LogFileDeletionResult(0));
        };

        if (deleteCase.Fails)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Controller.DeleteLogFileAsync("alpha"));
        }
        else
        {
            Assert.IsType<OkObjectResult>(await fixture.Controller.DeleteLogFileAsync("alpha"));
        }

        // Nothing is written before the delete, so a kill before its first unlink leaves the saved position.
        Assert.Equal(deleteCase.LegacySaved + deleteCase.Saved.Values.Sum(), positionDuringDelete);
        var positions = fixture.State.GetLogSourcePositions("alpha");
        foreach (var (stem, expected) in deleteCase.Expected)
        {
            Assert.Equal(expected, positions.GetValueOrDefault(stem));
        }
        Assert.Equal(deleteCase.Expected.Values.Sum(), fixture.State.GetLogPosition("alpha"));
        Assert.Equal(deleteCase.ExpectedTotal, fixture.State.GetLogTotalLines("alpha"));
        Assert.False(File.Exists(countsPath));
        Assert.Equal(
            Path.Combine(fixture.OperationsPath, "rust_resume_alpha.json"),
            Assert.Single(fixture.RustHelper.CountResumePaths));
        var push = Assert.Single(fixture.Notifications.Invocations);
        Assert.Equal(nameof(ISignalRNotificationService.NotifyAllAsync), push.Method);
        Assert.Equal(SignalREvents.ServiceCountsChanged, push.Args[0]);
    }

    [Fact]
    public async Task DeleteLogFile_AFailedPositionUpdateStillReopensNginxAsync()
    {
        ReopenWaitingNginx? nginx = null;
        using var fixture = new ControllerFixture(paths => nginx = new ReopenWaitingNginx(paths));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AlphaLogPath, "access.log.1"),
            string.Concat(Enumerable.Repeat("x\n", 4)));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AlphaLogPath, "access.log"),
            string.Concat(Enumerable.Repeat("x\n", 3)));
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { ["access.log"] = 7 });
        var state = (WriteFailingStateService)fixture.State;
        fixture.RustHelper.DeleteHandler = (path, _) =>
        {
            File.Delete(path);
            state.FailWrites = true;
            return Task.FromResult(new LogFileDeletionResult(0));
        };

        var delete = fixture.Controller.DeleteLogFileAsync("alpha");
        await nginx!.ReopenReached.Task.WaitAsync(_wait);
        nginx.ReleaseReopen.SetResult();

        await Assert.ThrowsAnyAsync<Exception>(() => delete);
        state.FailWrites = false;
    }

    [Fact]
    public async Task DeleteLogFile_ACountThatFailsDeletesNothingAsync()
    {
        using var fixture = new ControllerFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AlphaLogPath, "access.log.1"),
            string.Concat(Enumerable.Repeat("x\n", 4)));
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, string.Concat(Enumerable.Repeat("x\n", 3)));
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { ["access.log"] = 7 });
        fixture.State.SetLogTotalLines("alpha", 7);
        fixture.RustHelper.CountHandler = (_, _) =>
            throw new InvalidOperationException("Injected count failure.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.DeleteLogFileAsync("alpha"));

        Assert.Empty(fixture.RustHelper.DeleteRequests);
        Assert.True(File.Exists(logPath));
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(7, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_ARotationDuringTheCountDeletesNothingAsync()
    {
        using var fixture = new ControllerFixture();
        var rotated = Path.Combine(fixture.AlphaLogPath, "access.log.1");
        var current = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(rotated, string.Concat(Enumerable.Repeat("x\n", 4)));
        await File.WriteAllTextAsync(current, string.Concat(Enumerable.Repeat("x\n", 3)));
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { ["access.log"] = 7 });
        fixture.RustHelper.CountHandler = (_, _) =>
        {
            // logrotate runs during the count: the chosen file moves aside and nginx opens a new one.
            File.Move(rotated, Path.Combine(fixture.AlphaLogPath, "access.log.2"));
            File.Move(current, rotated);
            File.WriteAllText(current, "x\n");
            return Task.FromResult(new LogLineCountResult(0, 0, new Dictionary<string, long>()));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Controller.DeleteLogFileAsync("alpha"));

        Assert.Empty(fixture.RustHelper.DeleteRequests);
        Assert.True(File.Exists(current));
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_MissingFileReturnsNotFoundWithoutLaunchingRustAsync()
    {
        using var fixture = new ControllerFixture();

        var result = await fixture.Controller.DeleteLogFileAsync("alpha");

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(fixture.RustHelper.DeleteRequests);
    }

    [Fact]
    public async Task FreshInstallSeed_ALogDeleteDuringTheCountIsNotUndoneAsync()
    {
        using var fixture = new ControllerFixture();
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, "keep");
        var countStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RustHelper.CountHandler = async (_, _) =>
        {
            if (!countStarted.TrySetResult())
            {
                return new LogLineCountResult(0, 0, new Dictionary<string, long>());
            }
            await releaseCount.Task;
            return new LogLineCountResult(8, 1, new Dictionary<string, long> { ["access.log"] = 8 });
        };
        fixture.RustHelper.DeleteHandler = (path, _) =>
        {
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            return Task.FromResult(new LogFileDeletionResult(bytes));
        };
        var monitor = new LiveLogMonitorService(
            NullLogger<LiveLogMonitorService>.Instance,
            new ConfigurationBuilder().Build(),
            fixture.Processor,
            fixture.State,
            fixture.Datasources,
            fixture.Checker,
            fixture.RustHelper,
            fixture.RepairOwner);
        var seed = (Task)typeof(LiveLogMonitorService)
            .GetMethod("OnStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, new object[] { CancellationToken.None })!;
        await countStarted.Task.WaitAsync(_wait);

        var delete = fixture.Controller.DeleteLogFileAsync("alpha");
        await Task.WhenAny(delete, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.False(delete.IsCompleted);

        releaseCount.SetResult();
        await seed.WaitAsync(_wait);
        Assert.IsType<OkObjectResult>(await delete.WaitAsync(_wait));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public async Task FreshInstallSeed_APassThatSavedFirstKeepsItsPositionsAsync()
    {
        using var fixture = new ControllerFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AlphaLogPath, "access.log"),
            string.Concat(Enumerable.Repeat("x\n", 3)));
        await using var pass = await fixture.RepairOwner.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        fixture.RustHelper.CountHandler = (_, _) =>
            Task.FromResult(new LogLineCountResult(8, 1, new Dictionary<string, long> { ["access.log"] = 8 }));
        var monitor = new LiveLogMonitorService(
            NullLogger<LiveLogMonitorService>.Instance,
            new ConfigurationBuilder().Build(),
            fixture.Processor,
            fixture.State,
            fixture.Datasources,
            fixture.Checker,
            fixture.RustHelper,
            fixture.RepairOwner);
        var seed = (Task)typeof(LiveLogMonitorService)
            .GetMethod("OnStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, new object[] { CancellationToken.None })!;
        var stepWaiters = typeof(OperationStateService)
            .GetField("_logStepWaiters", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow + _wait;
        while ((int)stepWaiters.GetValue(fixture.RepairOwner)! != 1)
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(50);
        }

        // The pass that holds the logs saves its end positions while the seed waits for them.
        fixture.State.SetLogSourcePositions("alpha", new Dictionary<string, long> { ["access.log"] = 3 });
        await pass.DisposeAsync();
        await seed.WaitAsync(_wait);

        Assert.Equal(
            new Dictionary<string, long> { ["access.log"] = 3 },
            fixture.State.GetLogSourcePositions("alpha"));
        Assert.DoesNotContain(fixture.AlphaLogPath, fixture.RustHelper.CountRequests);
    }

    [Fact]
    public async Task FreshInstallSeed_ARestartSeedsTheDatasourcesLeftAsync()
    {
        using var fixture = new ControllerFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AlphaLogPath, "access.log"),
            string.Concat(Enumerable.Repeat("x\n", 3)));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.BetaLogPath, "access.log"),
            string.Concat(Enumerable.Repeat("x\n", 5)));
        var betaCounts = 0;
        fixture.RustHelper.CountHandler = (path, _) =>
        {
            if (path == fixture.AlphaLogPath)
            {
                return Task.FromResult(new LogLineCountResult(3, 1, new Dictionary<string, long> { ["access.log"] = 3 }));
            }
            if (betaCounts++ == 0)
            {
                // A pass elsewhere finishes, then the app stops.
                fixture.State.SetHasProcessedLogs(true);
                throw new OperationCanceledException();
            }
            return Task.FromResult(new LogLineCountResult(5, 1, new Dictionary<string, long> { ["access.log"] = 5 }));
        };
        Task StartMonitor() => (Task)typeof(LiveLogMonitorService)
            .GetMethod("OnStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                new LiveLogMonitorService(
                    NullLogger<LiveLogMonitorService>.Instance,
                    new ConfigurationBuilder().Build(),
                    fixture.Processor,
                    fixture.State,
                    fixture.Datasources,
                    fixture.Checker,
                    fixture.RustHelper,
                    fixture.RepairOwner),
                new object[] { CancellationToken.None })!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(StartMonitor);
        await StartMonitor().WaitAsync(_wait);

        Assert.Equal(
            new Dictionary<string, long> { ["access.log"] = 5 },
            fixture.State.GetLogSourcePositions("beta"));
    }

    [Fact]
    public async Task FreshInstallSeed_AResetToBeginningDuringSeedingSticksAsync()
    {
        using var fixture = new ControllerFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AlphaLogPath, "access.log"),
            string.Concat(Enumerable.Repeat("x\n", 3)));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.BetaLogPath, "access.log"),
            string.Concat(Enumerable.Repeat("x\n", 5)));
        var alphaCounting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RustHelper.CountHandler = async (path, _) =>
        {
            if (path == fixture.AlphaLogPath)
            {
                alphaCounting.SetResult();
                await release.Task;
                return new LogLineCountResult(3, 1, new Dictionary<string, long> { ["access.log"] = 3 });
            }
            return new LogLineCountResult(5, 1, new Dictionary<string, long> { ["access.log"] = 5 });
        };
        var monitor = new LiveLogMonitorService(
            NullLogger<LiveLogMonitorService>.Instance,
            new ConfigurationBuilder().Build(),
            fixture.Processor,
            fixture.State,
            fixture.Datasources,
            fixture.Checker,
            fixture.RustHelper,
            fixture.RepairOwner);
        var seed = (Task)typeof(LiveLogMonitorService)
            .GetMethod("OnStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, new object[] { CancellationToken.None })!;
        await alphaCounting.Task.WaitAsync(_wait);

        fixture.Processor.ResetLogPosition("beta");
        release.SetResult();
        await seed.WaitAsync(_wait);

        Assert.Empty(fixture.State.GetLogSourcePositions("beta"));
        Assert.DoesNotContain(fixture.BetaLogPath, fixture.RustHelper.CountRequests);
    }

    public sealed record SeedCase(string Name, string[] Steps, bool BetaSeeded);

    public static TheoryData<SeedCase> SeedCases => new()
    {
        new SeedCase("a fresh install seeds a datasource with logs", ["logs", "start"], true),
        new SeedCase("a stop during the seed count seeds the datasource at the next start", ["logs", "start-stopped", "start"], true),
        new SeedCase("an empty log folder at the first start, then a LogEntries reset", ["start", "logs", "pass", "db-reset", "start"], false),
        new SeedCase("a missing log folder at the first start, then a LogEntries reset", ["no-folder", "start", "logs", "pass", "db-reset", "start"], false),
        new SeedCase("a failed count at the first start, then a LogEntries reset", ["logs", "start-count-fails", "pass", "db-reset", "start"], false),
        new SeedCase("positions saved before the first start, then a LogEntries reset", ["logs", "partial", "start", "db-reset", "start"], false),
        new SeedCase("a restart before any pass, then a reset to the beginning and a restart", ["logs", "start", "start", "reset", "start"], false),
        new SeedCase("a restart before any pass, a pass, then a per-table LogEntries reset", ["logs", "start", "start", "pass", "db-reset-table", "start"], false),
        new SeedCase("a stop during the seed count, then a LogEntries reset", ["logs", "start-stopped", "db-reset", "start"], false),
    };

    [Theory]
    [MemberData(nameof(SeedCases))]
    public async Task FreshInstallSeed_SettlesEachDatasourceOnceAsync(SeedCase seedCase)
    {
        using var fixture = new ControllerFixture();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AlphaLogPath, "access.log"),
            string.Concat(Enumerable.Repeat("x\n", 3)));
        Exception? betaCountFailure = null;
        fixture.RustHelper.CountHandler = (path, _) =>
        {
            if (path == fixture.BetaLogPath && betaCountFailure is { } failure)
            {
                betaCountFailure = null;
                throw failure;
            }
            var perStem = new Dictionary<string, long>();
            foreach (var file in Directory.GetFiles(path))
            {
                if (LogSourceLayout.LogicalStem(Path.GetFileName(file)) is { } stem)
                {
                    perStem[stem] = perStem.GetValueOrDefault(stem) + File.ReadAllText(file).Count(c => c == '\n');
                }
            }
            return Task.FromResult(new LogLineCountResult(perStem.Values.Sum(), perStem.Count, perStem));
        };
        Task StartMonitor() => (Task)typeof(LiveLogMonitorService)
            .GetMethod("OnStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                new LiveLogMonitorService(
                    NullLogger<LiveLogMonitorService>.Instance,
                    new ConfigurationBuilder().Build(),
                    fixture.Processor,
                    fixture.State,
                    fixture.Datasources,
                    fixture.Checker,
                    fixture.RustHelper,
                    fixture.RepairOwner),
                new object[] { CancellationToken.None })!;

        var countsBeforeLastStart = 0;
        foreach (var step in seedCase.Steps)
        {
            switch (step)
            {
                case "logs":
                    Directory.CreateDirectory(fixture.BetaLogPath);
                    await File.WriteAllTextAsync(
                        Path.Combine(fixture.BetaLogPath, "access.log"),
                        string.Concat(Enumerable.Repeat("x\n", 5)));
                    break;
                case "no-folder":
                    Directory.Delete(fixture.BetaLogPath, recursive: true);
                    break;
                case "start":
                    countsBeforeLastStart = fixture.RustHelper.CountRequests.Count;
                    await StartMonitor().WaitAsync(_wait);
                    break;
                case "start-count-fails":
                    betaCountFailure = new InvalidOperationException("Injected count failure.");
                    await StartMonitor().WaitAsync(_wait);
                    break;
                case "start-stopped":
                    // The app stops while it counts beta's logs.
                    betaCountFailure = new OperationCanceledException();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(StartMonitor);
                    break;
                case "pass":
                    fixture.State.RecordLogIngestPass(
                        "beta",
                        new Dictionary<string, long> { ["access.log"] = 5 },
                        5,
                        diagnostics: null);
                    break;
                case "partial":
                    // A pass that saved part of its work before it ended.
                    fixture.State.SetLogSourcePositions("beta", new Dictionary<string, long> { ["access.log"] = 2 });
                    break;
                case "reset":
                    fixture.Processor.ResetLogPosition("beta");
                    break;
                case "db-reset":
                    fixture.State.ClearLogProcessingPositions();
                    break;
                case "db-reset-table":
                    // The per-table LogEntries reset's position writes.
                    foreach (var name in new[] { "alpha", "beta" })
                    {
                        fixture.State.SetLogSourcePositions(name, new Dictionary<string, long>());
                        fixture.State.SetLogPosition(name, 0);
                        fixture.State.SetLogTotalLines(name, 0);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(seedCase), step, "Unknown seed step");
            }
        }

        var countedAtLastStart = fixture.RustHelper.CountRequests.Skip(countsBeforeLastStart).ToList();
        if (seedCase.BetaSeeded)
        {
            Assert.Equal(
                new Dictionary<string, long> { ["access.log"] = 5 },
                fixture.State.GetLogSourcePositions("beta"));
            Assert.Contains(fixture.BetaLogPath, countedAtLastStart);
        }
        else
        {
            Assert.Empty(fixture.State.GetLogSourcePositions("beta"));
            Assert.DoesNotContain(fixture.BetaLogPath, countedAtLastStart);
        }
    }

    [Fact]
    public void FreshInstallSeed_TheListedMarkerSurvivesASave()
    {
        var listed = new AppState();
        listed.LogProcessing.SeedPendingDatasources = new HashSet<string>();

        Assert.NotNull(JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(listed))!
            .LogProcessing.SeedPendingDatasources);
        Assert.Null(JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(new AppState()))!
            .LogProcessing.SeedPendingDatasources);
    }

    private static Task<LogFileLock> HoldStepAsync(ControllerFixture fixture) =>
        fixture.RepairOwner.LockLogFilesAsync(
            null,
            OperationType.GameRemoval,
            LogFileLockKind.Rewrite,
            CancellationToken.None);

    private static void SetRunFlag(RustLogProcessorService processor, bool value)
    {
        typeof(RustLogProcessorService)
            .GetProperty(nameof(RustLogProcessorService.IsProcessing))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(processor, new object[] { value });
    }

    private sealed class ControllerFixture : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _services;
        private readonly OperationStateService _repairOwner;

        public ControllerFixture(Func<IPathResolver, NginxLogRotationService>? nginx = null)
        {
            _root = Path.Combine(Path.GetTempPath(), $"logs-controller-rust-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            AlphaLogPath = Path.Combine(_root, "alpha-logs");
            BetaLogPath = Path.Combine(_root, "beta-logs");
            Directory.CreateDirectory(AlphaLogPath);
            Directory.CreateDirectory(BetaLogPath);
            Directory.CreateDirectory(Path.Combine(_root, "alpha-cache"));
            Directory.CreateDirectory(Path.Combine(_root, "beta-cache"));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "alpha",
                    ["LanCache:DataSources:0:CachePath"] = Path.Combine(_root, "alpha-cache"),
                    ["LanCache:DataSources:0:LogPath"] = AlphaLogPath,
                    ["LanCache:DataSources:0:Enabled"] = "true",
                    ["LanCache:DataSources:1:Name"] = "beta",
                    ["LanCache:DataSources:1:CachePath"] = Path.Combine(_root, "beta-cache"),
                    ["LanCache:DataSources:1:LogPath"] = BetaLogPath,
                    ["LanCache:DataSources:1:Enabled"] = "true",
                    ["NginxLogRotation:Enabled"] = "false"
                })
                .Build();

            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = _root;
            OperationsPath = pathResolver.GetOperationsDirectory();
            Directory.CreateDirectory(OperationsPath);

            Datasources = new DatasourceService(
                configuration,
                pathResolver,
                NullLogger<DatasourceService>.Instance);
            State = CreateStateService(_root, configuration, pathResolver);
            RustHelper = new FakeRustProcessHelper(pathResolver);

            Tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            OperationStateService? repairOwner = null;
            RustLogProcessorService? processor = null;
            _services = new ServiceCollection()
                .AddSingleton(_ => repairOwner!)
                .AddSingleton(_ => processor!)
                .BuildServiceProvider();
            _repairOwner = repairOwner = new OperationStateService(
                NullLogger<OperationStateService>.Instance,
                configuration,
                State,
                _services.GetRequiredService<IServiceScopeFactory>(),
                DispatchProxy.Create<IHostApplicationLifetime, NullReturningProxy>(),
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                Tracker);
            _repairOwner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Checker = new OperationConflictChecker(
                Tracker,
                _repairOwner,
                NullLogger<OperationConflictChecker>.Instance);

            Processor = processor = new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                pathResolver,
                notifications: null!,
                State,
                _services,
                RustHelper,
                Datasources,
                Tracker);
            var nginxRotation = nginx?.Invoke(pathResolver) ?? new NginxLogRotationService(
                NullLogger<NginxLogRotationService>.Instance,
                configuration,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
            Notifications = (RecordingNotificationProxy)(object)notifications;

            Controller = new LogsController(
                Processor,
                rustLogRemovalService: null!,
                NullLogger<LogsController>.Instance,
                pathResolver,
                RustHelper,
                Datasources,
                State,
                nginxRotation,
                Checker,
                operationQueue: null!,
                _repairOwner,
                new CacheManagementService(
                    configuration,
                    NullLogger<CacheManagementService>.Instance,
                    pathResolver,
                    RustHelper,
                    nginxRotation,
                    Datasources,
                    State,
                    dbContextFactory: null!,
                    gameCacheDetectionService: null!,
                    Tracker,
                    notifications,
                    envFileReader: null!,
                    Checker,
                    new DatasourceCapabilityService(Datasources),
                    CacheScanGateHarness.Idle(),
                    _repairOwner));
        }

        public string AlphaLogPath { get; }
        public string BetaLogPath { get; }
        public string OperationsPath { get; }
        public DatasourceService Datasources { get; }
        public StateService State { get; }
        public FakeRustProcessHelper RustHelper { get; }
        public RustLogProcessorService Processor { get; }
        public UnifiedOperationTracker Tracker { get; }
        public OperationConflictChecker Checker { get; }
        public RecordingNotificationProxy Notifications { get; }
        public LogsController Controller { get; }
        public OperationStateService RepairOwner => _repairOwner;

        public string ResumePath(string datasourceName) =>
            Path.Combine(OperationsPath, $"rust_resume_{datasourceName}.json");

        public void WriteResume(string datasourceName, string contents) =>
            File.WriteAllText(ResumePath(datasourceName), contents);

        public void Dispose()
        {
            foreach (var operation in Tracker.GetActiveOperations())
            {
                Tracker.CompleteOperation(operation.Id, success: false, error: "Disposed test fixture");
            }
            _repairOwner.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        private static WriteFailingStateService CreateStateService(
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
            var state = new WriteFailingStateService(
                NullLogger<StateService>.Instance,
                pathResolver,
                encryption,
                steamAuthStorage);

            var cachedState = typeof(StateService).GetField(
                "_cachedState",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            cachedState.SetValue(state, new AppState { SetupCompleted = true });
            return state;
        }
    }

    private sealed class WriteFailingStateService : StateService
    {
        public WriteFailingStateService(
            ILogger<StateService> logger,
            IPathResolver pathResolver,
            SecureStateEncryptionService encryption,
            SteamAuthStorageService steamAuthStorage)
            : base(logger, pathResolver, encryption, steamAuthStorage)
        {
        }

        public bool FailWrites { get; set; }

        protected override void WriteState(string contents)
        {
            if (FailWrites)
            {
                throw new IOException("Injected state write failure.");
            }

            base.WriteState(contents);
        }
    }

    private sealed class FakeRustProcessHelper : RustProcessHelper
    {
        public FakeRustProcessHelper(IPathResolver pathResolver)
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver,
                operationTracker: null!)
        {
        }

        public List<string> CountRequests { get; } = new();
        public List<string?> CountResumePaths { get; } = new();
        public List<string> DeleteRequests { get; } = new();

        public Func<string, CancellationToken, Task<LogLineCountResult>> CountHandler { get; set; } =
            (_, _) => Task.FromResult(new LogLineCountResult(0, 0, new Dictionary<string, long>()));

        public Func<string, CancellationToken, Task<LogFileDeletionResult>> DeleteHandler { get; set; } =
            (_, _) => Task.FromResult(new LogFileDeletionResult(0));

        public override Task<LogLineCountResult> CountLogLinesAsync(
            string logsPath,
            CancellationToken cancellationToken = default,
            string? resumePath = null)
        {
            CountRequests.Add(logsPath);
            CountResumePaths.Add(resumePath);
            return CountHandler(logsPath, cancellationToken);
        }

        public override Task<LogFileDeletionResult> DeleteLogFileAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            DeleteRequests.Add(filePath);
            return DeleteHandler(filePath, cancellationToken);
        }
    }

    /// <summary>
    /// One host nginx writer over the test logs. Its reopen completes <see cref="ReopenReached"/>
    /// and then waits for <see cref="ReleaseReopen"/>.
    /// </summary>
    private sealed class ReopenWaitingNginx : NginxLogRotationService
    {
        public ReopenWaitingNginx(IPathResolver pathResolver)
            : base(
                NullLogger<NginxLogRotationService>.Instance,
                new ConfigurationBuilder().Build(),
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver)
        {
        }

        public TaskCompletionSource ReopenReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReopen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool CanProbeHostWriters => true;

        protected override async Task<ProcessCommandResult> RunProcessAsync(
            ProcessStartInfo start,
            string label,
            CancellationToken cancellationToken = default)
        {
            switch (label)
            {
                case "host nginx writer identity":
                    return new ProcessCommandResult { ExitCode = 0, Output = "4242|waiting\n" };
                case "host nginx verified reopen":
                    ReopenReached.SetResult();
                    await ReleaseReopen.Task.WaitAsync(cancellationToken);
                    return new ProcessCommandResult { ExitCode = 0 };
                default:
                    throw new InvalidOperationException($"Unexpected nginx command: {label}");
            }
        }
    }

}
