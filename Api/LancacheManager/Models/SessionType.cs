using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// User session type (admin vs guest).
/// Serialized as lowercase strings on the wire ("admin", "guest") to preserve
/// the pre-existing JSON contract with the frontend (auth.service.ts checks
/// <c>sessionType === 'admin'</c> / <c>'guest'</c>) and the persisted DB column
/// (see <c>LowercaseStringEnumConverter&lt;SessionType&gt;</c> in AppDbContext).
/// </summary>
/// <remarks>
/// We must use the generic <see cref="JsonStringEnumConverter{TEnum}"/> with an
/// explicit <see cref="JsonNamingPolicy.CamelCase"/> — the non-generic
/// <c>JsonStringEnumConverter</c> ignores the globally-configured naming policy
/// and would emit PascalCase ("Admin"/"Guest"), breaking the frontend.
/// </remarks>
[JsonConverter(typeof(SessionTypeJsonConverter))]
public enum SessionType
{
    Admin,
    Guest
}

internal sealed class SessionTypeJsonConverter : JsonStringEnumConverter<SessionType>
{
    public SessionTypeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
