using System.Collections.Concurrent;
using System.Globalization;

namespace Mailcast.Core;

/// <summary>
/// The hours of daylight a head end sends in: a slot runs only if it starts between
/// <see cref="AfterSunriseMinutes"/> after sunrise and <see cref="BeforeSunsetMinutes"/> before
/// sunset at <see cref="Locator"/>. 40 m NVIS only carries over UK paths in daylight; on
/// 2026-10-05 GB7RDG's slots an hour after sunrise and twenty minutes after sunset were heard by
/// nobody, and those from three hours after sunrise to two before sunset were.
/// </summary>
/// <remarks>
/// Each day's window is worked out from the sun alone (<see cref="Solar"/>), in UTC. A day of
/// polar day counts as daylight throughout, and one of polar night, or one shorter than the two
/// offsets together, has no slots.
/// </remarks>
public sealed class DaylightRule : IEquatable<DaylightRule>
{
    /// <summary>The fewest minutes either offset may be: four hours the other way.</summary>
    public const int LeastMinutes = -240;

    /// <summary>The most minutes either offset may be: twelve hours.</summary>
    public const int MostMinutes = 720;

    private readonly ConcurrentDictionary<DateOnly, DaylightWindow> _windows = new();

    /// <summary>Creates a rule. Throws <see cref="ArgumentException"/> for a locator or an offset that cannot be.</summary>
    public DaylightRule(string locator, int afterSunriseMinutes, int beforeSunsetMinutes)
    {
        if (!Maidenhead.TryParse(locator, out double lat, out double lon))
        {
            throw new ArgumentException($"'{locator}' is not a 4 or 6 character Maidenhead locator such as IO91lk.", nameof(locator));
        }
        if (afterSunriseMinutes is < LeastMinutes or > MostMinutes || beforeSunsetMinutes is < LeastMinutes or > MostMinutes)
        {
            throw new ArgumentException($"The minutes after sunrise and before sunset must each be {LeastMinutes} to {MostMinutes}.", nameof(afterSunriseMinutes));
        }
        Locator = locator.Length == 6 ? locator[..4].ToUpperInvariant() + locator[4..].ToLowerInvariant() : locator.ToUpperInvariant();
        AfterSunriseMinutes = afterSunriseMinutes;
        BeforeSunsetMinutes = beforeSunsetMinutes;
        Latitude = lat;
        Longitude = lon;
    }

    /// <summary>GB7RDG's: from 2 hours after sunrise to 30 minutes before sunset, near Reading.</summary>
    public static DaylightRule Gb7rdg { get; } = new("IO91lk", 120, 30);

    /// <summary>Where the sun is reckoned, as a Maidenhead locator: IO91lk.</summary>
    public string Locator { get; }

    /// <summary>The first slot may start this many minutes after sunrise.</summary>
    public int AfterSunriseMinutes { get; }

    /// <summary>The last slot may start this many minutes before sunset.</summary>
    public int BeforeSunsetMinutes { get; }

    /// <summary>The locator's centre, degrees north.</summary>
    public double Latitude { get; }

    /// <summary>The locator's centre, degrees east.</summary>
    public double Longitude { get; }

    /// <summary>The window of the day whose solar noon falls on <paramref name="day"/> (UTC).</summary>
    public DaylightWindow WindowOn(DateOnly day) => _windows.GetOrAdd(day, d =>
    {
        var sun = Solar.SunriseSunset(d, Latitude, Longitude);
        if (sun.Kind != SunDay.RisesAndSets)
        {
            return new DaylightWindow(d, sun, null, null);
        }
        var opens = sun.Sunrise!.Value.AddMinutes(AfterSunriseMinutes);
        var closes = sun.Sunset!.Value.AddMinutes(-BeforeSunsetMinutes);
        return opens <= closes ? new DaylightWindow(d, sun, opens, closes) : new DaylightWindow(d, sun, null, null);
    });

