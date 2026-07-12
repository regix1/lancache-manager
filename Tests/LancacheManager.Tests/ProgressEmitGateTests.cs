using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Tests;

public class ProgressEmitGateTests
{
    [Fact]
    public void StageChangesEmitImmediatelyAndDuplicatesDoNot()
    {
        var gate = new ProgressEmitGate(250);

        Assert.True(gate.ShouldEmit("stage.one", 1, 1_000));
        Assert.False(gate.ShouldEmit("stage.one", 1, 2_000));
        Assert.True(gate.ShouldEmit("stage.two", 2, 1_001));
    }

    [Fact]
    public void PendingLatestRevisionEmitsAtTimeBoundary()
    {
        var gate = new ProgressEmitGate(250);

        Assert.True(gate.ShouldEmit("stage", 1, 10_000));
        Assert.False(gate.ShouldEmit("stage", 2, 10_249));
        Assert.True(gate.ShouldEmit("stage", 2, 10_250));
        Assert.False(gate.ShouldEmit("stage", 2, 10_500));
    }

    [Fact]
    public void TickWrapUsesMonotonicUnsignedElapsedTime()
    {
        var gate = new ProgressEmitGate(250);
        var beforeWrap = long.MaxValue - 100;
        var afterWrap = long.MinValue + 200;

        Assert.True(gate.ShouldEmit("stage", 1, beforeWrap));
        Assert.True(gate.ShouldEmit("stage", 2, afterWrap));
    }

    [Fact]
    public void ConcurrentCallersEmitOneCopyOfARevision()
    {
        var gate = new ProgressEmitGate(0);
        var emitted = 0;

        Parallel.For(0, 64, _ =>
        {
            if (gate.ShouldEmit("stage", 7, 1_000))
            {
                Interlocked.Increment(ref emitted);
            }
        });

        Assert.Equal(1, emitted);
    }
}
