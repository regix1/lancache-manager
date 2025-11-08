using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace LancacheManager.Application.Services.Blizzard.Extensions;

public static class BinaryReaderExtensions
{
    public static short ReadInt16BigEndian(this BinaryReader reader)
    {
        return BinaryPrimitives.ReadInt16BigEndian(reader.ReadBytes(2));
    }

    public static int ReadInt32BigEndian(this BinaryReader reader)
    {
        return BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
    }

    public static ushort ReadUInt16BigEndian(this BinaryReader reader)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
    }

    public static uint ReadUInt32BigEndian(this BinaryReader reader)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
    }

    public static uint ReadUInt32BigEndian(this BinaryReader reader, byte[] buffer)
    {
        reader.Read(buffer, 0, buffer.Length);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    public static MD5Hash ReadMd5Hash(this BinaryReader reader, byte[] buffer)
    {
        reader.Read(buffer, 0, buffer.Length);
        return Unsafe.ReadUnaligned<MD5Hash>(ref buffer[0]);
    }

    public static byte[] AllocateBuffer<T>() where T : unmanaged
    {
        return new byte[Unsafe.SizeOf<T>()];
    }

    public static T Read<T>(this BinaryReader reader) where T : unmanaged
    {
        byte[] result = reader.ReadBytes(Unsafe.SizeOf<T>());
        return Unsafe.ReadUnaligned<T>(ref result[0]);
    }

    public static string ReadCString(this BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0)
        {
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static void CopyStream(this Stream input, Stream output, int bytes)
    {
        byte[] buffer = new byte[4096];
        int read;
        while (bytes > 0 && (read = input.Read(buffer, 0, Math.Min(buffer.Length, bytes))) > 0)
        {
            output.Write(buffer, 0, read);
            bytes -= read;
        }
    }
}
