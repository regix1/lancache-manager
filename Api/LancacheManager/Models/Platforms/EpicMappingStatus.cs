using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(JsonStringEnumConverter<EpicMappingStatus>))]
public enum EpicMappingStatus
{
    Idle,
    Authenticating,
    RefreshingCatalog,
    Completed,
    Error,
    Unknown
}
