using System.Collections.Concurrent;
using System.Globalization;

namespace Packet.Mailcast;

/// <summary>
/// The hours of daylight a head end sends in: a slot runs only if it starts between
/// <see cref="AfterSunriseMinutes"/> after sunrise and <see cref="BeforeSunsetMinutes"/> before
/// sunset at <see cref="Locator"/>. 40 m NVIS only carries over UK paths in daylight; on
/// 2026-10-05 M0LTE's slots an hour after sunrise and twenty minutes after sunset were heard by
/// nobody, and those from three hours after sunrise to two before sunset were.
/// </summary>
/// <remarks>
/// <para>
/// Each day's window is worked out from the sun alone (<see cref="Solar"/>), in UTC. A day of
/// polar day counts as daylight throughout, and one of polar night, or one shorter than the two
/// offsets together, has no slots.
/// </para>
/// <para>
/// With a <see cref="Season"/> (issue #273) the window comes from that instead, and the offsets
/// are kept only for receivers from before it. A year of UK 40 m WSPR showed the band does not
/// follow sunrise and sunset by fixed offsets: in winter it opens within half an hour of sunrise
/// and stays open an hour past sunset, and in summer it opens hours after sunrise and stays open
/// until about midnight. The sun is still worked out, for the evening slots and the journal.
/// </para>
/// </remarks>
public sealed class DaylightRule : IEquatable<DaylightRule>
{
    /// <summary>The fewest minutes either offset may be: four hours the other way.</summary>
    public const int LeastMinutes = -240;

    /// <summary>The most minutes either offset may be: twelve hours.</summary>
    public const int MostMinutes = 720;

    private readonly ConcurrentDictionary<DateOnly, DaylightWindow> _windows = new();

    /// <summary>Creates a rule. Throws <see cref="ArgumentException"/> for a locator or an offset that cannot be.</summary>
    public DaylightRule(string locator, int afterSunriseMinutes, int beforeSunsetMinutes, SeasonalWindow? season = null)
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
        Season = season;
        Latitude = lat;
        Longitude = lon;
    }

    /// <summary>Where the sun is reckoned, as a Maidenhead locator: IO91lk.</summary>
    public string Locator { get; }

    /// <summary>The first slot may start this many minutes after sunrise.</summary>
    public int AfterSunriseMinutes { get; }

    /// <summary>The last slot may start this many minutes before sunset.</summary>
    public int BeforeSunsetMinutes { get; }

    /// <summary>The measured window the slots follow instead of the offsets, or null to follow the sun.</summary>
    public SeasonalWindow? Season { get; }

    /// <summary>The locator's centre, degrees north.</summary>
    public double Latitude { get; }

    /// <summary>The locator's centre, degrees east.</summary>
    public double Longitude { get; }

    /// <summary>The window of the day whose solar noon falls on <paramref name="day"/> (UTC).</summary>
    public DaylightWindow WindowOn(DateOnly day) => _windows.GetOrAdd(day, d =>
    {
        var sun = Solar.SunriseSunset(d, Latitude, Longitude);
        if (Season is { } season)
        {
            var midnight = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            return season.MinutesOn(d) is var (open, last)
                ? new DaylightWindow(d, sun, midnight.AddMinutes(open), midnight.AddMinutes(last), Seasonal: true)
                : new DaylightWindow(d, sun, null, null, Seasonal: true);
        }
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

    /// <summary>The rule in words: "from 120 minutes after sunrise to 30 before sunset at IO91lk", or with a season "by month, UTC, ...".</summary>
    public string Describe() => Season is { } season ? season.Describe() : string.Create(CultureInfo.InvariantCulture,
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
        && AfterSunriseMinutes == other.AfterSunriseMinutes && BeforeSunsetMinutes == other.BeforeSunsetMinutes && Equals(Season, other.Season);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DaylightRule);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Locator.ToUpperInvariant(), AfterSunriseMinutes, BeforeSunsetMinutes, Season);

    /// <inheritdoc />
    public override string ToString() => Describe();
}

