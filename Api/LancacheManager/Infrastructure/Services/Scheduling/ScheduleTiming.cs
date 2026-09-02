using System.Globalization;
using Cronos;

namespace LancacheManager.Infrastructure.Services.Scheduling;

/// <summary>
/// Timing math for <see cref="CustomSchedule"/>: when it fires next, whether an instant falls inside
/// its window, and whether it can ever run at all. Both schedulers go through these three functions
/// and nothing else, so a schedule means exactly the same thing whether it drives scheduled prefill
/// or one of the generic schedules.
/// </summary>
public static class ScheduleTiming
{
    // Bounds on the occurrence walk in ComputeNextRun. An expression and a window that never
    // intersect - 15:00 daily inside a 22:00-06:00 window - have no next run at all, and an unbounded
    // walk would spin forever on whichever thread asked. The two bounds split the work. The candidate
    // count stops a dense expression: a per-minute one burns 4000 candidates inside three days. The
    // horizon stops a sparse one, so it must clear the longest real gap between occurrences of a
    // valid expression, which is the eight years between leap days across a non-leap century - a
    // leap-day expression ("0 0 29 2 *") asked just after 2096-02-29 next fires 2104-02-29, roughly
    // 2922 days later, because 2100 skips its leap year. 3700 days covers that with margin, and
    // GetOccurrences is lazy, so the wide horizon costs nothing when the walk ends early.
    private const int MaxCandidateOccurrences = 4000;
    private static readonly TimeSpan _maxLookAhead = TimeSpan.FromDays(3700);

    private const string WindowBoundFormat = "HH:mm";

    /// <summary>
    /// The next moment this schedule fires strictly after <paramref name="afterUtc"/>, or null when
    /// it never fires again - an expression with no future occurrence, or a window the expression can
    /// never land inside.
    /// </summary>
    public static DateTime? ComputeNextRun(CustomSchedule schedule, DateTime afterUtc)
    {
        var expression = ParseExpression(schedule.Expression);
        if (expression is null)
        {
            return null;
        }

        var zone = ResolveTimeZone(schedule.TimeZoneId);
        if (zone is null)
        {
            return null;
        }

        // Cronos throws ArgumentException unless the kind is Utc, and a DateTime that came back
        // through EF Core or a JSON round trip is frequently Unspecified.
        var searchFrom = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);

        // An expression Cronos itself cannot satisfy ("0 0 30 2 *" - February 30th) simply yields no
        // occurrences, so the loop falls through to null rather than throwing.
        var occurrences = expression.GetOccurrences(
            searchFrom,
            searchFrom + _maxLookAhead,
            zone,
            fromInclusive: false,
            toInclusive: true);

        foreach (var occurrence in occurrences.Take(MaxCandidateOccurrences))
        {
            if (IsWithinWindow(schedule, occurrence))
            {
                return occurrence;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="instantUtc"/> falls inside the schedule's window. A schedule with no
    /// window is always inside it.
    /// </summary>
    public static bool IsWithinWindow(CustomSchedule schedule, DateTime instantUtc)
    {
        if (schedule.WindowStart is not { } start || schedule.WindowEnd is not { } end)
        {
            return true;
        }

        var zone = ResolveTimeZone(schedule.TimeZoneId);
        if (zone is null)
        {
            // The window is stated in a zone this machine cannot resolve, so where it actually falls
            // is unknown. A window exists to keep runs off the link at certain hours, so an unknown
            // answer must not let a run start. Validate refuses to save a schedule in this state.
            return false;
        }

        var localTime = TimeOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc), zone));

        // An end earlier than the start is how a window that crosses midnight is written (22:00-06:00),
        // so the test turns into "at or after the start OR before the end" instead of "AND".
        return end < start
            ? localTime >= start || localTime < end
            : localTime >= start && localTime < end;
    }

    /// <summary>
    /// Null when the schedule is usable, otherwise a sentence naming what is wrong with it. The
    /// sentence is shown to the admin who tried to save, so it says what to do about it.
    /// </summary>
    public static string? Validate(CustomSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.Expression))
        {
            return "A custom schedule needs a repeat expression.";
        }

        if (ParseExpression(schedule.Expression) is null)
        {
            return $"'{schedule.Expression}' is not a schedule this server can read. It needs five fields: minute, hour, day of month, month, and day of week.";
        }

        if (string.IsNullOrWhiteSpace(schedule.TimeZoneId))
        {
            return "A custom schedule needs a timezone.";
        }

        // "UTC" names the same zone on every platform, so it is allowed through even though Windows
        // also publishes it as one of its own names.
        if (!IsUtcId(schedule.TimeZoneId)
            && TimeZoneInfo.TryConvertWindowsIdToIanaId(schedule.TimeZoneId, out var ianaId))
        {
            return $"Timezone '{schedule.TimeZoneId}' is a Windows name. Use '{ianaId}' instead, so the schedule means the same thing on the server as it does here.";
        }

        if (ResolveTimeZone(schedule.TimeZoneId) is null)
        {
            return $"Timezone '{schedule.TimeZoneId}' is not one this server knows about.";
        }

        if ((schedule.WindowStart is null) != (schedule.WindowEnd is null))
        {
            return "A time window needs both a start and an end.";
        }

        if (schedule.WindowStart is { } windowStart && windowStart == schedule.WindowEnd)
        {
            return "The time window starts and ends at the same time, so nothing could ever run inside it.";
        }

        // Catches an unreachable schedule at save time instead of leaving behind a job that quietly
        // never runs and gives the admin nothing to look at.
        if (ComputeNextRun(schedule, DateTime.UtcNow) is null)
        {
            return schedule.WindowStart is { } start && schedule.WindowEnd is { } end
                ? $"This schedule never runs: it repeats at times that never fall between {Format(start)} and {Format(end)}."
                : "This schedule never comes round again, so nothing would ever run.";
        }

        return null;
    }

    private static CronExpression? ParseExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }

        try
        {
            return CronExpression.Parse(expression);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The zone this machine knows by that id, or null when it knows none. Shared with the reader that
    /// names the server's own zone so a zone id means resolvable in exactly one place.
    /// </summary>
    public static TimeZoneInfo? ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static bool IsUtcId(string timeZoneId)
        => string.Equals(timeZoneId, "UTC", StringComparison.OrdinalIgnoreCase);

    private static string Format(TimeOnly time)
        => time.ToString(WindowBoundFormat, CultureInfo.InvariantCulture);
}
