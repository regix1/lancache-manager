using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using LancacheManager.Models;
using Microsoft.IdentityModel.Tokens;

namespace LancacheManager.Security;

public static class AppleClientSecret
{
    public static string Create(OidcSettings settings, DateTimeOffset now)
    {
        using var algorithm = ECDsa.Create();
        algorithm.ImportFromPem(settings.PrivateKey);
        var key = new ECDsaSecurityKey(algorithm) { KeyId = settings.KeyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
        var token = new JwtSecurityToken(
            issuer: settings.TeamId,
            audience: "https://appleid.apple.com",
            claims: [new Claim(JwtRegisteredClaimNames.Sub, settings.ClientId)],
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(5).UtcDateTime,
            signingCredentials: credentials);
        token.Header[JwtHeaderParameterNames.Kid] = settings.KeyId;
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
