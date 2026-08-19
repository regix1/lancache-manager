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

public static class EpicMappingStatusExtensions
{
    public static string ToDisplayString(this EpicMappingStatus status) => status switch
    {
        EpicMappingStatus.Idle => "Idle",
        EpicMappingStatus.Authenticating => "Authenticating",
        EpicMappingStatus.RefreshingCatalog => "Refreshing catalog",
        EpicMappingStatus.Completed => "Completed",
        EpicMappingStatus.Error => "Error",
        EpicMappingStatus.Unknown => "Unknown",
        _ => "Unknown"
    };
}
