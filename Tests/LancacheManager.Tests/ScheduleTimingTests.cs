using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Infrastructure.Services.Scheduling;

namespace LancacheManager.Tests;

public class ScheduleTimingTests
{
    // Berlin runs on CET (+1) in winter and CEST (+2) in summer, and its 2026 changeovers are
    // 29 March and 25 October - the two dates the daylight-saving tests below are built on.
    private const string BerlinZoneId = "Europe/Berlin";
    private static readonly TimeOnly NightWindowStart = new(22, 0);
    private static readonly TimeOnly NightWindowEnd = new(6, 0);
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- Next occurrence (ComputeNextRun) ----

    [Fact]
    public void ComputeNextRun_UnspecifiedKindInput_IsAnswered()
    {
        // A DateTime that came back through EF Core or a JSON round trip is frequently Unspecified,
        // and Cronos throws ArgumentException on any kind other than Utc.
        var afterUtc = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Unspecified);

        var nextRun = ScheduleTiming.ComputeNextRun(MakeSchedule("0 2 * * *"), afterUtc);

        Assert.Equal(new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc), nextRun);
    }

    [Fact]
    public void ComputeNextRun_NoWindow_IsTheNextOccurrenceStrictlyAfter()
    {
        // Strictly after, so a schedule asked about at the exact moment it fired does not answer with
        // that same moment and run twice.
        var onTheOccurrence = new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc);

        var nextRun = ScheduleTiming.ComputeNextRun(MakeSchedule("0 2 * * *"), onTheOccurrence);

        Assert.Equal(new DateTime(2026, 1, 3, 2, 0, 0, DateTimeKind.Utc), nextRun);
    }

    [Fact]
    public void ComputeNextRun_WindowCrossingMidnight_KeepsEveryOccurrenceInsideIt()
    {
        // 22:00-06:00 wraps past midnight, so the window test is "at or after 22:00 OR before 06:00".
        // Read as an "AND" it would let nothing through at all.
        var schedule = MakeSchedule("0 */2 * * *", BerlinZoneId, NightWindowStart, NightWindowEnd);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(BerlinZoneId);
        var afterUtc = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

        for (var occurrence = 0; occurrence < 5; occurrence++)
        {
            var nextRun = ScheduleTiming.ComputeNextRun(schedule, afterUtc);
            Assert.NotNull(nextRun);

            var localHour = TimeZoneInfo.ConvertTimeFromUtc(nextRun.Value, zone).Hour;
            Assert.Contains(localHour, new[] { 22, 0, 2, 4 });

            afterUtc = nextRun.Value;
        }
    }

    [Fact]
    public void ComputeNextRun_ExpressionThatNeverLandsInTheWindow_IsNullAndFinishes()
    {
        // 15:00 daily can never fall inside 22:00-06:00. Nothing is wrong with either half on its
        // own, so the walk has to stop on its own bound rather than spin on the thread that asked.
        var schedule = MakeSchedule("0 15 * * *", BerlinZoneId, NightWindowStart, NightWindowEnd);
        var stopwatch = Stopwatch.StartNew();

        var nextRun = ScheduleTiming.ComputeNextRun(schedule, new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));
        stopwatch.Stop();

        Assert.Null(nextRun);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"The bounded walk took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void ComputeNextRun_ExpressionWithNoFutureOccurrence_IsNull()
    {
        // February 30th never comes round, so Cronos yields no occurrences at all and the answer is
        // null rather than an exception.
        var nextRun = ScheduleTiming.ComputeNextRun(
            MakeSchedule("0 0 30 2 *"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(nextRun);
    }

    [Fact]
    public void ComputeNextRun_LeapDayExpression_ReachesTheNextLeapYear()
    {
        // "0 0 29 2 *" can sit almost four years away - asked in August 2026, the next 29 February
        // is in 2028, roughly 571 days out. A 400-day look-ahead called this unreachable and the
        // save was rejected, so the walk has to clear a full leap cycle.
        var nextRun = ScheduleTiming.ComputeNextRun(
            MakeSchedule("0 0 29 2 *"),
            new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc), nextRun);
    }

    [Fact]
    public void ComputeNextRun_LeapDayExpressionAcrossANonLeapCentury_ReachesTheNextLeapYear()
    {
        // The longest real gap for "0 0 29 2 *" is not four years but eight: 2100 skips its leap
        // year, so asked just after 2096-02-29 the next occurrence is 2104-02-29, roughly 2922 days
        // out. Both a 400-day and a 1500-day look-ahead called this unreachable.
        var nextRun = ScheduleTiming.ComputeNextRun(
            MakeSchedule("0 0 29 2 *"),
            new DateTime(2096, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2104, 2, 29, 0, 0, 0, DateTimeKind.Utc), nextRun);
    }

    [Fact]
    public void ComputeNextRun_UnreadableExpression_IsNull()
    {
        var nextRun = ScheduleTiming.ComputeNextRun(
            MakeSchedule("not a schedule"),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Null(nextRun);
    }

    [Fact]
    public void ComputeNextRun_SpringForward_StillRunsOnTheNightTheHourDisappears()
    {
        // Berlin skips 02:00-03:00 on 29 March 2026, so a daily 02:00 schedule has no 02:00 to fire
        // at. It must still run that night instead of silently missing a day.
        var schedule = MakeSchedule("0 2 * * *", BerlinZoneId);

        var nextRun = ScheduleTiming.ComputeNextRun(schedule, new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), nextRun);
    }

    [Fact]
    public void ComputeNextRun_FallBack_RunsOnceInTheRepeatedHour()
    {
        // Berlin repeats 02:00-03:00 on 25 October 2026, so local 02:30 happens twice that night -
        // once on CEST and once on CET. The schedule must run once, not twice.
        var schedule = MakeSchedule("30 2 * * *", BerlinZoneId);

        var firstRun = new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc);
        Assert.Equal(firstRun, ScheduleTiming.ComputeNextRun(schedule, new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc)));

        // The second pass through local 02:30 that night must NOT be a second run.
        Assert.Equal(new DateTime(2026, 10, 26, 1, 30, 0, DateTimeKind.Utc), ScheduleTiming.ComputeNextRun(schedule, firstRun));
    }

    // ---- Window test (IsWithinWindow) ----

    [Theory]
    [InlineData(22, 30, true)]   // inside, before midnight
    [InlineData(2, 0, true)]     // inside, after midnight - the half an "AND" reading loses
    [InlineData(5, 59, true)]    // the last minute inside the window
    [InlineData(6, 0, false)]    // the end bound is exclusive
    [InlineData(21, 59, false)]  // one minute before the window opens
    [InlineData(15, 0, false)]   // the middle of the day the window exists to keep runs off
    public void IsWithinWindow_WindowCrossingMidnight_MatchesTruthTable(int localHour, int localMinute, bool expected)
    {
        var schedule = MakeSchedule("0 * * * *", BerlinZoneId, NightWindowStart, NightWindowEnd);
        // 5 January is CET, so Berlin local time is one hour ahead of the UTC instant asked about.
        var instantUtc = new DateTime(2026, 1, 5, localHour, localMinute, 0, DateTimeKind.Utc).AddHours(-1);

        Assert.Equal(expected, ScheduleTiming.IsWithinWindow(schedule, instantUtc));
    }

    [Theory]
    [InlineData(9, 0, true)]     // inside a plain daytime window
    [InlineData(17, 0, false)]   // the end bound is exclusive here too
    [InlineData(3, 0, false)]    // before it opens
    public void IsWithinWindow_WindowInsideOneDay_MatchesTruthTable(int localHour, int localMinute, bool expected)
    {
        var schedule = MakeSchedule("0 * * * *", BerlinZoneId, new TimeOnly(8, 0), new TimeOnly(17, 0));
        var instantUtc = new DateTime(2026, 1, 5, localHour, localMinute, 0, DateTimeKind.Utc).AddHours(-1);

        Assert.Equal(expected, ScheduleTiming.IsWithinWindow(schedule, instantUtc));
    }

    [Fact]
    public void IsWithinWindow_NoWindow_IsAlwaysTrue()
    {
        var schedule = MakeSchedule("0 15 * * *", BerlinZoneId);

        Assert.True(ScheduleTiming.IsWithinWindow(schedule, new DateTime(2026, 1, 5, 14, 0, 0, DateTimeKind.Utc)));
    }

    // ---- Save-time validation (Validate) ----

    [Fact]
    public void Validate_UsableSchedule_IsNull()
    {
        Assert.Null(ScheduleTiming.Validate(MakeSchedule("0 2 * * *", BerlinZoneId)));
        Assert.Null(ScheduleTiming.Validate(MakeSchedule("0 */2 * * *", BerlinZoneId, NightWindowStart, NightWindowEnd)));
    }

    [Fact]
    public void Validate_UtcZone_IsAccepted()
    {
        // Windows publishes "UTC" as one of its own zone names, so the Windows-name check below has
        // to let it through: it is also what the container reports when no TZ is set.
        Assert.Null(ScheduleTiming.Validate(MakeSchedule("0 2 * * *")));
    }

    [Fact]
    public void Validate_WindowsTimeZoneName_ExplainsItInsteadOfThrowing()
    {
        // "W. Europe Standard Time" resolves on a Windows machine, and the IANA name resolves in the
        // Linux container. Accepting both would mean saving a schedule that only means the right
        // thing on one of them.
        var reason = ScheduleTiming.Validate(MakeSchedule("0 2 * * *", "W. Europe Standard Time"));

        Assert.NotNull(reason);
        Assert.Contains("W. Europe Standard Time", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_UnknownTimeZone_ExplainsItInsteadOfThrowing()
    {
        Assert.NotNull(ScheduleTiming.Validate(MakeSchedule("0 2 * * *", "Nowhere/Atall")));
    }

    [Fact]
    public void Validate_UnreadableExpression_IsRejected()
    {
        Assert.NotNull(ScheduleTiming.Validate(MakeSchedule("not a schedule")));
        Assert.NotNull(ScheduleTiming.Validate(MakeSchedule(string.Empty)));
    }

    [Fact]
    public void Validate_OneWindowBoundOnly_IsRejected()
    {
        Assert.NotNull(ScheduleTiming.Validate(MakeSchedule("0 2 * * *", BerlinZoneId, NightWindowStart, windowEnd: null)));
        Assert.NotNull(ScheduleTiming.Validate(MakeSchedule("0 2 * * *", BerlinZoneId, windowStart: null, windowEnd: NightWindowEnd)));
    }

    [Fact]
    public void Validate_ZeroLengthWindow_IsRejected()
    {
        Assert.NotNull(ScheduleTiming.Validate(MakeSchedule("0 2 * * *", BerlinZoneId, NightWindowStart, NightWindowStart)));
    }

    [Fact]
    public void Validate_ScheduleThatCanNeverRun_IsRejected()
    {
        // Both halves are fine on their own and the save is the last moment anyone is looking, so
        // this has to be caught here rather than leaving a job that quietly never runs.
        Assert.NotNull(ScheduleTiming.Validate(MakeSchedule("0 15 * * *", BerlinZoneId, NightWindowStart, NightWindowEnd)));
    }

    [Fact]
    public void Validate_LeapDayExpression_IsAccepted()
    {
        // The next occurrence can be nearly four years out, which is still a schedule that runs.
        Assert.Null(ScheduleTiming.Validate(MakeSchedule("0 0 29 2 *")));
    }

    // ---- Wire shape (TimeOnlyJsonConverter) ----

    [Fact]
    public void CustomSchedule_WindowBounds_RoundTripAsHoursAndMinutes()
    {
        // The built-in TimeOnly converter writes "22:00:00.0000000", which carries resolution a window
        // bound does not have and which the frontend's time fields cannot read back.
        var json = JsonSerializer.Serialize(MakeSchedule("0 */2 * * *", BerlinZoneId, NightWindowStart, NightWindowEnd), CamelCaseJson);

        Assert.Contains("\"windowStart\":\"22:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"windowEnd\":\"06:00\"", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<CustomSchedule>(json, CamelCaseJson);
        Assert.NotNull(restored);
        Assert.Equal(NightWindowStart, restored.WindowStart);
        Assert.Equal(NightWindowEnd, restored.WindowEnd);
    }

    [Fact]
    public void CustomSchedule_NoWindow_RoundTripsAsNull()
    {
        var json = JsonSerializer.Serialize(MakeSchedule("0 2 * * *", BerlinZoneId), CamelCaseJson);

        var restored = JsonSerializer.Deserialize<CustomSchedule>(json, CamelCaseJson);

        Assert.NotNull(restored);
        Assert.Null(restored.WindowStart);
        Assert.Null(restored.WindowEnd);
    }

    private static CustomSchedule MakeSchedule(
        string expression,
        string timeZoneId = "UTC",
        TimeOnly? windowStart = null,
        TimeOnly? windowEnd = null)
    {
        return new CustomSchedule
        {
            Expression = expression,
            TimeZoneId = timeZoneId,
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };
    }
}
