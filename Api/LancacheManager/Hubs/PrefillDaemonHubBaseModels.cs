using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Hubs;

/// <summary>
/// The answer to a guest's create: the new session, or the translation key and values of the refusal.
/// </summary>
public sealed class CreateSessionResult
{
    public DaemonSessionDto? Session { get; init; }
    public string? StageKey { get; init; }
    public Dictionary<string, object?>? Context { get; init; }
}

/// <summary>
/// The answer to a guest's End session: the translation key and values of the failure, or no key when the
/// session ended.
/// </summary>
public sealed class EndSessionResult
{
    public string? StageKey { get; init; }
    public Dictionary<string, object?>? Context { get; init; }
}
