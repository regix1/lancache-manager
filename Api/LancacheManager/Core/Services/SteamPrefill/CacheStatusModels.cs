using System.Text.Json.Serialization;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamPrefill;

[JsonConverter(typeof(JsonStringEnumConverter<CacheOutcome>))]
public enum CacheOutcome
{
    Current,
    Outdated,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheReason>))]
public enum CacheReason
{
    UnsupportedDaemon,
    Disconnected,
    ConnectionChanged,
    StatusUnavailable,
    InvalidResult,
    InvalidAppId,
    MissingApp,
    LinkedDepotUnavailable,
    ManifestUnavailable,
    UnsupportedOs,
    NoContent,
    NoCacheEvidence,
    InspectionFailed,
    DeadlineReached,
    AuthenticationRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheAuthority>))]
public enum CacheAuthority
{
    Absent,
    Empty,
    Snapshot
}

public sealed class CacheAppScope
{
    [JsonPropertyName("appId")]
    public required uint AppId { get; init; }

    [JsonPropertyName("authority")]
    public required CacheAuthority Authority { get; init; }
}

public class CacheStatusResult
{
    public static CacheStatusResult Unknown(IEnumerable<string> requested, CacheReason reason) => new()
    {
        Version = 2,
        Apps = requested.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(appId => AppCacheStatus.Unknown(appId, reason))
            .ToList()
    };

    public CacheStatusResult Normalize(
        IEnumerable<string> requested,
        CacheReason legacyUnknownReason = CacheReason.InvalidResult,
        bool acceptLegacyOutdated = true)
    {
        var ids = requested.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var source = Apps ?? [];
        if (Version is not null and not 2)
        {
            var invalidVersion = Unknown(ids, CacheReason.InvalidResult);
            invalidVersion.Message = Message;
            return invalidVersion;
        }

        if (Version == 2)
        {
            var requestedSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var malformedSet = source.Any(app => app is null || string.IsNullOrWhiteSpace(app.AppId)
                    || !requestedSet.Contains(app.AppId))
                || source.GroupBy(app => app.AppId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1);
            if (malformedSet)
            {
                var invalidRows = Unknown(ids, CacheReason.InvalidResult);
                invalidRows.Message = Message;
                return invalidRows;
            }

            var rows = source.ToDictionary(app => app.AppId, StringComparer.OrdinalIgnoreCase);
            var normalized = new List<AppCacheStatus>(ids.Count);
            foreach (var appId in ids)
            {
                if (!rows.TryGetValue(appId, out var row) || !row.ValidV2())
                {
                    normalized.Add(AppCacheStatus.Unknown(appId, CacheReason.InvalidResult));
                    continue;
                }

                normalized.Add(row.Copy(appId));
            }

            return new CacheStatusResult { Version = 2, Apps = normalized, Message = Message };
        }

        var legacy = source
            .Where(app => app is not null && !string.IsNullOrWhiteSpace(app.AppId))
            .GroupBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        var legacyRows = new List<AppCacheStatus>(ids.Count);
        foreach (var appId in ids)
        {
            if (!legacy.TryGetValue(appId, out var matches) || matches.Any(match => match.IsUpToDate is null))
            {
                legacyRows.Add(AppCacheStatus.Unknown(appId, legacyUnknownReason));
            }
            else if (matches.Any(match => match.IsUpToDate == false))
            {
                legacyRows.Add(acceptLegacyOutdated
                    ? AppCacheStatus.Outdated(appId)
                    : AppCacheStatus.Unknown(appId, legacyUnknownReason));
            }
            else if (matches.Any(match => match.IsUpToDate == true))
            {
                legacyRows.Add(AppCacheStatus.Current(appId));
            }
            else
            {
                legacyRows.Add(AppCacheStatus.Unknown(appId, legacyUnknownReason));
            }
        }

        return new CacheStatusResult { Version = 2, Apps = legacyRows, Message = Message };
    }

    public (List<string> UpToDateAppIds, List<string> OutdatedAppIds, List<string> UnknownAppIds) ResolveAppIds(
        IEnumerable<string> requested)
    {
        var normalized = Normalize(requested);
        return (
            normalized.Apps.Where(app => app.Outcome == CacheOutcome.Current).Select(app => app.AppId).ToList(),
            normalized.Apps.Where(app => app.Outcome == CacheOutcome.Outdated).Select(app => app.AppId).ToList(),
            normalized.Apps.Where(app => app.Outcome == CacheOutcome.Unknown).Select(app => app.AppId).ToList());
    }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("apps")]
    public List<AppCacheStatus> Apps { get; set; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonIgnore]
    internal CacheReason? StopReason { get; set; }

}

public class AppCacheStatus
{
    public static AppCacheStatus Current(string appId) => new()
    {
        AppId = appId,
        IsUpToDate = true,
        Outcome = CacheOutcome.Current
    };

    public static AppCacheStatus Outdated(string appId) => new()
    {
        AppId = appId,
        IsUpToDate = false,
        Outcome = CacheOutcome.Outdated
    };

    public static AppCacheStatus Unknown(string appId, CacheReason reason) => new()
    {
        AppId = appId,
        Outcome = CacheOutcome.Unknown,
        Reason = reason
    };

    [JsonPropertyName("appId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isUpToDate")]
    public bool? IsUpToDate { get; set; }

    [JsonPropertyName("outcome")]
    public CacheOutcome? Outcome { get; set; }

    [JsonPropertyName("reason")]
    public CacheReason? Reason { get; set; }

    [JsonPropertyName("downloadSize")]
    public long DownloadSize { get; set; }

    internal bool ValidV2() => Outcome switch
    {
        CacheOutcome.Current => Reason is null && IsUpToDate == true,
        CacheOutcome.Outdated => Reason is null && IsUpToDate == false,
        CacheOutcome.Unknown => Reason is not null
            && Enum.IsDefined(Reason.Value)
            && IsUpToDate != true,
        _ => false
    };

    internal AppCacheStatus Copy(string appId) => new()
    {
        AppId = appId,
        Name = Name,
        IsUpToDate = Outcome switch
        {
            CacheOutcome.Current => true,
            CacheOutcome.Outdated => false,
            _ => null
        },
        Outcome = Outcome,
        Reason = Reason,
        DownloadSize = DownloadSize
    };
}
