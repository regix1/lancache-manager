namespace LancacheManager.Core.Services.Xbox;

/// <summary>
/// Thrown when Microsoft refuses an Xbox sign-in attempt (device authenticate, user authenticate, or
/// XSTS authorize). StageKey is the i18n key for the reason (country restriction, child account, age
/// verification, missing profile, or a generic refusal/HTTP failure); Context carries the interpolation
/// values (`code` or `status`) that key expects. Message is the server log line and is also carried to
/// the browser as the `errorDetail` context value; the stage key is what the user actually reads,
/// because this path deliberately sends no `error` and no English `message` on the wire.
/// </summary>
public sealed class XboxLogonException : Exception
{
    public string StageKey { get; }
    public IReadOnlyDictionary<string, object>? Context { get; }

    public XboxLogonException(string message, string stageKey, IReadOnlyDictionary<string, object>? context)
        : base(message)
    {
        StageKey = stageKey;
        Context = context;
    }
}
