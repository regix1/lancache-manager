namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// Represents where a file is located within the archive system.
/// </summary>
public readonly struct ArchiveIndexEntry
{
    /// <summary>
    /// Which archive this file is in (index into the archives array)
    /// </summary>
    public readonly short Index;

    /// <summary>
    /// Byte offset within the archive (typically aligned to 4KB)
    /// </summary>
    public readonly uint Offset;

    /// <summary>
    /// Size of the file in bytes
    /// </summary>
    public readonly uint Size;

    public ArchiveIndexEntry(short index, uint size, uint offset)
    {
        Index = index;
        Offset = offset;
        Size = size;
    }

    public override string ToString()
    {
        return $"Archive[{Index}] offset={Offset} size={Size}";
    }
}