    /// <summary>
    /// Whether a slot starting at <paramref name="slot"/> is in daylight. Each solar day's window
    /// is checked, the day before and after too, so that a place far from Greenwich, whose sunset
    /// falls on the next UTC day, works as well.
    /// </summary>
    public bool Allows(DateTimeOffset slot)
    {
        var day = DateOnly.FromDateTime(slot.UtcDateTime);
        return WindowOn(day).Contains(slot) || WindowOn(day.AddDays(-1)).Contains(slot) || WindowOn(day.AddDays(1)).Contains(slot);
    }

    /// <summary>The rule in words: "from 120 minutes after sunrise to 30 before sunset at IO91lk".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"from {Minutes(AfterSunriseMinutes, "after sunrise", "before sunrise")} to {Minutes(BeforeSunsetMinutes, "before sunset", "after sunset")} at {Locator}");

    private static string Minutes(int minutes, string positive, string negative) => minutes switch
    {
        0 => positive.EndsWith("sunrise", StringComparison.Ordinal) ? "sunrise" : "sunset",
        > 0 => string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes {positive}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{-minutes} minutes {negative}"),
    };

    /// <inheritdoc />
    public bool Equals(DaylightRule? other) =>
        other is not null && string.Equals(Locator, other.Locator, StringComparison.OrdinalIgnoreCase)
        && AfterSunriseMinutes == other.AfterSunriseMinutes && BeforeSunsetMinutes == other.BeforeSunsetMinutes;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DaylightRule);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Locator.ToUpperInvariant(), AfterSunriseMinutes, BeforeSunsetMinutes);

    /// <inheritdoc />
    public override string ToString() => Describe();
}

/// <summary>One day's daylight window.</summary>
/// <param name="Day">The UTC day.</param>
/// <param name="Sun">Sunrise and sunset.</param>
/// <param name="Opens">The first time a slot may start, or null for none (or all day, in polar day).</param>
/// <param name="Closes">The last time a slot may start, likewise.</param>
public sealed record DaylightWindow(DateOnly Day, SunTimes Sun, DateTimeOffset? Opens, DateTimeOffset? Closes)
{
    /// <summary>The sun never sets: every slot of the UTC day is in daylight.</summary>
    public bool AllDay => Sun.Kind == SunDay.AlwaysUp;

    /// <summary>No slot of the day is in daylight: polar night, or a day shorter than the offsets.</summary>
    public bool None => !AllDay && Opens is null;

    /// <summary>Whether a slot starting at <paramref name="slot"/> is inside the window, its edges included.</summary>
    public bool Contains(DateTimeOffset slot) => AllDay
        ? DateOnly.FromDateTime(slot.UtcDateTime) == Day
        : Opens is { } opens && Closes is { } closes && slot >= opens && slot <= closes;
}

/// <summary>Daylight settings as a configuration file gives them; GB7RDG's by default.</summary>
public sealed record DaylightSettings
{
    /// <summary>Where the sun is reckoned, a 4 or 6 character Maidenhead locator.</summary>
    public string Locator { get; init; } = "IO91lk";

    /// <summary>The first slot may start this long after sunrise.</summary>
    public int AfterSunriseMinutes { get; init; } = 120;

    /// <summary>The last slot may start this long before sunset.</summary>
    public int BeforeSunsetMinutes { get; init; } = 30;

    /// <summary>What is wrong with the settings, in a sentence, or null when they are fine.</summary>
    public string? Problem(int everyMinutes, TimeOnly anchor)
    {
        if (Locator is null || !Maidenhead.TryParse(Locator, out _, out _))
        {
            return $"\"locator\" '{Locator}' is not a 4 or 6 character Maidenhead locator such as IO91lk";
        }
        if (AfterSunriseMinutes is < DaylightRule.LeastMinutes or > DaylightRule.MostMinutes || BeforeSunsetMinutes is < DaylightRule.LeastMinutes or > DaylightRule.MostMinutes)
        {
            return $"\"afterSunriseMinutes\" and \"beforeSunsetMinutes\" must each be {DaylightRule.LeastMinutes} to {DaylightRule.MostMinutes}";
        }
        if (everyMinutes >= 1 && 1440 % everyMinutes == 0 && !new SlotTimetable(anchor, everyMinutes, ToRule()).HasActiveSlotInAYear())
        {
            return $"no slot in a whole year starts in daylight at {Locator} with these offsets";
        }
        return null;
    }

