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
    public static (Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames, Dictionary<uint, uint> depotOwners, Dictionary<uint, string> depotNames)
        ConvertMappingsDictionaries(
            ConcurrentDictionary<uint, HashSet<uint>> depotToAppMappings,
            ConcurrentDictionary<uint, string> appNames,
            ConcurrentDictionary<uint, uint> depotOwners,
            ConcurrentDictionary<uint, string>? depotNames = null)
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

        var depotNamesDict = new Dictionary<uint, string>();
        if (depotNames != null)
        {
            foreach (var kvp in depotNames)
            {
                depotNamesDict[kvp.Key] = kvp.Value;
            }
        }

        return (depotMappingsDict, appNamesDict, depotOwnersDict, depotNamesDict);
    }
}
