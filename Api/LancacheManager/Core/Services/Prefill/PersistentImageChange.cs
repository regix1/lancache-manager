using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

internal sealed class PersistentImageChange
{
    public required DaemonSession Session { get; init; }
    public required string ContainerId { get; init; }
    public required string ConfiguredImage { get; init; }
    public required string DesiredImageId { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required bool Authenticated { get; init; }
    public required bool NeedsRelogin { get; init; }
    public string? CreatedSessionId { get; set; }
    public string? CreatedContainerId { get; set; }
    public bool ContainerRemoved { get; set; }
    public bool OldSessionRetired { get; set; }
    public bool Cancelled { get; set; }
}