/// <summary>One day's daylight window.</summary>
/// <param name="Day">The UTC day.</param>
/// <param name="Sun">Sunrise and sunset.</param>
/// <param name="Opens">The first time a slot may start, or null for none (or all day, in polar day).</param>
/// <param name="Closes">The last time a slot may start, likewise; with a season it may be on the next UTC day.</param>
/// <param name="Seasonal">From a <see cref="SeasonalWindow"/> rather than the sun, so polar day does not make it all day.</param>
public sealed record DaylightWindow(DateOnly Day, SunTimes Sun, DateTimeOffset? Opens, DateTimeOffset? Closes, bool Seasonal = false)
{
    /// <summary>The sun never sets: every slot of the UTC day is in daylight.</summary>
    public bool AllDay => !Seasonal && Sun.Kind == SunDay.AlwaysUp;

    /// <summary>No slot of the day is in daylight: polar night, or a day shorter than the offsets.</summary>
    public bool None => !AllDay && Opens is null;

    /// <summary>Whether a slot starting at <paramref name="slot"/> is inside the window, its edges included.</summary>
    public bool Contains(DateTimeOffset slot) => AllDay
        ? DateOnly.FromDateTime(slot.UtcDateTime) == Day
        : Opens is { } opens && Closes is { } closes && slot >= opens && slot <= closes;
}

/// <summary>Daylight settings as a configuration file gives them: the offsets default to the usual 120 and 30 minutes, and the locator has no default, since it is the station's own.</summary>
public sealed record DaylightSettings
{
    /// <summary>Where the sun is reckoned, a 4 or 6 character Maidenhead locator.</summary>
    public string Locator { get; init; } = "";

    /// <summary>The first slot may start this long after sunrise.</summary>
    public int AfterSunriseMinutes { get; init; } = 120;

    /// <summary>The last slot may start this long before sunset.</summary>
    public int BeforeSunsetMinutes { get; init; } = 30;

    /// <summary>
    /// The measured window to follow instead of the offsets (issue #273): twelve months, January
    /// first, each the band's opening and closing on the 15th, UTC, such as <c>"08:30-17:18"</c>.
    /// Left out, the slots follow the sun.
    /// </summary>
    public IReadOnlyList<string>? Season { get; init; }

    /// <summary>How long a slot lasts, for <see cref="Season"/>: the last one ends by the close. Not read from a file; the head end gives its own.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int SlotMinutes { get; init; } = 10;

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
        if (Season is not null && SeasonalWindow.FromSettings(Season, SlotMinutes, out string? seasonProblem) is null)
        {
            return seasonProblem;
        }
        if (everyMinutes >= 1 && 1440 % everyMinutes == 0 && !new SlotTimetable(anchor, everyMinutes, ToRule()).HasActiveSlotInAYear())
        {
            return Season is null
                ? $"no slot in a whole year starts in daylight at {Locator} with these offsets"
                : "no slot in a whole year fits in the \"season\" window";
        }
        return null;
    }

    /// <summary>The rule. Throws <see cref="ArgumentException"/> if <see cref="Problem"/> has something to say.</summary>
    public DaylightRule ToRule() => new(Locator, AfterSunriseMinutes, BeforeSunsetMinutes,
        Season is null ? null : SeasonalWindow.FromSettings(Season, SlotMinutes, out string? problem) ?? throw new ArgumentException(problem, nameof(Season)));
}

/// <summary>
/// A head end's evening slots: after its daylight window, the slots that start from sunset at the
/// daylight rule's locator until <see cref="UntilUtc"/>, for listeners far enough away that the
/// path stays open after dark (40 m from Reading to Sweden, say). They come after the daylight
/// slots, on the same timetable, so the two never overlap, and they never count against the
/// daylight strategy (see <see cref="BroadcastScheduler.PlanEvening"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each solar day's evening runs from its sunset to <see cref="UntilUtc"/> on the UTC day of its
/// solar noon, and never past the next sunrise. A day whose sunset is at or after that time (far
/// west, or far north in summer) has none, and so does one of polar day or night. A slot the
/// daylight rule allows is never an evening slot.
/// </para>
/// <para>
/// A receiver that does not want to listen after its own sunset (a rig that retunes, or a web SDR)
/// adds <see cref="ListenerLocator"/>: an evening slot then runs for it only if it starts before
/// sunset there too. That is the receiver's own view and never goes in a directory.
/// </para>
/// </remarks>
public sealed class EveningRule : IEquatable<EveningRule>
{
    private readonly double? _listenerLatitude;
    private readonly double? _listenerLongitude;
    private readonly ConcurrentDictionary<DateOnly, SunTimes> _listenerSun = new();

