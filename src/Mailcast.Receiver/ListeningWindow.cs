using System.Globalization;
using Packet.Mailcast;

namespace Mailcast.Receiver;

/// <summary>
/// One station's slots: one starts at <paramref name="Anchor"/> UTC and then every
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

/// <summary>A station whose slots this receiver knows (issues #178 and #144).</summary>
/// <param name="Station">Its callsign.</param>
/// <param name="Timetable">Its slots.</param>
/// <param name="FrequencyHz">Where its signal is centred.</param>
/// <param name="Sideband">Its sideband.</param>
/// <param name="Source">Where the slots come from, in words: "its own directory", "an advert" or "the config file".</param>
/// <param name="Followed">Whether this receiver listens at its slots.</param>
/// <param name="NotFollowedBecause">Why not, in words, when not.</param>
/// <param name="Locator">Its Maidenhead locator, when a directory or advert gave one.</param>
/// <param name="DialHz">The USB dial it is heard on, when it is followed.</param>
/// <param name="How">How it is heard, when followed: in the passband already, or by retuning.</param>
/// <param name="IfHz">When heard through a downconverting LNB, the intermediate frequency its signal is at; null otherwise.</param>
public sealed record ListenedStation(string Station, SlotTimetable Timetable, long FrequencyHz, Sideband Sideband, string Source, bool Followed, string? NotFollowedBecause,
    string? Locator = null, double? DialHz = null, HowHeard? How = null, long? IfHz = null)
{
    /// <summary>Where the signal is in this receiver's front end, Hz: the intermediate frequency behind an LNB, else the station's own frequency. Dials and audio positions are worked out from this; what is shown is <see cref="FrequencyHz"/>.</summary>
    public double HeardAtHz => IfHz ?? FrequencyHz;

    /// <summary>Where the station is on the air, as this receiver knows it.</summary>
    public StationOnAir OnTheAir => new(Station, FrequencyHz, Sideband, Locator, Source);
}

/// <summary>
/// The slots this receiver follows (issue #144): every followed station's, each with its own
/// timetable. With one station, every answer is exactly that station's
/// <see cref="SlotSchedule"/>'s. With none, there are no slots at all.
/// </summary>
public sealed class FollowedSlots
{
    /// <summary>The slots of the given stations, in order; there may be none.</summary>
    public FollowedSlots(IReadOnlyList<(string Station, SlotSchedule Schedule)> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        Stations = stations;
    }

    /// <summary>No station's slots.</summary>
    public static FollowedSlots None { get; } = new([]);

    /// <summary>One station's slots.</summary>
    public static FollowedSlots One(SlotSchedule schedule, string station)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return new([(station, schedule)]);
    }

    /// <summary>The stations followed, each with its slots.</summary>
    public IReadOnlyList<(string Station, SlotSchedule Schedule)> Stations { get; }

    /// <summary>Whether only one station is followed.</summary>
    public bool OneStation => Stations.Count == 1;

    /// <summary>The first station's slots: the only one's, when <see cref="OneStation"/>.</summary>
    public SlotSchedule First => Stations[0].Schedule;

    /// <summary>
    /// The station whose slot starts at <paramref name="slot"/> (whether it runs or not), the
    /// first listed when two share it; null when none does.
    /// </summary>
    public string? StationAt(DateTimeOffset slot)
    {
        var utc = slot.ToUniversalTime();
        foreach (var (station, schedule) in Stations)
        {
            if (schedule.LatestStart(utc) == utc)
            {
                return station;
            }
        }
        return null;
    }

    /// <summary>Every station's slots that run on a UTC day, earliest first, each start once.</summary>
    public IReadOnlyList<DateTimeOffset> ActiveOn(DateOnly day) =>
        OneStation ? First.ActiveOn(day) : [.. Stations.SelectMany(s => s.Schedule.ActiveOn(day)).Distinct().Order()];

    /// <summary>The first slot of any station that runs, starting at or after <paramref name="t"/>.</summary>
    public DateTimeOffset? NextActiveAtOrAfter(DateTimeOffset t) => Earliest(s => s.Timetable.NextActiveAtOrAfter(t));

    /// <summary>The first slot of any station that runs, starting after <paramref name="t"/>.</summary>
    public DateTimeOffset? NextActiveAfter(DateTimeOffset t) => Earliest(s => s.Timetable.NextActiveAfter(t));

    /// <summary>The latest slot of any station that ran, starting at or before <paramref name="t"/>.</summary>
    public DateTimeOffset? ActiveAtOrBefore(DateTimeOffset t) => Latest(s => s.Timetable.ActiveAtOrBefore(t));

    /// <summary>The first slot that runs starting at or after <paramref name="now"/> (<see cref="SlotSchedule.NextActiveStart"/>).</summary>
    public DateTimeOffset? NextActiveStart(DateTimeOffset now) => Stations.Count == 0 ? null : Stations.Min(s => s.Schedule.NextActiveStart(now));

    /// <summary>The latest slot that ran starting at or before <paramref name="now"/>, if any.</summary>
    public DateTimeOffset? LatestActiveStart(DateTimeOffset now) => Latest(s => s.LatestActiveStart(now));

    /// <summary>The latest slot of any station, run or not, starting at or before <paramref name="at"/>.</summary>
    public DateTimeOffset LatestStart(DateTimeOffset at) => Stations.Max(s => s.Schedule.LatestStart(at));

    /// <summary>The slot start of any station nearest <paramref name="at"/>, before or after it; the earlier on a tie.</summary>
    public DateTimeOffset NearestStart(DateTimeOffset at) =>
        Stations.Select(s => s.Schedule.NearestStart(at)).OrderBy(t => (t - at).Duration()).ThenBy(t => t).First();

    /// <summary>
    /// The slots in words, for the page and the log: one station's as <see cref="SlotSchedule.Describe"/>,
    /// several as "GB7RDG every hour on the hour; G8BPQ every hour at 30 minutes past".
    /// </summary>
    public string Describe() => Stations.Count == 0 ? "no station" : OneStation ? First.Describe() : string.Join("; ", Stations.Select(s => $"{s.Station} {s.Schedule.Describe()}"));

    private DateTimeOffset? Earliest(Func<SlotSchedule, DateTimeOffset?> each) =>
        Stations.Select(s => each(s.Schedule)).Where(t => t is not null).DefaultIfEmpty(null).Min();

    private DateTimeOffset? Latest(Func<SlotSchedule, DateTimeOffset?> each) =>
        Stations.Select(s => each(s.Schedule)).Where(t => t is not null).DefaultIfEmpty(null).Max();
}

