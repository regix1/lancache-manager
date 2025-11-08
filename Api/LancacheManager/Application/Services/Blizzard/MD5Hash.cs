using System.Runtime.InteropServices;

namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// Represents a 16-byte MD5 hash as two 64-bit integers.
/// This is how Blizzard represents hashes throughout their system.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MD5Hash : IEquatable<MD5Hash>
{
    public readonly ulong LowPart;
    public readonly ulong HighPart;

    public MD5Hash(ulong lowPart, ulong highPart)
    {
        LowPart = lowPart;
        HighPart = highPart;
    }

    public static MD5Hash FromBytes(byte[] data, int offset = 0)
    {
        if (data.Length - offset < 16)
            throw new ArgumentException("Not enough bytes for MD5 hash");

        ulong low = BitConverter.ToUInt64(data, offset);
        ulong high = BitConverter.ToUInt64(data, offset + 8);
        return new MD5Hash(low, high);
    }

    public static MD5Hash FromHexString(string hex)
    {
        if (hex.Length != 32)
            throw new ArgumentException("MD5 hex string must be 32 characters");

        byte[] bytes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return FromBytes(bytes);
    }

    public string ToHexString()
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(LowPart).CopyTo(bytes, 0);
        BitConverter.GetBytes(HighPart).CopyTo(bytes, 8);
        return BitConverter.ToString(bytes).Replace("-", "").ToLower();
    }

    public bool Equals(MD5Hash other)
    {
        return LowPart == other.LowPart && HighPart == other.HighPart;
    }

    public override bool Equals(object? obj)
    {
        return obj is MD5Hash other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(LowPart, HighPart);
    }

    public override string ToString()
    {
        return ToHexString();
    }

    public static bool operator ==(MD5Hash left, MD5Hash right) => left.Equals(right);
    public static bool operator !=(MD5Hash left, MD5Hash right) => !left.Equals(right);
}