    /// <summary>Creates a rule. Throws <see cref="ArgumentException"/> for a listener locator that is not one.</summary>
    public EveningRule(TimeOnly untilUtc, string? listenerLocator = null)
    {
        UntilUtc = untilUtc;
        if (listenerLocator is not null)
        {
            if (!Maidenhead.TryParse(listenerLocator, out double lat, out double lon))
            {
                throw new ArgumentException($"'{listenerLocator}' is not a 4 or 6 character Maidenhead locator such as JO67fm.", nameof(listenerLocator));
            }
            ListenerLocator = listenerLocator;
            _listenerLatitude = lat;
            _listenerLongitude = lon;
        }
    }

    /// <summary>The last evening slot starts before this time of day, UTC.</summary>
    public TimeOnly UntilUtc { get; }

    /// <summary>A receiver's own locator: evening slots that start after sunset there are left out. Null for the head end's own rule.</summary>
    public string? ListenerLocator { get; }

    /// <summary>The same rule, ending at sunset at <paramref name="locator"/> as well.</summary>
    public EveningRule EndingAtSunsetAt(string locator) => new(UntilUtc, locator);

    /// <summary>
    /// Whether a slot starting at <paramref name="slot"/> is an evening slot of a head end with the
    /// daylight rule <paramref name="daylight"/>: not in its daylight, and in some solar day's
    /// evening (the day before and after are checked too, as <see cref="DaylightRule.Allows"/> does).
    /// </summary>
    public bool Allows(DateTimeOffset slot, DaylightRule daylight)
    {
        ArgumentNullException.ThrowIfNull(daylight);
        if (daylight.Allows(slot))
        {
            return false;
        }
        var day = DateOnly.FromDateTime(slot.UtcDateTime);
        return InEvening(slot, day, daylight) || InEvening(slot, day.AddDays(-1), daylight) || InEvening(slot, day.AddDays(1), daylight);
    }

    private bool InEvening(DateTimeOffset slot, DateOnly day, DaylightRule daylight)
    {
        var sun = daylight.WindowOn(day).Sun;
        if (sun.Kind != SunDay.RisesAndSets)
        {
            return false;
        }
        var opens = sun.Sunset!.Value;
        var until = new DateTimeOffset(day.ToDateTime(UntilUtc, DateTimeKind.Utc));
        if (daylight.WindowOn(day.AddDays(1)).Sun is { Kind: SunDay.RisesAndSets, Sunrise: { } nextSunrise } && nextSunrise < until)
        {
            until = nextSunrise;
        }
        if (slot < opens || slot >= until)
        {
            return false;
        }
        if (_listenerLatitude is not double lat || _listenerLongitude is not double lon)
        {
            return true;
        }
        var here = _listenerSun.GetOrAdd(day, d => Solar.SunriseSunset(d, lat, lon));
        return here.Kind switch
        {
            SunDay.AlwaysUp => true,
            SunDay.AlwaysDown => false,
            _ => slot < here.Sunset!.Value,
        };
    }

    /// <summary>The rule in words: "from sunset until 22:00 UTC", with ", and before sunset at JO67fm" for a listener's.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"from sunset until {UntilUtc:HH:mm} UTC{(ListenerLocator is null ? "" : $", and only before sunset at {ListenerLocator}")}");

    /// <inheritdoc />
    public bool Equals(EveningRule? other) =>
        other is not null && UntilUtc == other.UntilUtc && string.Equals(ListenerLocator, other.ListenerLocator, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as EveningRule);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(UntilUtc, ListenerLocator?.ToUpperInvariant());

    /// <inheritdoc />
    public override string ToString() => Describe();
}

