using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Message type discriminator sent by the SteamPrefill daemon over the wire.
/// Wire values are kebab-case: "credential-challenge", "progress", "auth-state", "status-update".
/// </summary>
[JsonConverter(typeof(DaemonMessageTypeJsonConverter))]
public enum DaemonMessageType
{
    CredentialChallenge,
    Progress,
    AuthState,
    StatusUpdate,
    /// <summary>Fallback for any unrecognized wire value — never treat as an error.</summary>
    Unknown
}

/// <summary>
/// Converts <see cref="DaemonMessageType"/> to/from kebab-case wire strings:
/// "credential-challenge", "progress", "auth-state", "status-update".
/// Unrecognized values deserialize to <see cref="DaemonMessageType.Unknown"/>.
/// </summary>
internal sealed class DaemonMessageTypeJsonConverter : JsonConverter<DaemonMessageType>
{
    public override DaemonMessageType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "credential-challenge" => DaemonMessageType.CredentialChallenge,
            "progress"             => DaemonMessageType.Progress,
            "auth-state"           => DaemonMessageType.AuthState,
            "status-update"        => DaemonMessageType.StatusUpdate,
            _                      => DaemonMessageType.Unknown
        };
    }

    public override void Write(Utf8JsonWriter writer, DaemonMessageType value, JsonSerializerOptions options)
    {
        var wire = value switch
        {
            DaemonMessageType.CredentialChallenge => "credential-challenge",
            DaemonMessageType.Progress            => "progress",
            DaemonMessageType.AuthState           => "auth-state",
            DaemonMessageType.StatusUpdate        => "status-update",
            _                                     => "unknown"
        };
        writer.WriteStringValue(wire);
    }
}
