using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(LoginKindJsonConverter))]
public enum LoginKind
{
    CustomOidc,
    Google,
    GitHub,
    Microsoft,
    Apple
}

internal sealed class LoginKindJsonConverter : JsonConverter<LoginKind>
{
    public override LoginKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Login kind must be a string");
        }

        return reader.GetString()?.ToLowerInvariant() switch
        {
            "customoidc" => LoginKind.CustomOidc,
            "google" => LoginKind.Google,
            "github" => LoginKind.GitHub,
            "microsoft" => LoginKind.Microsoft,
            "apple" => LoginKind.Apple,
            _ => throw new JsonException("Unknown login kind")
        };
    }

    public override void Write(Utf8JsonWriter writer, LoginKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            LoginKind.CustomOidc => "customOidc",
            LoginKind.Google => "google",
            LoginKind.GitHub => "github",
            LoginKind.Microsoft => "microsoft",
            LoginKind.Apple => "apple",
            _ => throw new JsonException("Unknown login kind")
        });
    }
}
