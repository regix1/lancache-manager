using System.Runtime.CompilerServices;

namespace LancacheManager.Application.Services.Blizzard.Extensions;

public static class MD5Extensions
{
    public static MD5Hash ToMD5(this string str)
    {
        if (str.Length != 32)
        {
            throw new ArgumentException("input string length != 32", nameof(str));
        }
        var array = Convert.FromHexString(str);
        return Unsafe.As<byte, MD5Hash>(ref array[0]);
    }
}
