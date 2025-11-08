namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// Represents a chunk location in the Blizzard archive system.
/// This is the key for mapping: (which archive, at what byte offset)
/// </summary>
public readonly struct ChunkLocation : IEquatable<ChunkLocation>
{
    public readonly int ArchiveIndex;
    public readonly uint ByteOffset;

    public ChunkLocation(int archiveIndex, uint byteOffset)
    {
        ArchiveIndex = archiveIndex;
        ByteOffset = byteOffset;
    }

    public bool Equals(ChunkLocation other)
    {
        return ArchiveIndex == other.ArchiveIndex && ByteOffset == other.ByteOffset;
    }

    public override bool Equals(object? obj)
    {
        return obj is ChunkLocation other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ArchiveIndex, ByteOffset);
    }

    public override string ToString()
    {
        return $"Archive[{ArchiveIndex}] @ offset {ByteOffset}";
    }

    public static bool operator ==(ChunkLocation left, ChunkLocation right) => left.Equals(right);
    public static bool operator !=(ChunkLocation left, ChunkLocation right) => !left.Equals(right);
}
