using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LancacheManager.Infrastructure.Data.Converters;

/// <summary>
/// EF Core ValueConverter that persists an enum as a lowercase string in the database
/// and parses any casing back into the enum.
///
/// Use this whenever a pre-existing database stores lowercase literal values
/// (e.g. "admin", "guest", "active") and the C# enum is PascalCase.
/// </summary>
public sealed class LowercaseStringEnumConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public LowercaseStringEnumConverter()
        : base(
            v => v.ToString().ToLowerInvariant(),
            s => Enum.Parse<TEnum>(s, ignoreCase: true))
    {
    }
}
