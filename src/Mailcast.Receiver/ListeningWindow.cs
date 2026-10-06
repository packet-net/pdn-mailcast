using System.Globalization;
using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>
/// GB7RDG's slots: one starts at <paramref name="Anchor"/> UTC and then every
/// <paramref name="EveryMinutes"/>, round the clock, and with a <paramref name="Daylight"/> rule
/// only those in daylight run.
/// </summary>
/// <param name="Anchor">One slot's start, UTC.</param>
/// <param name="EveryMinutes">Minutes from one slot's start to the next; it divides a day.</param>
/// <param name="Daylight">The head end's daylight rule, or null when every slot runs.</param>
public sealed record SlotSchedule(TimeOnly Anchor, int EveryMinutes, DaylightRule? Daylight = null)
{
    private const int MinutesPerDay = 1440;

    /// <summary>The same, as Packet.Mailcast and the directory have it.</summary>
    public SlotTimetable Timetable => new(Anchor, EveryMinutes, Daylight);

    /// <summary>A head end's timetable, as its directory gives it.</summary>
    public static SlotSchedule From(SlotTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        return new(timetable.Anchor, timetable.EveryMinutes, timetable.Daylight);
    }

    /// <summary>The slots that run on a UTC day, earliest first: every slot without a daylight rule.</summary>
    public IReadOnlyList<DateTimeOffset> ActiveOn(DateOnly day) => Timetable.ActiveSlotsOn(day);

    /// <summary>The first slot that runs starting at or after <paramref name="now"/>.</summary>
    public DateTimeOffset NextActiveStart(DateTimeOffset now) => Timetable.NextActiveAtOrAfter(now.ToUniversalTime()) ?? NextStart(now);

    /// <summary>The latest slot that ran starting at or before <paramref name="now"/>, if any.</summary>
    public DateTimeOffset? LatestActiveStart(DateTimeOffset now) => Timetable.ActiveAtOrBefore(now.ToUniversalTime());

    /// <summary>How many slots there are in a day.</summary>
    public int SlotsPerDay => MinutesPerDay / EveryMinutes;

    /// <summary>The slots' starts by time of day, in order from the anchor: the anchor first, then each one after it.</summary>
    public IReadOnlyList<TimeOnly> FromAnchor =>
        [.. Enumerable.Range(0, SlotsPerDay).Select(i => Anchor.AddMinutes((double)i * EveryMinutes))];

    /// <summary>The first slot that starts at or after <paramref name="now"/>.</summary>
    public DateTimeOffset NextStart(DateTimeOffset now) => ListeningWindow.NextStart(now, FromAnchor);

    /// <summary>The latest slot that starts at or before <paramref name="at"/>.</summary>
    public DateTimeOffset LatestStart(DateTimeOffset at)
    {
        var next = NextStart(at);
        return next == at.ToUniversalTime() ? next : next.AddMinutes(-EveryMinutes);
    }

    /// <summary>The slot start nearest <paramref name="at"/>, before or after it.</summary>
    public DateTimeOffset NearestStart(DateTimeOffset at)
    {
        var before = LatestStart(at);
        var after = before.AddMinutes(EveryMinutes);
        return at - before <= after - at ? before : after;
    }

    /// <summary>
    /// The schedule in words, for the page and the log: "every hour on the hour", then with a
    /// daylight rule ", in daylight: from 120 minutes after sunrise to 30 minutes before sunset at IO91lk".
    /// </summary>
    public string Describe() => EveryMinutes switch
    {
        60 when Anchor.Minute == 0 => "every hour on the hour",
        60 => string.Create(CultureInfo.InvariantCulture, $"every hour at {Anchor.Minute} minutes past"),
        MinutesPerDay => string.Create(CultureInfo.InvariantCulture, $"once a day at {Anchor:HH:mm} UTC"),
        _ => string.Create(CultureInfo.InvariantCulture, $"every {EveryMinutes} minutes from {Anchor:HH:mm} UTC"),
    } + (Daylight is { } d ? ", in daylight: " + d.Describe() : "");
}