    /// <summary>The rule. Throws <see cref="ArgumentException"/> if <see cref="Problem"/> has something to say.</summary>
    public DaylightRule ToRule() => new(Locator, AfterSunriseMinutes, BeforeSunsetMinutes);
}

/// <summary>
/// When a head end's slots are: one starts at <see cref="Anchor"/> UTC and then every
/// <see cref="EveryMinutes"/>, round the clock, and with a <see cref="Daylight"/> rule only those
/// that start in its window run. The same in the head end, in the directory it sends, and in
/// the receiver.
/// </summary>
/// <param name="Anchor">One slot's start, UTC.</param>
/// <param name="EveryMinutes">Minutes from one slot to the next; it divides a day.</param>
/// <param name="Daylight">The daylight rule, or null for every slot.</param>
public sealed record SlotTimetable(TimeOnly Anchor, int EveryMinutes, DaylightRule? Daylight = null)
{
    /// <summary>How far ahead or back a search for a slot in daylight looks: more than a year, so it always finds one if any day has one.</summary>
    public const int SearchDays = 400;

    private const int MinutesPerDay = 1440;

    /// <summary>From one slot to the next.</summary>
    public int EveryMinutes { get; init; } = EveryMinutes >= 1 && MinutesPerDay % EveryMinutes == 0
        ? EveryMinutes
        : throw new ArgumentException("A slot interval must divide a day.", nameof(EveryMinutes));

    /// <summary>How many slots there are in a day, in daylight or not.</summary>
    public int SlotsPerDay => MinutesPerDay / EveryMinutes;

    /// <summary>The latest slot start at or before <paramref name="t"/>, in daylight or not.</summary>
    public DateTimeOffset SlotAtOrBefore(DateTimeOffset t)
    {
        long every = TimeSpan.FromMinutes(EveryMinutes).Ticks;
        var first = new DateTimeOffset(t.UtcDateTime.Date, TimeSpan.Zero) + Anchor.ToTimeSpan();
        long k = (long)Math.Floor((t.UtcTicks - first.UtcTicks) / (double)every);
        return first.AddTicks(every * k);
    }

    /// <summary>Every slot start on a UTC day, in daylight or not, earliest first.</summary>
    public IReadOnlyList<DateTimeOffset> SlotsOn(DateOnly day)
    {
        var midnight = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        int firstMinute = (int)(Anchor.ToTimeSpan().TotalMinutes % EveryMinutes);
        return [.. Enumerable.Range(0, SlotsPerDay).Select(i => midnight.AddMinutes(firstMinute + ((double)i * EveryMinutes)))];
    }

    /// <summary>Whether the slot starting at <paramref name="slot"/> runs: always, without a daylight rule.</summary>
    public bool IsActive(DateTimeOffset slot) => Daylight?.Allows(slot) ?? true;

    /// <summary>The slots that run on a UTC day, earliest first.</summary>
    public IReadOnlyList<DateTimeOffset> ActiveSlotsOn(DateOnly day) =>
        Daylight is null ? SlotsOn(day) : [.. SlotsOn(day).Where(Daylight.Allows)];

    /// <summary>The first slot that runs starting at or after <paramref name="t"/>, or null if none does within <see cref="SearchDays"/>.</summary>
    public DateTimeOffset? NextActiveAtOrAfter(DateTimeOffset t)
    {
        var day = DateOnly.FromDateTime(t.UtcDateTime);
        for (int d = 0; d <= SearchDays; d++)
        {
            foreach (var slot in ActiveSlotsOn(day.AddDays(d)))
            {
                if (slot >= t)
                {
                    return slot;
                }
            }
        }
        return null;
    }

