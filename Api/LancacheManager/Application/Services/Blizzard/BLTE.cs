using LancacheManager.Application.Services.Blizzard.Extensions;
using System.IO.Compression;

namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// BLTE (Blizzard Lossless Text Encoding) decompression.
/// Used to decompress manifest files downloaded from Blizzard CDN.
/// https://wowdev.wiki/BLTE
/// </summary>
public static class BLTE
{
    public static MemoryStream Parse(byte[] content)
    {
        var resultStream = new MemoryStream();
        using var inputStream = new MemoryStream(content);
        using var bin = new BinaryReader(inputStream);

        // Check BLTE magic number
        if (bin.ReadUInt32() != 0x45544c42)  // "BLTE"
        {
            throw new Exception("Not a BLTE file");
        }

        uint blteSize = bin.ReadUInt32BigEndian();
        byte[] bytes = bin.ReadBytes(4);
        int chunkCount = bytes[1] << 16 | bytes[2] << 8 | bytes[3] << 0;

        int supposedHeaderSize = 24 * chunkCount + 12;
        if (supposedHeaderSize != blteSize)
        {
            throw new Exception("Invalid header size!");
        }
        if (supposedHeaderSize > bin.BaseStream.Length)
        {
            throw new Exception("Not enough data");
        }

        var chunkCompressedSizes = new int[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            chunkCompressedSizes[i] = bin.ReadInt32BigEndian();
            // Skip decompressed size
            bin.ReadInt32BigEndian();
            // Skip checksum
            bin.ReadBytes(16);
        }

        foreach (var compressedSize in chunkCompressedSizes)
        {
            if (compressedSize > (bin.BaseStream.Length - bin.BaseStream.Position))
            {
                throw new Exception("Trying to read more than is available!");
            }
            HandleDataBlock(bin, compressedSize, resultStream);
        }

        // Reset the result stream
        resultStream.Seek(0, SeekOrigin.Begin);
        return resultStream;
    }

    private static void HandleDataBlock(BinaryReader bin, int compressedSize, MemoryStream result)
    {
        var chunkType = bin.ReadByte();
        switch (chunkType)
        {
            case 0x4E: // N (no compression)
                bin.BaseStream.CopyStream(result, compressedSize - 1);
                break;
            case 0x5A: // Z (zlib, compressed)
                var buffer = bin.ReadBytes(compressedSize - 1);
                using (var stream = new MemoryStream(buffer, 2, compressedSize - 3))
                using (var ds = new DeflateStream(stream, CompressionMode.Decompress))
                {
                    ds.CopyTo(result);
                }
                break;
            case 0x45: // E (encrypted)
                throw new NotImplementedException("BLTE decryption not supported!");
            case 0x46: // F (frame)
                throw new NotImplementedException("BLTE frame not supported!");
            default:
                throw new Exception($"Unsupported mode {chunkType:X}!");
        }
    }
}
