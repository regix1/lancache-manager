namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Provides access to container environment configuration (PUID/PGID).
/// Values are read from environment variables set during container startup.
/// </summary>
public static class ContainerEnvironment
{
    private static readonly string _puid = Environment.GetEnvironmentVariable("PUID") ?? "1000";
    private static readonly string _pgid = Environment.GetEnvironmentVariable("PGID") ?? "1000";

    /// <summary>
    /// Returns a formatted UID:GID string for use in error messages.
    /// </summary>
    public static string UidGid => $"{_puid}:{_pgid}";
}
