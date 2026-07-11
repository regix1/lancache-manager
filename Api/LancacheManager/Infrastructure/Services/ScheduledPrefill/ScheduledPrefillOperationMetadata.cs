namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Mutable display state for the currently running service inside the aggregate scheduled-prefill
/// operation. The run-status endpoint reads this concurrently with the scheduler advancing between
/// services, so the value uses volatile reads and writes.
/// </summary>
public sealed class ScheduledPrefillOperationMetadata
{
    private int _showNotification;

    public ScheduledPrefillOperationMetadata(bool showNotification)
    {
        _showNotification = showNotification ? 1 : 0;
    }

    /// <summary>
    /// True when the current service should appear in the universal notification bar.
    /// </summary>
    public bool ShowNotification
    {
        get => Volatile.Read(ref _showNotification) == 1;
        set => Volatile.Write(ref _showNotification, value ? 1 : 0);
    }
}
