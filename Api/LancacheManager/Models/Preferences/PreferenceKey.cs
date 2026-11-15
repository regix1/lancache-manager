using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(PreferenceKeyJsonConverter))]
public enum PreferenceKey
{
    Unknown,
    SelectedTheme,
    SharpCorners,
    DisableFocusOutlines,
    DisableTooltips,
    PicsAlwaysVisible,
    DisableStickyNotifications,
    UseLocalTimezone,
    UseUtcTimezone,
    Use24HourFormat,
    ShowDatasourceLabels,
    RefreshRate,
    RefreshRateLocked,
    AllowedTimeFormats,
    SteamMaxThreadCount,
    EpicMaxThreadCount,
    XboxMaxThreadCount,
    BattleNetMaxThreadCount,
    RiotMaxThreadCount
}

public class PreferenceKeyJsonConverter : JsonConverter<PreferenceKey>
{
    public static PreferenceKey ParseFromString(string? value) => value?.ToLowerInvariant() switch
    {
        "selectedtheme" => PreferenceKey.SelectedTheme,
        "sharpcorners" => PreferenceKey.SharpCorners,
        "disablefocusoutlines" => PreferenceKey.DisableFocusOutlines,
        "disabletooltips" => PreferenceKey.DisableTooltips,
        "picsalwaysvisible" => PreferenceKey.PicsAlwaysVisible,
        "disablestickynotifications" => PreferenceKey.DisableStickyNotifications,
        "uselocaltimezone" => PreferenceKey.UseLocalTimezone,
        "useutctimezone" => PreferenceKey.UseUtcTimezone,
        "use24hourformat" => PreferenceKey.Use24HourFormat,
        "showdatasourcelabels" => PreferenceKey.ShowDatasourceLabels,
        "refreshrate" => PreferenceKey.RefreshRate,
        "refreshratelocked" => PreferenceKey.RefreshRateLocked,
        "allowedtimeformats" => PreferenceKey.AllowedTimeFormats,
        "steammaxthreadcount" => PreferenceKey.SteamMaxThreadCount,
        "epicmaxthreadcount" => PreferenceKey.EpicMaxThreadCount,
        "xboxmaxthreadcount" => PreferenceKey.XboxMaxThreadCount,
        "battlenetmaxthreadcount" => PreferenceKey.BattleNetMaxThreadCount,
        "riotmaxthreadcount" => PreferenceKey.RiotMaxThreadCount,
        _ => PreferenceKey.Unknown
    };

    public override PreferenceKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value?.ToLowerInvariant() switch
        {
            "selectedtheme" => PreferenceKey.SelectedTheme,
            "sharpcorners" => PreferenceKey.SharpCorners,
            "disablefocusoutlines" => PreferenceKey.DisableFocusOutlines,
            "disabletooltips" => PreferenceKey.DisableTooltips,
            "picsalwaysvisible" => PreferenceKey.PicsAlwaysVisible,
            "disablestickynotifications" => PreferenceKey.DisableStickyNotifications,
            "uselocaltimezone" => PreferenceKey.UseLocalTimezone,
            "useutctimezone" => PreferenceKey.UseUtcTimezone,
            "use24hourformat" => PreferenceKey.Use24HourFormat,
            "showdatasourcelabels" => PreferenceKey.ShowDatasourceLabels,
            "refreshrate" => PreferenceKey.RefreshRate,
            "refreshratelocked" => PreferenceKey.RefreshRateLocked,
            "allowedtimeformats" => PreferenceKey.AllowedTimeFormats,
            "steammaxthreadcount" => PreferenceKey.SteamMaxThreadCount,
            "epicmaxthreadcount" => PreferenceKey.EpicMaxThreadCount,
            "xboxmaxthreadcount" => PreferenceKey.XboxMaxThreadCount,
            "battlenetmaxthreadcount" => PreferenceKey.BattleNetMaxThreadCount,
            "riotmaxthreadcount" => PreferenceKey.RiotMaxThreadCount,
            _ => PreferenceKey.Unknown
        };
    }

    public override void Write(Utf8JsonWriter writer, PreferenceKey value, JsonSerializerOptions options)
    {
        var str = value switch
        {
            PreferenceKey.SelectedTheme => "selectedtheme",
            PreferenceKey.SharpCorners => "sharpcorners",
            PreferenceKey.DisableFocusOutlines => "disablefocusoutlines",
            PreferenceKey.DisableTooltips => "disabletooltips",
            PreferenceKey.PicsAlwaysVisible => "picsalwaysvisible",
            PreferenceKey.DisableStickyNotifications => "disablestickynotifications",
            PreferenceKey.UseLocalTimezone => "uselocaltimezone",
            PreferenceKey.UseUtcTimezone => "useutctimezone",
            PreferenceKey.Use24HourFormat => "use24hourformat",
            PreferenceKey.ShowDatasourceLabels => "showdatasourcelabels",
            PreferenceKey.RefreshRate => "refreshrate",
            PreferenceKey.RefreshRateLocked => "refreshratelocked",
            PreferenceKey.AllowedTimeFormats => "allowedtimeformats",
            PreferenceKey.SteamMaxThreadCount => "steammaxthreadcount",
            PreferenceKey.EpicMaxThreadCount => "epicmaxthreadcount",
            PreferenceKey.XboxMaxThreadCount => "xboxmaxthreadcount",
            PreferenceKey.BattleNetMaxThreadCount => "battlenetmaxthreadcount",
            PreferenceKey.RiotMaxThreadCount => "riotmaxthreadcount",
            _ => "unknown"
        };
        writer.WriteStringValue(str);
    }
}
