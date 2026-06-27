using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Resolves whether a service is ready to run a scheduled prefill, or whether it needs a login first.
/// </summary>
public interface IScheduledPrefillAuthService
{
    Task<ScheduledPrefillAuthPlan> EnsureAuthenticatedAsync(
        PrefillPlatform service,
        ScheduledPrefillAuthContext context,
        CancellationToken ct);
}

public enum ScheduledPrefillAuthState
{
    Ready,
    NeedsLogin
}

public sealed class ScheduledPrefillAuthPlan
{
    public required PrefillPlatform Service { get; init; }
    public required ScheduledPrefillAuthState State { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public string? NeedsLoginReason { get; init; }
    public Func<DaemonSession, CancellationToken, Task>? AfterSessionCreatedAsync { get; init; }
    public Func<CancellationToken, Task>? CleanupAsync { get; init; }
}

public sealed class ScheduledPrefillAuthContext
{
    public required PrefillPlatform Service { get; init; }
    public required string UserId { get; init; }
}
