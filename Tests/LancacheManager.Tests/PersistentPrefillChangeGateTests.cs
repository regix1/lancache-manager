using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

public sealed class PersistentPrefillChangeGateTests
{
    [Fact]
    public async Task TryEnterChange_ReturnsImmediatelyWhileAnotherChangeOwnsTheGate()
    {
        var gate = new PersistentPrefillChangeGate();
        await using var first = await gate.EnterChangeAsync(CancellationToken.None);

        Assert.False(gate.TryEnterChange(out var denied));
        Assert.Null(denied);

        await first.DisposeAsync();

        Assert.True(gate.TryEnterChange(out var acquired));
        Assert.NotNull(acquired);
        await acquired!.DisposeAsync();
    }
}