/// <summary>
/// When a web SDR is listened to: a few of the day's slots, spread evenly, each from a little
/// before its start to a little after, so as to stay inside a public receiver's daily allowance.
/// </summary>
public static class ListeningWindow
{
    /// <summary>
    /// The <paramref name="perDay"/> slots a web SDR listens to, spread evenly through the day
    /// starting with the anchor (8 of 24 hourly slots from 00:00 is every 3 hours from 00:00),
    /// sorted by time of day. Every slot, if there are no more than <paramref name="perDay"/>.
    /// </summary>
    public static IReadOnlyList<TimeOnly> WebSdrSlots(SlotSchedule schedule, int perDay)
    {
        var all = schedule.FromAnchor;
        int n = Math.Clamp(perDay, 1, all.Count);
        return [.. Enumerable.Range(0, n).Select(i => all[i * all.Count / n]).Order()];
    }

    /// <summary>
    /// The slots a web SDR listens to on a UTC day: <paramref name="perDay"/> of the day's slots,
    /// spread evenly. Without a daylight rule they are <see cref="WebSdrSlots"/>, the same every
    /// day; with one they are spread over that day's daylight slots, starting with its first.
    /// Every slot of the day, if there are no more than <paramref name="perDay"/>.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> WebSdrSlotsOn(SlotSchedule schedule, int perDay, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Daylight is null)
        {
            return [.. WebSdrSlots(schedule, perDay).Select(t => new DateTimeOffset(day.ToDateTime(t, DateTimeKind.Utc)))];
        }
        var active = schedule.ActiveOn(day);
        if (active.Count == 0)
        {
            return [];
        }
        int n = Math.Clamp(perDay, 1, active.Count);
        return [.. Enumerable.Range(0, n).Select(i => active[i * active.Count / n])];
    }

    /// <summary>
    /// The web SDR's window in progress at <paramref name="now"/>, or else the next one: from
    /// <see cref="ReceiverConfig.WebSdrBefore"/> before one of <see cref="WebSdrSlotsOn"/> to
    /// <see cref="ReceiverConfig.WebSdrAfter"/> after it.
    /// </summary>
    public static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot) Next(DateTimeOffset now, SlotSchedule schedule, int perDay)
    {
        var utc = now.ToUniversalTime();
        var today = DateOnly.FromDateTime(utc.UtcDateTime);
        for (int d = -1; d <= SlotTimetable.SearchDays; d++)
        {
            foreach (var start in WebSdrSlotsOn(schedule, perDay, today.AddDays(d)))
            {
                var closes = start + ReceiverConfig.WebSdrAfter;
                if (utc < closes)
                {
                    return (start - ReceiverConfig.WebSdrBefore, closes, start);
                }
            }
        }
        throw new InvalidOperationException("no slot in daylight for more than a year");
    }

    /// <summary>
    /// A sound card's window around the slot in progress at <paramref name="now"/>, or else the
    /// next that runs: the same as a web SDR's, from <see cref="ReceiverConfig.WebSdrBefore"/>
    /// before the slot to <see cref="ReceiverConfig.WebSdrAfter"/> after, but around every slot
    /// that runs. A sound card listens all the time; this is when the hooks run. Null when no slot
    /// runs in the year ahead.
    /// </summary>
    public static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot)? SoundCard(DateTimeOffset now, SlotSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var timetable = schedule.Timetable;
        var utc = now.ToUniversalTime();
        if (timetable.ActiveAtOrBefore(utc) is { } latest && utc < latest + ReceiverConfig.WebSdrAfter)
        {
            return (latest - ReceiverConfig.WebSdrBefore, latest + ReceiverConfig.WebSdrAfter, latest);
        }
        return timetable.NextActiveAtOrAfter(utc) is { } next
            ? (next - ReceiverConfig.WebSdrBefore, next + ReceiverConfig.WebSdrAfter, next)
            : null;
    }

    /// <summary>
    /// The window in progress at <paramref name="now"/>, or else the next one, for slots at
    /// <paramref name="slots"/> each day. Its opening is at or before <paramref name="now"/>
    /// exactly when the web SDR should be open now.
    /// </summary>
    public static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot) Next(DateTimeOffset now, IReadOnlyList<TimeOnly> slots)
    {
        var utc = now.ToUniversalTime();
        foreach (var start in Starts(utc, slots))
        {
            var closes = start + ReceiverConfig.WebSdrAfter;
            if (utc < closes)
            {
                return (start - ReceiverConfig.WebSdrBefore, closes, start);
            }
        }
        throw new InvalidOperationException("unreachable: tomorrow's windows close after now");
    }

    /// <summary>The first of <paramref name="slots"/> (times of day) that starts at or after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextStart(DateTimeOffset now, IReadOnlyList<TimeOnly> slots)
    {
        var utc = now.ToUniversalTime();
        return Starts(utc, slots).First(s => s >= utc);
    }

    /// <summary>Every slot from yesterday to tomorrow, in order.</summary>
    private static IEnumerable<DateTimeOffset> Starts(DateTimeOffset utc, IReadOnlyList<TimeOnly> slots)
    {
        var today = DateOnly.FromDateTime(utc.UtcDateTime);
        foreach (var day in new[] { today.AddDays(-1), today, today.AddDays(1) })
        {
            foreach (var slot in slots.Order())
            {
                yield return new DateTimeOffset(day.ToDateTime(slot), TimeSpan.Zero);
            }
        }
    }

    /// <summary>Times of day as a list for a person: "00:00, 03:00 and 06:00 UTC".</summary>
    public static string Times(IReadOnlyList<TimeOnly> slots)
    {
        var words = slots.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)).ToList();
        return (words.Count == 1 ? words[0] : string.Join(", ", words[..^1]) + " and " + words[^1]) + " UTC";
    }

    /// <summary>
    /// What the web SDR listens to on a day, in words, for the log: the same as
    /// <see cref="Describe(SlotSchedule, IReadOnlyList{TimeOnly})"/> without a daylight rule, and
    /// with one, which of that day's daylight slots.
    /// </summary>
    public static string Describe(SlotSchedule schedule, int perDay, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Daylight is null)
        {
            return Describe(schedule, WebSdrSlots(schedule, perDay));
        }
        var active = schedule.ActiveOn(day);
        var listened = WebSdrSlotsOn(schedule, perDay, day);
        string which = active.Count == 0 ? "no slots, as none is in daylight"
            : listened.Count == active.Count ? (active.Count == 1 ? $"the one daylight slot, at {SlotTimetable.Times(listened)}" : $"all {active.Count} daylight slots, at {SlotTimetable.Times(listened)}")
            : $"{listened.Count} of the {active.Count} daylight slots, at {SlotTimetable.Times(listened)}";
        return string.Create(CultureInfo.InvariantCulture,
            $"on {day:yyyy-MM-dd} the web SDR listens to {which}, from {ReceiverConfig.WebSdrBefore.TotalMinutes:F0} minutes before each to {ReceiverConfig.WebSdrAfter.TotalMinutes:F0} after, to stay inside its listening allowance of about 3 hours a day");
    }

    /// <summary>What the web SDR listens to, in words, for the page and the log.</summary>
    public static string Describe(SlotSchedule schedule, IReadOnlyList<TimeOnly> slots) => string.Create(CultureInfo.InvariantCulture,
        $"the web SDR listens to {(slots.Count == schedule.SlotsPerDay ? (slots.Count == 1 ? "the one slot" : $"all {slots.Count} slots") : $"{slots.Count} of the {schedule.SlotsPerDay} slots")} a day, at {Times(slots)}, "
        + $"from {ReceiverConfig.WebSdrBefore.TotalMinutes:F0} minutes before each to {ReceiverConfig.WebSdrAfter.TotalMinutes:F0} after, to stay inside its listening allowance of about 3 hours a day");
}
