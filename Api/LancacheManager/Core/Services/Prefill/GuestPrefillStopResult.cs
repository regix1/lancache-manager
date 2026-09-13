namespace LancacheManager.Core.Services;

public sealed record GuestPrefillStopResult(int FailedSessions, int PendingStarts)
{
    public bool Success => FailedSessions == 0 && PendingStarts == 0;
}
