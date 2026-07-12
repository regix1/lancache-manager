using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Tests;

public class RustProcessHelperStderrTests
{
    [Fact]
    public async Task CallbackObservesLineBeforePumpCompletes()
    {
        var reader = new PausingLineReader("milestone");
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = RustProcessHelper.PumpStderrLinesAsync(
            reader,
            line => observed.TrySetResult(),
            maxRetainedChars: 1024);

        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pump.IsCompleted);

        reader.ReleaseEof();
        Assert.Contains("milestone", await pump.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OverflowRetainsBoundedTailAndMarker()
    {
        var stderr = string.Join(Environment.NewLine, Enumerable.Range(0, 100).Select(i => $"line-{i:D3}"));
        var result = await RustProcessHelper.PumpStderrLinesAsync(
            new StringReader(stderr),
            onStderrLine: null,
            maxRetainedChars: 40);

        Assert.StartsWith(BoundedTextTail.TruncationMarker, result);
        Assert.Contains("line-099", result);
        Assert.DoesNotContain("line-000", result);
    }

    [Fact]
    public async Task CallbackFailureDoesNotStopDrain()
    {
        var failures = 0;
        var result = await RustProcessHelper.PumpStderrLinesAsync(
            new StringReader("one\ntwo\nthree"),
            _ => throw new InvalidOperationException("observer failure"),
            maxRetainedChars: 1024,
            _ => failures++);

        Assert.Equal(3, failures);
        Assert.Contains("three", result);
    }

    private sealed class PausingLineReader(string firstLine) : TextReader
    {
        private readonly TaskCompletionSource<string?> _eof =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public override Task<string?> ReadLineAsync() =>
            Interlocked.Increment(ref _reads) == 1
                ? Task.FromResult<string?>(firstLine)
                : _eof.Task;

        internal void ReleaseEof() => _eof.TrySetResult(null);
    }
}
