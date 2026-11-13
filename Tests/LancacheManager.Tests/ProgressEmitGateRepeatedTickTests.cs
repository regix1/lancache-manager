using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Tests;

/// <summary>
/// A log removal's first child report repeats the starting report the host already sent. When that
/// repeat arrives after the interval has passed, the gate must still send the update it holds later
/// in the run, or the card stays one count behind for every quiet stretch.
/// </summary>
public class ProgressEmitGateRepeatedTickTests
{
    private const long IntervalMs = 250;

    public static TheoryData<long, int> LateRepeats => new()
    {
        { 251, 1 },
        { 300, 1 },
        { 10_000, 1 },
        { 300, 2 }
    };

    [Theory]
    [MemberData(nameof(LateRepeats))]
    public async Task ALateRepeatOfTheSentReportStillLetsTheNextHeldUpdateOutAsync(long sinceSentMs, int repeats)
    {
        var gate = new ProgressEmitGate(IntervalMs);
        var sent = new List<long>();
        var newestSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task> Send(long revision) => () =>
        {
            lock (sent) sent.Add(revision);
            if (revision == 3) newestSent.TrySetResult();
            return Task.CompletedTask;
        };

        // The host's starting report went out live this long ago.
        Assert.True(gate.ShouldEmit("removal.stage", 1, Environment.TickCount64 - sinceSentMs));

        // The child's first report repeats it, then two newer reports follow in quick succession.
        for (var repeat = 0; repeat < repeats; repeat++)
            await gate.EmitAsync("removal.stage", 1, Send(1));
        await gate.EmitAsync("removal.stage", 2, Send(2));
        await gate.EmitAsync("removal.stage", 3, Send(3));

        await Task.WhenAny(newestSent.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        string shown;
        lock (sent) shown = string.Join(",", sent);
        Assert.True(newestSent.Task.IsCompleted,
            $"The newest update (revision 3) was never sent after the quiet stretch; sent revisions: [{shown}]");
    }
}
