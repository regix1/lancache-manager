namespace LancacheManager.Models;

/// <summary>
/// Distinguishes the session types that have an account behind them from the one that does not.
/// </summary>
public static class SessionTypeExtensions
{
    /// <summary>
    /// True for a session backed by an account. An admin and a user both hold one; a user changes
    /// only itself, never the installation or other people. A guest holds a session with no account behind it.
    /// </summary>
    public static bool IsAccountHolder(this SessionType sessionType)
        => sessionType is SessionType.Admin or SessionType.User;
}
