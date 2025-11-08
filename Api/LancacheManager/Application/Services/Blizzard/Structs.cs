using System.Collections;

namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// Build configuration file structure
/// </summary>
public class BuildConfigFile
{
    public MD5Hash[] download = Array.Empty<MD5Hash>();
    public MD5Hash[] install = Array.Empty<MD5Hash>();
    public MD5Hash[] encoding = Array.Empty<MD5Hash>();
    public int[] encodingSize = Array.Empty<int>();
    public int[] installSize = Array.Empty<int>();
    public string buildName = string.Empty;
}

/// <summary>
/// CDN configuration file structure
/// </summary>
public class CDNConfigFile
{
    public List<Archive> archives = new List<Archive>();
    public MD5Hash fileIndex;
}

public struct Archive
{
    public string hashId;
    public MD5Hash hashIdMd5;
}

/// <summary>
/// Versions entry
/// </summary>
public struct VersionsEntry
{
    public string buildConfig;
    public string cdnConfig;
    public string productConfig;
}

/// <summary>
/// Install manifest structure
/// </summary>
public class InstallFile
{
    public byte hashSize;
    public ushort numTags;
    public uint numEntries;
    public InstallTagEntry[] tags = Array.Empty<InstallTagEntry>();
    public InstallFileEntry[] entries = Array.Empty<InstallFileEntry>();
}

public struct InstallTagEntry
{
    public string name;
    public ushort type;
    public BitArray files;
}

public struct InstallFileEntry
{
    public string name;
    public MD5Hash contentHash;
    public uint size;
    public List<string> tags;

    public override string ToString()
    {
        return $"{name} size: {size}";
    }
}

/// <summary>
/// Encoding table structure
/// </summary>
public class EncodingFile
{
    public uint numEntriesA;
    public Dictionary<MD5Hash, MD5Hash> ReversedEncodingDictionary = new Dictionary<MD5Hash, MD5Hash>();
}
