using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionType
{
    Admin,
    Guest
}