/// <summary>
/// When a head end's slots are: one starts at <see cref="Anchor"/> UTC and then every
/// <see cref="EveryMinutes"/>, round the clock, and with a <see cref="Daylight"/> rule only those
/// that start in its window run, and with an <see cref="Evening"/> rule as well those in its
/// evening. The same in the head end, in the directory it sends, and in the receiver.
/// </summary>
/// <param name="Anchor">One slot's start, UTC.</param>
/// <param name="EveryMinutes">Minutes from one slot to the next; it divides a day.</param>
/// <param name="Daylight">The daylight rule, or null for every slot.</param>
/// <param name="Evening">The evening slots after the daylight ones, or null for none. Ignored without a daylight rule, since every slot runs then.</param>
public sealed record SlotTimetable(TimeOnly Anchor, int EveryMinutes, DaylightRule? Daylight = null, EveningRule? Evening = null)
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

    /// <summary>Whether the slot starting at <paramref name="slot"/> runs, in daylight or in the evening: always, without a daylight rule.</summary>
    public bool IsActive(DateTimeOffset slot) => Daylight is null || Daylight.Allows(slot) || IsEvening(slot);

    /// <summary>Whether the slot starting at <paramref name="slot"/> is one of the evening slots (<see cref="Evening"/>), which never count against the daylight strategy.</summary>
    public bool IsEvening(DateTimeOffset slot) => Daylight is { } daylight && Evening is { } evening && evening.Allows(slot, daylight);

    /// <summary>The same timetable without its evening slots: what a receiver from before them follows. A method, so that a record's printing does not follow it round.</summary>
    public SlotTimetable WithoutEvening() => Evening is null ? this : this with { Evening = null };

    /// <summary>The slots that run on a UTC day, daylight and evening, earliest first.</summary>
    public IReadOnlyList<DateTimeOffset> ActiveSlotsOn(DateOnly day) =>
        Daylight is null ? SlotsOn(day) : [.. SlotsOn(day).Where(IsActive)];

    /// <summary>The evening slots on a UTC day, earliest first; none without an evening rule.</summary>
    public IReadOnlyList<DateTimeOffset> EveningSlotsOn(DateOnly day) =>
        Daylight is null || Evening is null ? [] : [.. SlotsOn(day).Where(IsEvening)];

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
    /// <c>slots=HH:mm/EVERY</c>; with a daylight rule, <c>daylight=LOCATOR/AFTER/BEFORE</c>; and
    /// with a measured window, <c>season=</c> and <see cref="SeasonalWindow.ToText"/>; and
    /// with evening slots as well, <c>evening=HH:mm</c>, the time the last evening slot starts
    /// before. Readers from before the evening slots ignore that field, as they ignore any they
    /// do not know, so they simply do not listen in the evening; readers from before the season
    /// likewise follow the <c>daylight=</c> offsets alone. A listener's own sunset
    /// (<see cref="EveningRule.ListenerLocator"/>) is never written.
    /// </summary>
    public IReadOnlyList<string> ToFields()
    {
        var fields = new List<string> { string.Create(CultureInfo.InvariantCulture, $"slots={Anchor:HH:mm}/{EveryMinutes}") };
        if (Daylight is { } d)
        {
            fields.Add(string.Create(CultureInfo.InvariantCulture, $"daylight={d.Locator}/{d.AfterSunriseMinutes}/{d.BeforeSunsetMinutes}"));
            if (d.Season is { } season)
            {
                fields.Add(SeasonalWindow.FieldName + season.ToText());
            }
            if (Evening is { } e)
            {
                fields.Add(string.Create(CultureInfo.InvariantCulture, $"{EveningField}{e.UntilUtc:HH:mm}"));
            }
        }
        return fields;
    }

    /// <summary>The evening slots' field name, with its equals sign.</summary>
    public const string EveningField = "evening=";

    /// <summary>
    /// Reads <see cref="ToFields"/>' fields from among others, which are ignored. Null when there
    /// is no <c>slots=</c> field or it or a <c>daylight=</c> field cannot be read, so a timetable
    /// a later head end writes differently is ignored rather than misread. An <c>evening=</c>
    /// field that cannot be read, or one without a daylight rule, is left out on its own: the
    /// daylight slots still stand, as for a receiver from before the evening slots. A
    /// <c>season=</c> field that cannot be read is likewise left out, and the offsets stand.
    /// </summary>
    public static SlotTimetable? FromFields(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        string? slots = null;
        string? daylight = null;
        string? evening = null;
        string? season = null;
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
            else if (field.StartsWith(EveningField, StringComparison.Ordinal))
            {
                evening ??= field[EveningField.Length..];
            }
            else if (field.StartsWith(SeasonalWindow.FieldName, StringComparison.Ordinal))
            {
                season ??= field[SeasonalWindow.FieldName.Length..];
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
                rule = new DaylightRule(d[0], after, before, SeasonalWindow.FromText(season));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        EveningRule? eveningRule = rule is not null && evening is not null
            && TimeOnly.TryParseExact(evening, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
            ? new EveningRule(until) : null;
        return new SlotTimetable(anchor, every, rule, eveningRule);
    }
}
