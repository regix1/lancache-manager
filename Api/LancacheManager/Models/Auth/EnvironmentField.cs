using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// A sign-in service setting that a <c>Security__*</c> environment variable supplies. The browser
/// shows these fields read-only; only the names travel, never the values.
/// </summary>
/// <remarks>
/// The generic <see cref="JsonStringEnumConverter{TEnum}"/> with an explicit
/// <see cref="JsonNamingPolicy.CamelCase"/> is required: the non-generic converter ignores the
/// global naming policy and writes "ClientId", which the browser's field names ("clientId") never
/// match.
/// </remarks>
[JsonConverter(typeof(EnvironmentFieldJsonConverter))]
public enum EnvironmentField
{
    ClientId,
    ClientSecret,
    Authority,
    DisplayName,
    AllowedSubjects,
    OwnerSubject,
    OwnerIssuer,
    Tenant,
    TeamId,
    KeyId,
    PrivateKey
}

internal sealed class EnvironmentFieldJsonConverter : JsonStringEnumConverter<EnvironmentField>
{
    public EnvironmentFieldJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
