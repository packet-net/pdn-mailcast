using System.Globalization;

namespace Packet.Mailcast;

/// <summary>
/// When the band is open through the year, from measurement rather than from the sun (issue
/// #273): for each month, the time it opens and the time it closes, UTC, on the 15th. Days in
/// between are interpolated straight between the two nearest 15ths, to the minute. A slot runs
/// if it starts at or after the day's opening and ends, <see cref="SlotMinutes"/> later, at or
/// before the day's close.
/// </summary>
/// <remarks>
/// A close at or before its opening is the next day's: June's 07:36 to 00:00 runs to midnight.
/// The window is at most a day long.
/// </remarks>
public sealed class SeasonalWindow : IEquatable<SeasonalWindow>
{
    /// <summary>The longest a slot may be taken to last: twelve hours.</summary>
    public const int MostSlotMinutes = 720;

    /// <summary>The directory field's name, with its equals sign.</summary>
    public const string FieldName = "season=";

    private static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private readonly (int Opens, int Closes)[] _months;

    /// <summary>
    /// Creates a window from twelve months, January first, each its opening and closing time as
    /// minutes after midnight UTC. Throws <see cref="ArgumentException"/> for anything else.
    /// </summary>
    public SeasonalWindow(IReadOnlyList<(int Opens, int Closes)> months, int slotMinutes)
    {
        ArgumentNullException.ThrowIfNull(months);
        if (months.Count != 12)
        {
            throw new ArgumentException("A seasonal window needs twelve months, January first.", nameof(months));
        }
        if (slotMinutes is < 0 or > MostSlotMinutes)
        {
            throw new ArgumentException($"A slot's length must be 0 to {MostSlotMinutes} minutes.", nameof(slotMinutes));
        }
        _months = new (int, int)[12];
        for (int m = 0; m < 12; m++)
        {
            var (opens, closes) = months[m];
            if (opens is < 0 or >= 1440 || closes is < 0 or >= 1440)
            {
                throw new ArgumentException($"{MonthNames[m]}: a time must be 00:00 to 23:59.", nameof(months));
            }
            _months[m] = (opens, closes <= opens ? closes + 1440 : closes);
        }
        SlotMinutes = slotMinutes;
    }

    /// <summary>How long a slot is taken to last: the last one starts this long before the close.</summary>
    public int SlotMinutes { get; }

    /// <summary>Each month's opening and closing on the 15th, as minutes after that day's midnight UTC; a close past midnight is 1440 or more.</summary>
    public IReadOnlyList<(int Opens, int Closes)> Months => _months;

    /// <summary>
    /// The band's opening and the latest a slot may start on <paramref name="day"/>, as minutes
    /// after its midnight UTC (the latter 1440 or more past midnight), or null when no slot fits.
    /// </summary>
    public (int Opens, int LastStart)? MinutesOn(DateOnly day)
    {
        var before = day.Day >= 15 ? new DateOnly(day.Year, day.Month, 15) : new DateOnly(day.Year, day.Month, 15).AddMonths(-1);
        var after = before.AddMonths(1);
        int span = after.DayNumber - before.DayNumber;
        int k = day.DayNumber - before.DayNumber;
        var (o0, c0) = _months[before.Month - 1];
        var (o1, c1) = _months[after.Month - 1];
        int opens = o0 + Step(o1 - o0, k, span);
        int closes = c0 + Step(c1 - c0, k, span);
        int last = closes - SlotMinutes;
        return last < opens ? null : (opens, last);
    }

    private static int Step(int change, int k, int span) => (int)Math.Round((double)change * k / span, MidpointRounding.AwayFromZero);

    /// <summary>The window as the directory carries it, after <see cref="FieldName"/>: <c>10/0830-1718/0802-1839/...</c>, the slot's length then the months from January.</summary>
    public string ToText() => SlotMinutes.ToString(CultureInfo.InvariantCulture) + "/"
        + string.Join('/', _months.Select(m => Hhmm(m.Opens, "") + "-" + Hhmm(m.Closes, "")));

    /// <summary>Reads <see cref="ToText"/>, or null if it cannot be read.</summary>
    public static SeasonalWindow? FromText(string? text)
    {
        var p = text?.Split('/');
        if (p is not { Length: 13 } || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out int slot) || slot > MostSlotMinutes)
        {
            return null;
        }
        var months = new List<(int, int)>();
        foreach (string month in p[1..])
        {
            var t = month.Split('-');
            if (t.Length != 2 || Minutes(t[0], "HHmm") is not int opens || Minutes(t[1], "HHmm") is not int closes)
            {
                return null;
            }
            months.Add((opens, closes));
        }
        return new SeasonalWindow(months, slot);
    }

    /// <summary>
    /// Reads a configuration file's twelve months, January first, each <c>"08:30-17:18"</c>
    /// (UTC). Returns null and says what is wrong in <paramref name="problem"/> if they cannot be read.
    /// </summary>
    public static SeasonalWindow? FromSettings(IReadOnlyList<string>? months, int slotMinutes, out string? problem)
    {
        if (months is not { Count: 12 })
        {
            problem = "\"season\" must list twelve months, January first, each opening and closing time UTC such as \"08:30-17:18\"";
            return null;
        }
        var read = new List<(int, int)>();
        for (int m = 0; m < 12; m++)
        {
            var t = (months[m] ?? "").Split('-');
            if (t.Length != 2 || Minutes(t[0].Trim(), "HH:mm") is not int opens || Minutes(t[1].Trim(), "HH:mm") is not int closes)
            {
                problem = $"\"season\": {MonthNames[m]} '{Printable(months[m] ?? "")}' is not an opening and closing time UTC such as \"08:30-17:18\"";
                return null;
            }
            read.Add((opens, closes));
        }
        if (slotMinutes is < 0 or > MostSlotMinutes)
        {
            problem = $"a slot's length must be 0 to {MostSlotMinutes} minutes for \"season\"";
            return null;
        }
        problem = null;
        return new SeasonalWindow(read, slotMinutes);
    }

    private static string Printable(string text) => new([.. text.Select(c => c is >= ' ' and <= '~' ? c : '?')]);

    private static int? Minutes(string text, string format) =>
        TimeOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? (t.Hour * 60) + t.Minute : null;

    private static string Hhmm(int minutes, string separator)
    {
        int m = minutes % 1440;
        return string.Create(CultureInfo.InvariantCulture, $"{m / 60:00}{separator}{m % 60:00}");
    }

    /// <summary>The window in words: "by month, UTC, each slot ending by the close (10 minutes): Jan 08:30-17:18, Feb ...".</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"by month, UTC, each slot ending by the close ({SlotMinutes} minutes): {string.Join(", ", _months.Select((m, i) => $"{MonthNames[i]} {Hhmm(m.Opens, ":")}-{Hhmm(m.Closes, ":")}"))}");

    /// <inheritdoc />
    public bool Equals(SeasonalWindow? other) => other is not null && SlotMinutes == other.SlotMinutes && _months.SequenceEqual(other._months);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SeasonalWindow);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SlotMinutes);
        foreach (var m in _months)
        {
            hash.Add(m);
        }
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Describe();
}
