namespace LancacheManager.Core.Services;

internal sealed class PersistentPrefillChangeLease : IAsyncDisposable
{
    private SemaphoreSlim? _gate;

    internal PersistentPrefillChangeLease(SemaphoreSlim gate)
    {
        _gate = gate;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _gate, null)?.Release();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Serializes changes to one daemon's persistent session, so a selection, prefill, sign-in, image
/// update or expiry check never interleaves with another change to the same container.
/// </summary>
internal sealed class PersistentPrefillChangeGate
{
    private readonly SemaphoreSlim _changeGate = new(1, 1);

    public async ValueTask<PersistentPrefillChangeLease> EnterChangeAsync(
        CancellationToken cancellationToken)
    {
        await _changeGate.WaitAsync(cancellationToken);
        return new PersistentPrefillChangeLease(_changeGate);
    }

    public bool TryEnterChange(out PersistentPrefillChangeLease? lease)
    {
        if (!_changeGate.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = new PersistentPrefillChangeLease(_changeGate);
        return true;
    }
}
