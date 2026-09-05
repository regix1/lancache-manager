using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(AccountModeJsonConverter))]
public enum AccountMode
{
    Password,
    ApiKeyPassword,
    ApiKeyOidc,
    Oidc,
    Unauthenticated
}

internal sealed class AccountModeJsonConverter : JsonConverter<AccountMode>
{
    public override AccountMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Account mode must be a string");
        }

        return reader.GetString()?.ToLowerInvariant() switch
        {
            "password" => AccountMode.Password,
            "apikeypassword" => AccountMode.ApiKeyPassword,
            "apikeyoidc" => AccountMode.ApiKeyOidc,
            "oidc" => AccountMode.Oidc,
            "unauthenticated" => AccountMode.Unauthenticated,
            _ => throw new JsonException("Unknown account mode")
        };
    }

    public override void Write(Utf8JsonWriter writer, AccountMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            AccountMode.Password => "password",
            AccountMode.ApiKeyPassword => "apiKeyPassword",
            AccountMode.ApiKeyOidc => "apiKeyOidc",
            AccountMode.Oidc => "oidc",
            AccountMode.Unauthenticated => "unauthenticated",
            _ => throw new JsonException("Unknown account mode")
        });
    }
}
