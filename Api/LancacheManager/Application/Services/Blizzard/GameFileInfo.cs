namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// Information about a game file that a chunk belongs to.
/// </summary>
public class GameFileInfo
{
    /// <summary>
    /// The file path/name (e.g., "Data/models/character.m2")
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Size of the file in bytes
    /// </summary>
    public uint Size { get; set; }

    /// <summary>
    /// MD5 hash of the file content (EKey)
    /// </summary>
    public MD5Hash ContentHash { get; set; }

    /// <summary>
    /// Archive location where this file is stored
    /// </summary>
    public ChunkLocation Location { get; set; }

    /// <summary>
    /// Tags associated with this file (language, platform, etc.)
    /// </summary>
    public List<string> Tags { get; set; } = new List<string>();

    public override string ToString()
    {
        return $"{FileName} ({Size} bytes) @ {Location}";
    }
}
