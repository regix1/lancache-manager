namespace LancacheManager.Core.Services.SteamKit2;

/// <summary>
/// Thrown when the Steam connection drops while a session operation is in flight.
/// Transient - a fresh connect picks a different CM server (SteamKit2 marks the old one bad).
/// </summary>
public sealed class SteamConnectionLostException : Exception
{
    public SteamConnectionLostException(string message) : base(message) { }
}

/// <summary>
/// Thrown when a Steam logon attempt is rejected. Message is already user-friendly.
/// ErrorType/StageKey feed the SteamSessionError toast; IsTransient marks CM-side failures
/// (TryAnotherCM/ServiceUnavailable) that a reconnect to a different server can fix.
/// </summary>
public sealed class SteamLogonException : Exception
{
    public string ErrorType { get; }
    public string StageKey { get; }
    public string Result { get; }
    public string ExtendedResult { get; }
    public bool IsTransient { get; }

    public SteamLogonException(
        string message,
        string errorType,
        string stageKey,
        string result,
        string extendedResult,
        bool isTransient) : base(message)
    {
        ErrorType = errorType;
        StageKey = stageKey;
        Result = result;
        ExtendedResult = extendedResult;
        IsTransient = isTransient;
    }
}
