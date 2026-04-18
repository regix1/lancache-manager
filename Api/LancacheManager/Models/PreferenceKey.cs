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
    Use24HourFormat,
    ShowDatasourceLabels,
    ShowYearInDates,
    RefreshRate,
    RefreshRateLocked,
    AllowedTimeFormats,
    SteamMaxThreadCount,
    EpicMaxThreadCount
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
        "use24hourformat" => PreferenceKey.Use24HourFormat,
        "showdatasourcelabels" => PreferenceKey.ShowDatasourceLabels,
        "showyearindates" => PreferenceKey.ShowYearInDates,
        "refreshrate" => PreferenceKey.RefreshRate,
        "refreshratelocked" => PreferenceKey.RefreshRateLocked,
        "allowedtimeformats" => PreferenceKey.AllowedTimeFormats,
        "steammaxthreadcount" => PreferenceKey.SteamMaxThreadCount,
        "epicmaxthreadcount" => PreferenceKey.EpicMaxThreadCount,
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
            "use24hourformat" => PreferenceKey.Use24HourFormat,
            "showdatasourcelabels" => PreferenceKey.ShowDatasourceLabels,
            "showyearindates" => PreferenceKey.ShowYearInDates,
            "refreshrate" => PreferenceKey.RefreshRate,
            "refreshratelocked" => PreferenceKey.RefreshRateLocked,
            "allowedtimeformats" => PreferenceKey.AllowedTimeFormats,
            "steammaxthreadcount" => PreferenceKey.SteamMaxThreadCount,
            "epicmaxthreadcount" => PreferenceKey.EpicMaxThreadCount,
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
            PreferenceKey.Use24HourFormat => "use24hourformat",
            PreferenceKey.ShowDatasourceLabels => "showdatasourcelabels",
            PreferenceKey.ShowYearInDates => "showyearindates",
            PreferenceKey.RefreshRate => "refreshrate",
            PreferenceKey.RefreshRateLocked => "refreshratelocked",
            PreferenceKey.AllowedTimeFormats => "allowedtimeformats",
            PreferenceKey.SteamMaxThreadCount => "steammaxthreadcount",
            PreferenceKey.EpicMaxThreadCount => "epicmaxthreadcount",
            _ => "unknown"
        };
        writer.WriteStringValue(str);
    }
}
