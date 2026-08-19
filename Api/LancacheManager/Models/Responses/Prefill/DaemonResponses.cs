namespace LancacheManager.Models;

/// <summary>
/// Daemon service status shown on a platform's management card: container availability plus how many
/// sessions are open and how many of those are authenticated. Built once in
/// <see cref="Controllers.Base.DaemonControllerBase{TService}"/> and routed only by the platforms whose
/// card polls it over REST rather than the prefill hub (see that base class for why).
/// </summary>
public class DaemonStatusResponse
{
    public bool DockerAvailable { get; set; }
    public int ActiveSessions { get; set; }
    public int AuthenticatedSessions { get; set; }
    public int MaxSessionsPerUser { get; set; }
    public int SessionTimeoutMinutes { get; set; }
}

/// <summary>
/// Confirmation that a session's prefill selection was stored, with the number of apps it now holds.
/// The count is echoed back so the picker can show what was saved without re-reading the selection.
/// </summary>
public class SelectedAppsResponse
{
    public string Message { get; set; } = string.Empty;
    public int Count { get; set; }
}
