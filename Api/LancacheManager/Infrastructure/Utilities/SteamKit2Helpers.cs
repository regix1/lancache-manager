using System.Collections.Concurrent;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Helper utilities for SteamKit2Service
/// </summary>
public static class SteamKit2Helpers
{
    /// <summary>
    /// Converts concurrent dictionaries to regular dictionaries for serialization
    /// </summary>
    public static (Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames, Dictionary<uint, uint> depotOwners)
        ConvertMappingsDictionaries(
            ConcurrentDictionary<uint, HashSet<uint>> depotToAppMappings,
            ConcurrentDictionary<uint, string> appNames,
            ConcurrentDictionary<uint, uint> depotOwners)
    {
        var depotMappingsDict = new Dictionary<uint, HashSet<uint>>();
        foreach (var kvp in depotToAppMappings)
        {
            depotMappingsDict[kvp.Key] = kvp.Value;
        }

        var appNamesDict = new Dictionary<uint, string>();
        foreach (var kvp in appNames)
        {
            appNamesDict[kvp.Key] = kvp.Value;
        }

        var depotOwnersDict = new Dictionary<uint, uint>();
        foreach (var kvp in depotOwners)
        {
            depotOwnersDict[kvp.Key] = kvp.Value;
        }

        return (depotMappingsDict, appNamesDict, depotOwnersDict);
    }

    /// <summary>
    /// Updates connection activity timestamp for keep-alive tracking
    /// </summary>
    public static DateTime UpdateConnectionActivity()
    {
        return DateTime.UtcNow;
    }

    /// <summary>
    /// Calculates time since last activity
    /// </summary>
    public static TimeSpan GetIdleTime(DateTime lastActivity)
    {
        return DateTime.UtcNow - lastActivity;
    }

    /// <summary>
    /// Checks if connection should be closed due to inactivity
    /// </summary>
    public static bool ShouldDisconnectIdle(DateTime lastActivity, int keepAliveSeconds)
    {
        return GetIdleTime(lastActivity).TotalSeconds >= keepAliveSeconds;
    }
}