/// <summary>
/// When a web SDR is listened to: every daylight slot that fits in its allowance, each from a little before its
/// start to a little after, so as to stay inside a public receiver's daily allowance.
/// </summary>
public static class ListeningWindow
{
    /// <summary>
    /// The slots a web SDR listens to without a daylight rule: as many as fit in its allowance
    /// (<see cref="ReceiverConfig.MostWebSdrSlotsPerDay"/>), spread evenly through the day starting
    /// with the anchor (12 of 24 hourly slots from 00:00 is every other hour from 00:00), sorted by
    /// time of day. With no daylight there is no morning or afternoon to prefer. Every slot, if
    /// there are no more than that.
    /// </summary>
    public static IReadOnlyList<TimeOnly> WebSdrSlots(SlotSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var all = schedule.FromAnchor;
        int n = Math.Min(ReceiverConfig.MostWebSdrSlotsPerDay, all.Count);
        return [.. Enumerable.Range(0, n).Select(i => all[i * all.Count / n]).Order()];
    }

    /// <summary>
    /// The slots a web SDR listens to on a UTC day. Without a daylight rule they are
    /// <see cref="WebSdrSlots"/>, the same every day. With one, they are every daylight slot that
    /// fits in its allowance, which is all of them for most of the year, and near midsummer, when
    /// there are more, the latest that fit.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> WebSdrSlotsOn(FollowedSlots followed, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(followed);
        if (followed.OneStation)
        {
            return WebSdrSlotsOn(followed.First, day);
        }
        // Several stations share the one allowance, as evenly as their slots allow: each gets an
        // equal part (any a station cannot use goes to the others), and picks within it by the
        // one-station rule, so no station is left out by the way their slots interleave.
        var active = followed.Stations.Select(st => st.Schedule.ActiveOn(day)).ToList();
        var quota = new int[active.Count];
        int left = ReceiverConfig.MostWebSdrSlotsPerDay;
        while (left > 0)
        {
            var wanting = Enumerable.Range(0, active.Count).Where(i => quota[i] < active[i].Count).ToList();
            if (wanting.Count == 0)
            {
                break;
            }
            int share = Math.Max(1, left / wanting.Count);
            foreach (int i in wanting)
            {
                int add = Math.Min(Math.Min(share, active[i].Count - quota[i]), left);
                quota[i] += add;
                left -= add;
            }
        }
        var picked = new List<DateTimeOffset>();
        for (int i = 0; i < active.Count; i++)
        {
            var schedule = followed.Stations[i].Schedule;
            int n = quota[i];
            if (n == active[i].Count)
            {
                picked.AddRange(active[i]);
            }
            else if (schedule.Daylight is not null)
            {
                picked.AddRange(active[i].Skip(active[i].Count - n));
            }
            else
            {
                picked.AddRange(Enumerable.Range(0, n).Select(k => active[i][k * active[i].Count / n]));
            }
        }
        return [.. picked.Distinct().Order()];
    }

    /// <summary>One station's web SDR slots on a day (see the overload for several).</summary>
    public static IReadOnlyList<DateTimeOffset> WebSdrSlotsOn(SlotSchedule schedule, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Daylight is null)
        {
            return [.. WebSdrSlots(schedule).Select(t => new DateTimeOffset(day.ToDateTime(t, DateTimeKind.Utc)))];
        }
        var active = schedule.ActiveOn(day);
        // When there are more daylight slots than fit in the allowance (near midsummer), the
        // earliest morning ones are left out, not the late afternoon ones: late afternoon is often
        // the best time for 40 m to reach stations near GB7RDG, and an early morning slot hears little.
        int n = Math.Min(ReceiverConfig.MostWebSdrSlotsPerDay, active.Count);
        return [.. active.Skip(active.Count - n)];
    }

    /// <summary>
    /// The web SDR's window in progress at <paramref name="now"/>, or else the next one: from
    /// <see cref="ReceiverConfig.WebSdrBefore"/> before one of <see cref="WebSdrSlotsOn"/> to
    /// <see cref="ReceiverConfig.WebSdrAfter"/> after it.
    /// </summary>
    public static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot) Next(DateTimeOffset now, FollowedSlots schedule)
    {
        var utc = now.ToUniversalTime();
        var today = DateOnly.FromDateTime(utc.UtcDateTime);
        for (int d = -1; d <= SlotTimetable.SearchDays; d++)
        {
            foreach (var start in WebSdrSlotsOn(schedule, today.AddDays(d)))
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
    public static (DateTimeOffset Opens, DateTimeOffset Closes, DateTimeOffset Slot)? SoundCard(DateTimeOffset now, FollowedSlots schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var timetable = schedule;
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
    public static string Describe(FollowedSlots followed, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(followed);
        if (followed.OneStation)
        {
            return Describe(followed.First, day);
        }
        var active = followed.ActiveOn(day);
        var listened = WebSdrSlotsOn(followed, day);
        var skipped = active.Except(listened).ToList();
        string which = active.Count == 0 ? "no slots, as none runs today"
            : skipped.Count == 0 ? $"all {active.Count} slots of the {followed.Stations.Count} stations it follows, at {SlotTimetable.Times(listened)}"
            : $"{listened.Count} of the {active.Count} slots of the {followed.Stations.Count} stations it follows, at {SlotTimetable.Times(listened)}, and skips {SlotTimetable.Times(skipped)}";
        return string.Create(CultureInfo.InvariantCulture,
            $"on {day:yyyy-MM-dd} the web SDR listens to {which}, from {ReceiverConfig.WebSdrBefore.TotalMinutes:F0} minutes before each to {ReceiverConfig.WebSdrAfter.TotalMinutes:F0} after, to stay inside its listening allowance of about 3 hours a day");
    }

    /// <summary>One station's web SDR day in words (see the overload for several).</summary>
    public static string Describe(SlotSchedule schedule, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Daylight is null)
        {
            return Describe(schedule, WebSdrSlots(schedule));
        }
        var active = schedule.ActiveOn(day);
        var listened = WebSdrSlotsOn(schedule, day);
        var skipped = active.Except(listened).ToList();
        string which = active.Count == 0 ? "no slots, as none is in daylight"
            : skipped.Count == 0 ? (active.Count == 1 ? $"the one daylight slot, at {SlotTimetable.Times(listened)}" : $"all {active.Count} daylight slots, at {SlotTimetable.Times(listened)}")
            : $"{listened.Count} of the {active.Count} daylight slots, at {SlotTimetable.Times(listened)}, and skips {SlotTimetable.Times(skipped)}";
        return string.Create(CultureInfo.InvariantCulture,
            $"on {day:yyyy-MM-dd} the web SDR listens to {which}, from {ReceiverConfig.WebSdrBefore.TotalMinutes:F0} minutes before each to {ReceiverConfig.WebSdrAfter.TotalMinutes:F0} after, to stay inside its listening allowance of about 3 hours a day");
    }

    /// <summary>What the web SDR listens to, in words, for the page and the log.</summary>
    public static string Describe(SlotSchedule schedule, IReadOnlyList<TimeOnly> slots) => string.Create(CultureInfo.InvariantCulture,
        $"the web SDR listens to {(slots.Count == schedule.SlotsPerDay ? (slots.Count == 1 ? "the one slot" : $"all {slots.Count} slots") : $"{slots.Count} of the {schedule.SlotsPerDay} slots")} a day, at {Times(slots)}, {(slots.Count < schedule.SlotsPerDay ? $"and skips the other {schedule.SlotsPerDay - slots.Count}, " : "")}from {ReceiverConfig.WebSdrBefore.TotalMinutes:F0} minutes before each to {ReceiverConfig.WebSdrAfter.TotalMinutes:F0} after, to stay inside its listening allowance of about 3 hours a day");
}
