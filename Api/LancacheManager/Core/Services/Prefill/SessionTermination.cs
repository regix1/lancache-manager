using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

internal sealed class SessionTermination
{
    public required DaemonSession Session { get; init; }
    public required string Reason { get; init; }
    public string? TerminatedBy { get; init; }
    public Task? Work { get; set; }
    public Task? Removal { get; set; }
    public bool Removed { get; set; }
    public bool HistoryClosed { get; set; }
    public bool EntryClosed { get; set; }
    public bool ActivityRetired { get; set; }
    public bool Persisted { get; set; }
    public bool GlobalSent { get; set; }
    public bool OwnerSent { get; set; }
    public HashSet<string> SentConnections { get; } = new();
    public bool Disposed { get; set; }
    public bool Complete { get; set; }
}
