using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Services.Scheduling;

/// <summary>
/// A schedule expressed as a cron recurrence plus an optional time-of-day window, stored per service
/// beside the plain interval. <c>null</c> means the service keeps running on its interval exactly as
/// before. When one IS present it wins outright over the interval, and the interval value is
/// deliberately left untouched on the record so that clearing the custom schedule puts the service
/// straight back on the cadence it had before.
/// </summary>
public sealed class CustomSchedule
{
    /// <summary>
    /// Standard 5-field cron expression (minute, hour, day of month, month, day of week), evaluated
    /// in <see cref="TimeZoneId"/> rather than in whatever local time the process happens to have.
    /// </summary>
    public required string Expression { get; init; }

    /// <summary>
    /// IANA zone id such as "Europe/Berlin", or the bare "UTC". A Windows name like
    /// "W. Europe Standard Time" is rejected by <see cref="ScheduleTiming.Validate"/>: it resolves on
    /// a Windows machine but this ships in a Linux container, and a schedule that only means the
    /// right thing on one of the two is worse than one that refuses to save.
    /// </summary>
    public required string TimeZoneId { get; init; }

    /// <summary>
    /// Start of the window a run is allowed to begin in, local to <see cref="TimeZoneId"/>. Null
    /// (together with <see cref="WindowEnd"/>) means there is no window and every occurrence runs.
    /// Serialized as "HH:mm" by <see cref="TimeOnlyJsonConverter"/>.
    /// </summary>
    [JsonConverter(typeof(TimeOnlyJsonConverter))]
    public TimeOnly? WindowStart { get; init; }

    /// <summary>
    /// End of the window, exclusive. May be EARLIER than <see cref="WindowStart"/>, which is how a
    /// window that crosses midnight is written (22:00 to 06:00). The window only decides whether a
    /// run may START - a run still going when the window closes is left to finish.
    /// </summary>
    [JsonConverter(typeof(TimeOnlyJsonConverter))]
    public TimeOnly? WindowEnd { get; init; }
}

/// <summary>
/// Serializes <see cref="TimeOnly"/> as "HH:mm". The built-in converter writes the full round-trip
/// form ("22:00:00.0000000"), which carries resolution a window bound does not have and which the
/// frontend's time fields cannot read back. Reads accept a longer form too, so a value written by
/// anything else still loads.
/// </summary>
internal sealed class TimeOnlyJsonConverter : JsonConverter<TimeOnly>
{
    private const string WireFormat = "HH:mm";

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // One malformed window bound must not stop the whole state file from loading, so it reads
            // back as midnight and the next save runs it through ScheduleTiming.Validate.
            return default;
        }

        var value = reader.GetString();
        return TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : default;
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(WireFormat, CultureInfo.InvariantCulture));
    }
}
