using System.Security.Cryptography;
using System.Text;

namespace LancacheManager.Core.Utilities;

public static class CryptoUtils
{
    public static string ComputeAnonymousHash(string userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Convert.ToBase64String(hash)[..12];
    }
}
