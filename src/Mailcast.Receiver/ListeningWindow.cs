using System.Globalization;

namespace Mailcast.Receiver;

/// <summary>GB7RDG's slots: one starts at <paramref name="Anchor"/> UTC and then every <paramref name="EveryMinutes"/>, round the clock.</summary>
/// <param name="Anchor">One slot's start, UTC.</param>
/// <param name="EveryMinutes">Minutes from one slot's start to the next; it divides a day.</param>
public sealed record SlotSchedule(TimeOnly Anchor, int EveryMinutes)
{
    private const int MinutesPerDay = 1440;

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

    /// <summary>The schedule in words, for the page and the log: "every hour on the hour".</summary>
    public string Describe() => EveryMinutes switch
    {
        60 when Anchor.Minute == 0 => "every hour on the hour",
        60 => string.Create(CultureInfo.InvariantCulture, $"every hour at {Anchor.Minute} minutes past"),
        MinutesPerDay => string.Create(CultureInfo.InvariantCulture, $"once a day at {Anchor:HH:mm} UTC"),
        _ => string.Create(CultureInfo.InvariantCulture, $"every {EveryMinutes} minutes from {Anchor:HH:mm} UTC"),
    };
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

    /// <summary>What the web SDR listens to, in words, for the page and the log.</summary>
    public static string Describe(SlotSchedule schedule, IReadOnlyList<TimeOnly> slots) => string.Create(CultureInfo.InvariantCulture,
        $"the web SDR listens to {(slots.Count == schedule.SlotsPerDay ? (slots.Count == 1 ? "the one slot" : $"all {slots.Count} slots") : $"{slots.Count} of the {schedule.SlotsPerDay} slots")} a day, at {Times(slots)}, "
        + $"from {ReceiverConfig.WebSdrBefore.TotalMinutes:F0} minutes before each to {ReceiverConfig.WebSdrAfter.TotalMinutes:F0} after, to stay inside its listening allowance of about 3 hours a day");
}
