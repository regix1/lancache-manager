using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PrefillPlatform>))]
public enum PrefillPlatform
{
    Steam,
    Epic
}