    /// <summary>The first slot that runs starting after <paramref name="t"/>, or null if none does within <see cref="SearchDays"/>.</summary>
    public DateTimeOffset? NextActiveAfter(DateTimeOffset t) => NextActiveAtOrAfter(t.AddTicks(1));

    /// <summary>The latest slot that runs starting at or before <paramref name="t"/>, or null if none did within <see cref="SearchDays"/>.</summary>
    public DateTimeOffset? ActiveAtOrBefore(DateTimeOffset t)
    {
        var day = DateOnly.FromDateTime(t.UtcDateTime);
        for (int d = 0; d <= SearchDays; d++)
        {
            var slots = ActiveSlotsOn(day.AddDays(-d));
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                if (slots[i] <= t)
                {
                    return slots[i];
                }
            }
        }
        return null;
    }

    /// <summary>Whether any slot in the year from 1 January 2026 runs: false only for offsets no day is long enough for.</summary>
    public bool HasActiveSlotInAYear()
    {
        var start = new DateOnly(2026, 1, 1);
        for (int d = 0; d < 366; d++)
        {
            if (ActiveSlotsOn(start.AddDays(d)).Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Slot times as a list for a person: "09:00, 10:00 and 11:00 UTC", or "none".</summary>
    public static string Times(IEnumerable<DateTimeOffset> slots)
    {
        var words = slots.Select(t => t.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)).ToList();
        return words.Count switch
        {
            0 => "none",
            1 => words[0] + " UTC",
            _ => string.Join(", ", words[..^1]) + " and " + words[^1] + " UTC",
        };
    }

    /// <summary>
    /// The fields the directory carries the timetable in, after the first entry's title:
    /// <c>slots=HH:mm/EVERY</c> and, with a daylight rule, <c>daylight=LOCATOR/AFTER/BEFORE</c>.
    /// </summary>
    public IReadOnlyList<string> ToFields()
    {
        var fields = new List<string> { string.Create(CultureInfo.InvariantCulture, $"slots={Anchor:HH:mm}/{EveryMinutes}") };
        if (Daylight is { } d)
        {
            fields.Add(string.Create(CultureInfo.InvariantCulture, $"daylight={d.Locator}/{d.AfterSunriseMinutes}/{d.BeforeSunsetMinutes}"));
        }
        return fields;
    }

    /// <summary>
    /// Reads <see cref="ToFields"/>' fields from among others, which are ignored. Null when there
    /// is no <c>slots=</c> field or it or a <c>daylight=</c> field cannot be read, so a timetable
    /// a later head end writes differently is ignored rather than misread.
    /// </summary>
    public static SlotTimetable? FromFields(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        string? slots = null;
        string? daylight = null;
        foreach (var field in fields)
        {
            if (field.StartsWith("slots=", StringComparison.Ordinal))
            {
                slots ??= field["slots=".Length..];
            }
            else if (field.StartsWith("daylight=", StringComparison.Ordinal))
            {
                daylight ??= field["daylight=".Length..];
            }
        }
        if (slots is null)
        {
            return null;
        }
        var s = slots.Split('/');
        if (s.Length != 2
            || !TimeOnly.TryParseExact(s[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor)
            || !int.TryParse(s[1], NumberStyles.None, CultureInfo.InvariantCulture, out int every)
            || every < 1 || MinutesPerDay % every != 0)
        {
            return null;
        }
        DaylightRule? rule = null;
        if (daylight is not null)
        {
            var d = daylight.Split('/');
            if (d.Length != 3
                || !int.TryParse(d[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int after)
                || !int.TryParse(d[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int before))
            {
                return null;
            }
            try
            {
                rule = new DaylightRule(d[0], after, before);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return new SlotTimetable(anchor, every, rule);
    }
}
