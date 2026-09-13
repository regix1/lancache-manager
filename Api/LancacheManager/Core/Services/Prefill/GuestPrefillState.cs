using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

internal sealed class GuestPrefillState
{
    public int Stops { get; set; }
    public bool CleanupPending { get; set; }
    public List<GuestPrefillStart> Starts { get; } = new();
}

internal sealed class GuestPrefillStart
{
    public required Guid UserId { get; init; }
    public required PrefillDaemonServiceBase Daemon { get; init; }
    public bool Stopped { get; set; }
    public bool Active { get; set; } = true;
    public bool CreateDispatched { get; set; }
    public bool Removed { get; set; }
    public bool ContainerRemoved { get; set; }
    public string? SessionId { get; set; }
    public string? ContainerName { get; set; }
    public string? ContainerId { get; set; }
    public string? Directory { get; set; }
    public DaemonSession? Session { get; set; }
    public bool Registered { get; set; }
    public TaskCompletionSource Registration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Publication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationTokenSource Cancellation { get; } = new();
    public Task CancellationTask { get; set; } = Task.CompletedTask;
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SemaphoreSlim Cleanup { get; } = new(1, 1);
}
