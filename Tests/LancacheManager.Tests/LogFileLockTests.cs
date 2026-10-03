using System.Threading.Channels;
using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public sealed class LogFileLockTests : IDisposable
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);
    private readonly string _root;

    public LogFileLockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-log-file-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task StepsWaitForTheImportPassAndForEachOtherAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;

        var pass = await LockAsync(owner, LogFileLockKind.Ingest);
        var first = LockAsync(owner, LogFileLockKind.Rows);
        Assert.False(first.IsCompleted);

        await pass.DisposeAsync();
        var firstLock = await first.WaitAsync(_wait);
        var second = LockAsync(owner, LogFileLockKind.Rewrite);
        Assert.False(second.IsCompleted);

        await firstLock.DisposeAsync();
        var secondLock = await second.WaitAsync(_wait);

        // A repeated dispose of the first lock must not free the logs the second step now holds.
        await firstLock.DisposeAsync();
        await owner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
        var third = LockAsync(owner, LogFileLockKind.Rows);
        Assert.False(third.IsCompleted);

        await secondLock.DisposeAsync();
        await using var thirdLock = await third.WaitAsync(_wait);
    }

    [Fact]
    public async Task WaitingStepKeepsTheNextImportPassOutAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        var stepContext = new PumpedContext();
        var passContext = new PumpedContext();

        var pass = await LockAsync(owner, LogFileLockKind.Ingest);
        var step = stepContext.Run(() => LockAsync(owner, LogFileLockKind.Rows));
        var nextPass = passContext.Run(() => LockAsync(owner, LogFileLockKind.Ingest));

        await pass.DisposeAsync();
        await passContext.RunNextAsync();
        Assert.False(nextPass.IsCompleted);

        await stepContext.RunNextAsync();
        var stepLock = await step.WaitAsync(_wait);
        await passContext.RunNextAsync();
        Assert.False(nextPass.IsCompleted);

        await stepLock.DisposeAsync();
        await passContext.RunNextAsync();
        await using var nextLock = await nextPass.WaitAsync(_wait);
    }

    [Fact]
    public async Task ImportThatWaitedThroughAStepGoesBeforeTheNextStepAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        var tracker = harness.Tracker;
        var removalId = tracker.RegisterOperation(
            OperationType.GameRemoval,
            "Game Removal",
            new CancellationTokenSource(),
            blockedByName: "Cache Clearing");
        var batchId = tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            new CancellationTokenSource());
        var scanId = tracker.RegisterOperation(
            OperationType.EvictionScan,
            "Eviction Scan",
            new CancellationTokenSource());
        string? BlockedBy(Guid operationId) => tracker.GetOperation(operationId)!.BlockedByName;
        var scanContext = new PumpedContext();
        var batchContext = new PumpedContext();

        var removal = await owner.LockLogFilesAsync(
            removalId,
            OperationType.GameRemoval,
            LogFileLockKind.Rewrite,
            CancellationToken.None);
        var scan = scanContext.Run(() => owner.LockLogFilesAsync(
            scanId,
            OperationType.EvictionScan,
            LogFileLockKind.Rows,
            CancellationToken.None));
        var batch = batchContext.Run(() => owner.LockLogFilesAsync(
            batchId,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None));

        Assert.Equal("Cache Clearing", BlockedBy(removalId));
        Assert.Equal("Game Removal", BlockedBy(scanId));
        Assert.Equal("Game Removal", BlockedBy(batchId));

        // The scan waited longer, but the import that waited through the removal goes first.
        await removal.DisposeAsync();
        await scanContext.RunNextAsync();
        Assert.False(scan.IsCompleted);
        // The removal has let go and the import has not taken its turn yet, so nothing is named.
        Assert.Null(BlockedBy(scanId));
        await batchContext.RunNextAsync();
        var batchLock = await batch.WaitAsync(_wait);
        Assert.Null(BlockedBy(batchId));

        await scanContext.RunNextAsync();
        Assert.False(scan.IsCompleted);
        Assert.Equal("Log Processing", BlockedBy(scanId));

        await batchLock.DisposeAsync();
        await scanContext.RunNextAsync();
        await using var scanLock = await scan.WaitAsync(_wait);
        Assert.Null(BlockedBy(scanId));
    }

    [Fact]
    public async Task WaitingLineDropsANameWhenTheNextHolderHasNoneAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        var tracker = harness.Tracker;
        var removalId = tracker.RegisterOperation(
            OperationType.GameRemoval,
            "Game Removal",
            new CancellationTokenSource());
        var scanId = tracker.RegisterOperation(
            OperationType.EvictionScan,
            "Eviction Scan",
            new CancellationTokenSource());
        var scanContext = new PumpedContext();
        var unnamedContext = new PumpedContext();

        var removal = await owner.LockLogFilesAsync(
            removalId,
            OperationType.GameRemoval,
            LogFileLockKind.Rewrite,
            CancellationToken.None);
        var scan = scanContext.Run(() => owner.LockLogFilesAsync(
            scanId,
            OperationType.EvictionScan,
            LogFileLockKind.Rows,
            CancellationToken.None));
        var unnamed = unnamedContext.Run(() => LockAsync(owner, LogFileLockKind.Rows));
        Assert.Equal("Game Removal", tracker.GetOperation(scanId)!.BlockedByName);

        // A step with no operation of its own takes the logs before the scan looks again.
        await removal.DisposeAsync();
        await unnamedContext.RunNextAsync();
        var unnamedLock = await unnamed.WaitAsync(_wait);
        await scanContext.RunNextAsync();
        Assert.False(scan.IsCompleted);
        Assert.Null(tracker.GetOperation(scanId)!.BlockedByName);

        await unnamedLock.DisposeAsync();
        await scanContext.RunNextAsync();
        await using var scanLock = await scan.WaitAsync(_wait);
    }

    [Fact]
    public async Task StepCancelledWhileWaitingLetsTheNextImportPassInAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        using var cancel = new CancellationTokenSource();

        var pass = await LockAsync(owner, LogFileLockKind.Ingest);
        var step = LockAsync(owner, LogFileLockKind.Rows, cancel.Token);
        var nextPass = LockAsync(owner, LogFileLockKind.Ingest);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.WaitAsync(_wait));
        await pass.DisposeAsync();
        await using var nextLock = await nextPass.WaitAsync(_wait);
    }

    [Fact]
    public async Task StepCancelledWhileWaitingStopsNamingTheHolderAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        var tracker = harness.Tracker;
        var resetId = tracker.RegisterOperation(
            OperationType.DatabaseReset,
            "Database Reset",
            new CancellationTokenSource());
        var removalId = tracker.RegisterOperation(
            OperationType.LogRemoval,
            "Log Removal",
            new CancellationTokenSource());
        using var cancel = new CancellationTokenSource();

        await using var reset = await owner.LockLogFilesAsync(
            resetId,
            OperationType.DatabaseReset,
            LogFileLockKind.Rows,
            CancellationToken.None);
        var removal = owner.LockLogFilesAsync(
            removalId,
            OperationType.LogRemoval,
            LogFileLockKind.Rewrite,
            cancel.Token);
        Assert.Equal("Database Reset", tracker.GetOperation(removalId)!.BlockedByName);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => removal.WaitAsync(_wait));
        Assert.Null(tracker.GetOperation(removalId)!.BlockedByName);
    }

    [Fact]
    public async Task StepCancelledWhileTheSpeedTrackerRunsGivesTheLogsBackAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        using var cancel = new CancellationTokenSource();

        Assert.True(owner.TryBeginSpeedTrackerRun());
        var step = LockAsync(owner, LogFileLockKind.Rewrite, cancel.Token);
        await owner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
        var pass = LockAsync(owner, LogFileLockKind.Ingest);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.WaitAsync(_wait));
        await using var passLock = await pass.WaitAsync(_wait);
        owner.EndSpeedTrackerRun();
    }

    [Theory]
    [InlineData(LogFileLockKind.Rows)]
    [InlineData(LogFileLockKind.Rewrite)]
    public async Task StepWaitsForTheSpeedTrackerChildToExitAsync(LogFileLockKind kind)
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;

        await using (await LockAsync(owner, LogFileLockKind.Ingest))
        {
            Assert.True(owner.TryBeginSpeedTrackerRun());
        }

        var step = LockAsync(owner, kind);
        Assert.False(step.IsCompleted);
        await owner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
        Assert.False(owner.TryBeginSpeedTrackerRun());

        owner.EndSpeedTrackerRun();
        var stepLock = await step.WaitAsync(_wait);
        Assert.False(owner.TryBeginSpeedTrackerRun());

        await stepLock.DisposeAsync();
        Assert.True(owner.TryBeginSpeedTrackerRun());
        owner.EndSpeedTrackerRun();
    }

    [Fact]
    public async Task AReopenRunsBesideAnImportPassAndTheSpeedTrackerAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;

        var pass = await LockAsync(owner, LogFileLockKind.Ingest);
        Assert.True(owner.TryBeginSpeedTrackerRun());
        var reopen = await LockAsync(owner, LogFileLockKind.Reopen).WaitAsync(_wait);

        // No step holds the logs, so the speed tracker is not asked to stop, and the next import pass
        // starts while the reopen signals.
        await owner.WaitForLogStepAsync(active: false, CancellationToken.None).WaitAsync(_wait);
        await pass.DisposeAsync();
        await using (await LockAsync(owner, LogFileLockKind.Ingest).WaitAsync(_wait))
        {
        }

        await reopen.DisposeAsync();
        owner.EndSpeedTrackerRun();
    }

    [Fact]
    public async Task AReopenWaitsForAStepAndKeepsTheNextStepOutAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;

        var step = await LockAsync(owner, LogFileLockKind.Rows);
        var reopen = LockAsync(owner, LogFileLockKind.Reopen);
        Assert.False(reopen.IsCompleted);

        await step.DisposeAsync();
        var reopenLock = await reopen.WaitAsync(_wait);
        var nextStep = LockAsync(owner, LogFileLockKind.Rewrite);
        Assert.False(nextStep.IsCompleted);

        await reopenLock.DisposeAsync();
        await using var nextLock = await nextStep.WaitAsync(_wait);
    }

    [Fact]
    public async Task AStepWaitingOnlyBehindAReopenDoesNotHoldOffTheImportAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;

        var reopenLock = await LockAsync(owner, LogFileLockKind.Reopen);
        var step = LockAsync(owner, LogFileLockKind.Rewrite);
        Assert.False(step.IsCompleted);

        var liveId = harness.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            new CancellationTokenSource(),
            liveIngest: true);
        var pass = await owner.LockLogFilesAsync(
            liveId,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        await pass.DisposeAsync();
        await reopenLock.DisposeAsync();
        await using var stepLock = await step.WaitAsync(_wait);
    }

    [Fact]
    public async Task AManualPassDoesNotStartAheadOfAWaitingStepWhileAReopenHoldsAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;

        var reopenLock = await LockAsync(owner, LogFileLockKind.Reopen);
        var step = LockAsync(owner, LogFileLockKind.Rewrite);
        Assert.False(step.IsCompleted);

        var manualId = harness.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            new CancellationTokenSource());
        var pass = owner.LockLogFilesAsync(
            manualId,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(pass.IsCompleted);

        await reopenLock.DisposeAsync();
        var stepLock = await step.WaitAsync(_wait);
        Assert.False(pass.IsCompleted);

        await stepLock.DisposeAsync();
        await using var passLock = await pass.WaitAsync(_wait);
    }

    [Fact]
    public async Task WaitForLogStepCompletesOnlyOnTheMatchingChangeAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        var context = new PumpedContext();

        var stepStarted = context.Run(() => owner.WaitForLogStepAsync(active: true, CancellationToken.None));
        var pass = await LockAsync(owner, LogFileLockKind.Ingest);
        await context.RunNextAsync();
        Assert.False(stepStarted.IsCompleted);

        await pass.DisposeAsync();
        await context.RunNextAsync();
        Assert.False(stepStarted.IsCompleted);

        var step = await LockAsync(owner, LogFileLockKind.Rows);
        await context.RunNextAsync();
        Assert.True(stepStarted.IsCompletedSuccessfully);

        var stepEnded = context.Run(() => owner.WaitForLogStepAsync(active: false, CancellationToken.None));
        owner.EndSpeedTrackerRun();
        await context.RunNextAsync();
        Assert.False(stepEnded.IsCompleted);

        await step.DisposeAsync();
        await context.RunNextAsync();
        Assert.True(stepEnded.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StepWaiterParkedBeforeTheImportPassEndsWakesAtTheStepGrantAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        var stepContext = new PumpedContext();
        var waiterContext = new PumpedContext();

        var pass = await LockAsync(owner, LogFileLockKind.Ingest);
        var step = stepContext.Run(() => LockAsync(owner, LogFileLockKind.Rows));
        var stepStarted = waiterContext.Run(() => owner.WaitForLogStepAsync(active: true, CancellationToken.None));

        await pass.DisposeAsync();
        await waiterContext.RunNextAsync();
        Assert.False(stepStarted.IsCompleted);
        await stepContext.RunNextAsync();
        await using var stepLock = await step.WaitAsync(_wait);

        await waiterContext.RunNextAsync();
        Assert.True(stepStarted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StepWaiterParkedBeforeAStepGrantWakesWithNoImportHolderAsync()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = harness.Owner;
        using var cancel = new CancellationTokenSource();
        var passContext = new PumpedContext();
        var stepContext = new PumpedContext();
        var waiterContext = new PumpedContext();

        // The first step's release owes the waiting import the next turn, so the second step
        // registers and waits with no holder at all.
        var first = await LockAsync(owner, LogFileLockKind.Rows);
        var pass = passContext.Run(() => LockAsync(owner, LogFileLockKind.Ingest, cancel.Token));
        await first.DisposeAsync();
        var second = stepContext.Run(() => LockAsync(owner, LogFileLockKind.Rewrite));
        var stepStarted = waiterContext.Run(() => owner.WaitForLogStepAsync(active: true, CancellationToken.None));

        // Cancelling the last waiting import clears the turn it was owed.
        await cancel.CancelAsync();
        await passContext.RunNextAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);

        await waiterContext.RunNextAsync();
        Assert.False(stepStarted.IsCompleted);
        await stepContext.RunNextAsync();
        await using var secondLock = await second.WaitAsync(_wait);

        await waiterContext.RunNextAsync();
        Assert.True(stepStarted.IsCompletedSuccessfully);
    }

    private Task<OperationRepairTests.RepairHarness> CreateHarnessAsync() =>
        OperationRepairTests.RepairHarness.CreateAsync(_root, start: false);

    private static Task<LogFileLock> LockAsync(
        OperationStateService owner,
        LogFileLockKind kind,
        CancellationToken cancellationToken = default) =>
        owner.LockLogFilesAsync(
            null,
            kind == LogFileLockKind.Ingest ? OperationType.LogProcessing : OperationType.GameRemoval,
            kind,
            cancellationToken);

    /// <summary>
    /// Holds every continuation posted to it until the test runs it, so the test decides which
    /// waiter re-checks the lock first.
    /// </summary>
    private sealed class PumpedContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _posted =
            Channel.CreateUnbounded<(SendOrPostCallback Callback, object? State)>();

        public override void Post(SendOrPostCallback d, object? state) => _posted.Writer.TryWrite((d, state));

        public T Run<T>(Func<T> start)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return start();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public async Task RunNextAsync()
        {
            var (callback, state) = await _posted.Reader.ReadAsync().AsTask().WaitAsync(_wait);
            Run(() =>
            {
                callback(state);
                return true;
            });
        }
    }
}
